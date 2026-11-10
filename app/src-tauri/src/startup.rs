use std::sync::Mutex;
use tauri::WebviewWindow;

static REGISTRATION: Mutex<()> = Mutex::new(());

pub fn requested(args: impl IntoIterator<Item = String>) -> bool {
    args.into_iter().any(|arg| arg == "--startup")
}

fn check_window(window: &WebviewWindow) -> Result<(), String> {
    if window.label() == "main" {
        Ok(())
    } else {
        Err("startup_window_required".into())
    }
}

#[tauri::command]
pub fn load_startup(window: WebviewWindow) -> Result<bool, String> {
    check_window(&window)?;
    let _guard = REGISTRATION.lock().map_err(|_| "startup_io_error")?;
    platform::registration(None)
}

#[tauri::command]
pub fn set_startup(window: WebviewWindow, enabled: bool) -> Result<bool, String> {
    check_window(&window)?;
    let _guard = REGISTRATION.lock().map_err(|_| "startup_io_error")?;
    platform::registration(Some(enabled))
}

#[cfg(not(windows))]
mod platform {
    pub fn registration(_: Option<bool>) -> Result<bool, String> {
        Err("startup_unavailable".into())
    }
}

#[cfg(windows)]
mod platform {
    use std::{
        fs,
        os::windows::ffi::OsStrExt,
        path::{Path, PathBuf},
    };
    use windows::{
        Win32::{
            Foundation::RPC_E_CHANGED_MODE,
            Storage::FileSystem::{MOVE_FILE_FLAGS, MoveFileExW},
            System::Com::{
                CLSCTX_INPROC_SERVER, COINIT_APARTMENTTHREADED, CoCreateInstance, CoInitializeEx,
                CoTaskMemFree, CoUninitialize, IPersistFile, STGM_READ,
            },
            UI::Shell::{
                FOLDERID_Startup, IShellLinkW, KF_FLAG_DEFAULT, SHGetKnownFolderPath, ShellLink,
            },
        },
        core::{Interface, PCWSTR},
    };

    const NAME: &str = "ALTER GENESIS (Tauri).lnk";
    const ARGS: &str = "--startup";
    const IO: &str = "startup_io_error";
    const CONFLICT: &str = "startup_conflict";

    struct Apartment(bool);
    impl Apartment {
        fn enter() -> Result<Self, String> {
            // Use an existing apartment if this thread was already initialized by Tauri/WebView2.
            let result = unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) };
            if result.is_ok() {
                Ok(Self(true))
            } else if result == RPC_E_CHANGED_MODE {
                Ok(Self(false))
            } else {
                Err(IO.into())
            }
        }
    }
    impl Drop for Apartment {
        fn drop(&mut self) {
            if self.0 {
                unsafe { CoUninitialize() };
            }
        }
    }

    fn wide(path: &Path) -> Vec<u16> {
        path.as_os_str().encode_wide().chain(Some(0)).collect()
    }

    fn startup_path() -> Result<PathBuf, String> {
        if let Some(folder) = std::env::var_os("ALTER_GENESIS_STARTUP_DIR") {
            // A development override is never allowed to silently fall back to the real Startup.
            let folder = PathBuf::from(folder);
            if !folder.is_absolute() || !folder.is_dir() {
                return Err(IO.into());
            }
            return Ok(folder.join(NAME));
        }
        if std::env::var_os("ALTER_GENESIS_SETTINGS_PATH").is_some() {
            return Err("startup_test_folder_required".into());
        }
        // Windows owns and may redirect this per-user known folder; don't reconstruct APPDATA.
        let value = unsafe { SHGetKnownFolderPath(&FOLDERID_Startup, KF_FLAG_DEFAULT, None) }
            .map_err(|_| IO)?;
        let result = unsafe { value.to_string() }
            .map(PathBuf::from)
            .map_err(|_| IO.to_string());
        unsafe { CoTaskMemFree(Some(value.0.cast())) };
        result.map(|folder| folder.join(NAME))
    }

    fn shell_link() -> Result<IShellLinkW, String> {
        unsafe { CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER) }.map_err(|_| IO.into())
    }

    fn persisted(link: &IShellLinkW) -> Result<IPersistFile, String> {
        link.cast().map_err(|_| IO.into())
    }

    fn matches(path: &Path, executable: &Path) -> Result<bool, String> {
        let metadata = match fs::symlink_metadata(path) {
            Ok(value) => value,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(false),
            Err(_) => return Err(IO.into()),
        };
        if !metadata.is_file() || metadata.file_type().is_symlink() {
            return Err(CONFLICT.into());
        }
        let link = shell_link()?;
        let filename = wide(path);
        unsafe { persisted(&link)?.Load(PCWSTR(filename.as_ptr()), STGM_READ) }
            .map_err(|_| CONFLICT)?;
        let mut target = [0u16; 32768];
        let mut args = [0u16; 32768];
        // Do not Resolve(): reading a link must not search the network or repair another target.
        unsafe {
            link.GetPath(&mut target, std::ptr::null_mut(), 0)
                .map_err(|_| CONFLICT)?;
            link.GetArguments(&mut args).map_err(|_| CONFLICT)?;
        }
        let target_end = target.iter().position(|v| *v == 0).ok_or(CONFLICT)?;
        let args_end = args.iter().position(|v| *v == 0).ok_or(CONFLICT)?;
        let target = String::from_utf16(&target[..target_end]).map_err(|_| CONFLICT)?;
        let args = String::from_utf16(&args[..args_end]).map_err(|_| CONFLICT)?;
        if target.eq_ignore_ascii_case(&executable.to_string_lossy()) && args == ARGS {
            Ok(true)
        } else {
            Err(CONFLICT.into())
        }
    }

    struct Pending(PathBuf);
    impl Drop for Pending {
        fn drop(&mut self) {
            let _ = fs::remove_file(&self.0);
        }
    }

    fn create(path: &Path, executable: &Path) -> Result<(), String> {
        let parent = path.parent().ok_or(IO)?;
        fs::create_dir_all(parent).map_err(|_| IO)?;
        let nonce = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_err(|_| IO)?
            .as_nanos();
        let temporary_path = parent.join(format!(
            "alter-genesis-{}-{nonce}.pending",
            std::process::id()
        ));
        // Reserve a unique temporary file; never replace an unrelated shortcut, even in a race.
        fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temporary_path)
            .map_err(|_| IO)?;
        let temporary = Pending(temporary_path);
        let link = shell_link()?;
        let target = wide(executable);
        let workdir = wide(executable.parent().ok_or(IO)?);
        let args = wide(Path::new(ARGS));
        unsafe {
            link.SetPath(PCWSTR(target.as_ptr())).map_err(|_| IO)?;
            link.SetArguments(PCWSTR(args.as_ptr())).map_err(|_| IO)?;
            link.SetWorkingDirectory(PCWSTR(workdir.as_ptr()))
                .map_err(|_| IO)?;
            link.SetIconLocation(PCWSTR(target.as_ptr()), 0)
                .map_err(|_| IO)?;
            let temporary_path = wide(&temporary.0);
            persisted(&link)?
                .Save(PCWSTR(temporary_path.as_ptr()), true)
                .map_err(|_| IO)?;
            let destination = wide(path);
            // No REPLACE_EXISTING flag: a new file appearing during creation is preserved.
            MoveFileExW(
                PCWSTR(temporary_path.as_ptr()),
                PCWSTR(destination.as_ptr()),
                MOVE_FILE_FLAGS(0),
            )
            .map_err(|_| IO)?;
        }
        Ok(())
    }

    pub fn registration(change: Option<bool>) -> Result<bool, String> {
        let _apartment = Apartment::enter()?;
        let path = startup_path()?;
        let executable = std::env::current_exe().map_err(|_| IO)?;
        update(&path, &executable, change)
    }

    fn update(path: &Path, executable: &Path, change: Option<bool>) -> Result<bool, String> {
        let enabled = matches(path, executable)?;
        match change {
            Some(true) if !enabled => create(path, executable)?,
            Some(false) if enabled => fs::remove_file(path).map_err(|_| IO)?,
            _ => (),
        }
        matches(path, executable)
    }

    #[cfg(test)]
    mod tests {
        use super::*;
        #[test]
        fn shortcut_round_trip_and_collision_protection() {
            let _apartment = Apartment::enter().unwrap();
            let folder =
                std::env::temp_dir().join(format!("alter-genesis-startup-{}", std::process::id()));
            fs::create_dir_all(&folder).unwrap();
            let path = folder.join(NAME);
            let exe = std::env::current_exe().unwrap();
            assert!(!update(&path, &exe, None).unwrap());
            assert!(update(&path, &exe, Some(true)).unwrap());
            let bytes = fs::read(&path).unwrap();
            assert!(update(&path, &exe, Some(true)).unwrap());
            assert_eq!(bytes, fs::read(&path).unwrap());
            assert_eq!(
                update(&path, &folder.join("other.exe"), Some(false)),
                Err(CONFLICT.into())
            );
            assert_eq!(bytes, fs::read(&path).unwrap());
            assert!(!update(&path, &exe, Some(false)).unwrap());
            fs::write(&path, b"unrelated data").unwrap();
            assert_eq!(update(&path, &exe, Some(true)), Err(CONFLICT.into()));
            assert_eq!(update(&path, &exe, Some(false)), Err(CONFLICT.into()));
            assert_eq!(fs::read(&path).unwrap(), b"unrelated data");
            fs::remove_file(&path).unwrap();
            fs::remove_dir(&folder).unwrap();
        }
    }
}

#[cfg(test)]
mod tests {
    #[test]
    fn only_exact_startup_argument_suppresses_settings() {
        assert!(super::requested(["--startup".into()]));
        assert!(!super::requested(["--startup-other".into()]));
    }
}
