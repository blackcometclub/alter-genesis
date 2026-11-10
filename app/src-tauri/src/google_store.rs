use serde::{Serialize, de::DeserializeOwned};
use std::{
    fs::{self, File, OpenOptions},
    io::Write,
    path::PathBuf,
    sync::atomic::{AtomicU64, Ordering},
};
use zeroize::{Zeroize, Zeroizing};

static SEQUENCE: AtomicU64 = AtomicU64::new(0);
const MAX_BYTES: u64 = 8 * 1024 * 1024;

#[cfg(windows)]
fn crypt(bytes: &[u8], encrypt: bool) -> Result<Vec<u8>, String> {
    use windows_sys::Win32::{
        Foundation::LocalFree,
        Security::Cryptography::{
            CRYPT_INTEGER_BLOB, CRYPTPROTECT_UI_FORBIDDEN, CryptProtectData, CryptUnprotectData,
        },
    };
    let size = u32::try_from(bytes.len()).map_err(|_| "google_store_error")?;
    let input = CRYPT_INTEGER_BLOB {
        cbData: size,
        pbData: bytes.as_ptr().cast_mut(),
    };
    let entropy_bytes = b"TINMOON.AlterGenesis.Tauri.Google.v1";
    let entropy = CRYPT_INTEGER_BLOB {
        cbData: entropy_bytes.len() as u32,
        pbData: entropy_bytes.as_ptr().cast_mut(),
    };
    let mut output = CRYPT_INTEGER_BLOB {
        cbData: 0,
        pbData: std::ptr::null_mut(),
    };
    // Inputs remain alive and DPAPI treats these input blobs as read-only. No machine-wide key or UI.
    let success = unsafe {
        if encrypt {
            CryptProtectData(
                &input,
                std::ptr::null(),
                &entropy,
                std::ptr::null(),
                std::ptr::null(),
                CRYPTPROTECT_UI_FORBIDDEN,
                &mut output,
            )
        } else {
            CryptUnprotectData(
                &input,
                std::ptr::null_mut(),
                &entropy,
                std::ptr::null(),
                std::ptr::null(),
                CRYPTPROTECT_UI_FORBIDDEN,
                &mut output,
            )
        }
    };
    if success == 0 {
        return Err("google_store_protected".into());
    }
    if output.pbData.is_null() || output.cbData == 0 || u64::from(output.cbData) > MAX_BYTES {
        if !output.pbData.is_null() {
            unsafe {
                LocalFree(output.pbData.cast());
            }
        }
        return Err("google_store_protected".into());
    }
    // A successful DPAPI call allocates cbData bytes through LocalAlloc.
    let result =
        unsafe { std::slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec() };
    unsafe {
        std::slice::from_raw_parts_mut(output.pbData, output.cbData as usize).zeroize();
        LocalFree(output.pbData.cast());
    }
    Ok(result)
}

#[cfg(not(windows))]
fn crypt(_: &[u8], _: bool) -> Result<Vec<u8>, String> {
    Err("google_store_unsupported".into())
}

pub struct GoogleStore {
    path: PathBuf,
    lock: Option<File>,
    error: Option<String>,
}

impl GoogleStore {
    pub fn new(path: PathBuf) -> Self {
        let mut store = Self {
            path,
            lock: None,
            error: None,
        };
        let result = (|| {
            if let Some(parent) = store.path.parent() {
                fs::create_dir_all(parent).map_err(|_| "google_store_error")?;
            }
            let lock = OpenOptions::new()
                .create(true)
                .truncate(false)
                .read(true)
                .write(true)
                .open(store.path.with_extension("lock"))
                .map_err(|_| "google_store_error")?;
            lock.try_lock().map_err(|_| "google_in_use")?;
            Ok::<_, &str>(lock)
        })();
        match result {
            Ok(lock) => store.lock = Some(lock),
            Err(error) => store.error = Some(error.into()),
        }
        store
    }

    pub fn load<T: DeserializeOwned>(&mut self) -> Result<Option<T>, String> {
        if let Some(error) = &self.error {
            return Err(error.clone());
        }
        let result: Result<Option<T>, String> = (|| {
            let metadata = match fs::metadata(&self.path) {
                Ok(value) => value,
                Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
                Err(_) => return Err("google_store_error".into()),
            };
            if !metadata.is_file() || metadata.len() > MAX_BYTES {
                return Err("google_store_protected".into());
            }
            let bytes = fs::read(&self.path).map_err(|_| "google_store_error")?;
            let plain = Zeroizing::new(crypt(&bytes, false)?);
            let value: serde_json::Value =
                serde_json::from_slice(&plain).map_err(|_| "google_store_protected")?;
            if value.get("version").and_then(|v| v.as_u64()) != Some(1) {
                return Err("google_store_protected".into());
            }
            serde_json::from_value(value)
                .map(Some)
                .map_err(|_| "google_store_protected".into())
        })();
        if let Err(error) = &result {
            self.error = Some(error.clone());
        }
        result
    }

    pub fn save<T: Serialize>(&self, document: &T) -> Result<(), String> {
        if let Some(error) = &self.error {
            return Err(error.clone());
        }
        let plain = Zeroizing::new(serde_json::to_vec(document).map_err(|_| "google_store_error")?);
        if plain.len() as u64 > MAX_BYTES {
            return Err("google_store_error".into());
        }
        let encrypted = crypt(&plain, true)?;
        if encrypted.len() as u64 > MAX_BYTES {
            return Err("google_store_error".into());
        }
        let number = SEQUENCE.fetch_add(1, Ordering::Relaxed);
        let temporary = self
            .path
            .with_extension(format!("{}.{}.tmp", std::process::id(), number));
        let mut created = false;
        let result = (|| {
            let mut file = OpenOptions::new()
                .create_new(true)
                .write(true)
                .open(&temporary)?;
            created = true;
            file.write_all(&encrypted)?;
            file.sync_all()?;
            drop(file);
            fs::rename(&temporary, &self.path)
        })();
        if result.is_err() && created {
            let _ = fs::remove_file(&temporary);
        }
        result.map_err(|_| "google_store_error".into())
    }

    // Only the user's explicit disconnect may clear a protected or unreadable local record.
    pub fn clear(&mut self) -> Result<(), String> {
        if self.lock.is_none() {
            return Err("google_in_use".into());
        }
        match fs::remove_file(&self.path) {
            Ok(()) => (),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => (),
            Err(_) => return Err("google_store_error".into()),
        }
        self.error = None;
        Ok(())
    }
}

#[cfg(all(test, windows))]
mod tests {
    use super::*;
    struct Fixture {
        directory: PathBuf,
        root: PathBuf,
    }
    impl Fixture {
        fn new() -> Self {
            let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
                .join("../../artifacts/tauri-google-tests");
            fs::create_dir_all(&root).unwrap();
            let root = fs::canonicalize(root).unwrap();
            let id = SEQUENCE.fetch_add(1, Ordering::Relaxed);
            let directory = root.join(format!("{}-{id}", std::process::id()));
            fs::create_dir(&directory).unwrap();
            Self { directory, root }
        }
        fn path(&self) -> PathBuf {
            self.directory.join("google.dpapi")
        }
    }
    impl Drop for Fixture {
        fn drop(&mut self) {
            if let Ok(resolved) = fs::canonicalize(&self.directory) {
                assert_eq!(resolved.parent(), Some(self.root.as_path()));
                let _ = fs::remove_dir_all(resolved);
            }
        }
    }
    #[test]
    fn encrypted_round_trip_replace_and_local_disconnect() {
        let f = Fixture::new();
        let mut store = GoogleStore::new(f.path());
        assert!(store.load::<serde_json::Value>().unwrap().is_none());
        let doc = serde_json::json!({"version":1,"token":"fixture-token-never-real","calendars":["fixture"]});
        store.save(&doc).unwrap();
        let bytes = fs::read(f.path()).unwrap();
        assert!(!bytes.windows(10).any(|w| w == b"fixture-to"));
        assert_eq!(store.load::<serde_json::Value>().unwrap(), Some(doc));
        let next = serde_json::json!({"version":1,"token":"changed"});
        store.save(&next).unwrap();
        drop(store);
        let mut store = GoogleStore::new(f.path());
        assert_eq!(store.load::<serde_json::Value>().unwrap(), Some(next));
        store.clear().unwrap();
        assert!(!f.path().exists());
    }
    #[test]
    fn corrupt_and_future_records_are_protected_until_explicit_clear() {
        for bytes in [
            b"not encrypted".to_vec(),
            crypt(b"", true).unwrap(),
            crypt(br#"{"version":2}"#, true).unwrap(),
        ] {
            let f = Fixture::new();
            fs::write(f.path(), &bytes).unwrap();
            let mut store = GoogleStore::new(f.path());
            assert!(store.load::<serde_json::Value>().is_err());
            assert!(store.save(&serde_json::json!({"version":1})).is_err());
            assert_eq!(fs::read(f.path()).unwrap(), bytes);
            store.clear().unwrap();
            assert!(!f.path().exists());
        }
    }
    #[test]
    fn another_process_owner_cannot_read_write_or_clear_the_store() {
        let f = Fixture::new();
        let first = GoogleStore::new(f.path());
        let mut second = GoogleStore::new(f.path());
        assert_eq!(
            second.load::<serde_json::Value>().unwrap_err(),
            "google_in_use"
        );
        assert!(second.save(&serde_json::json!({"version":1})).is_err());
        assert!(second.clear().is_err());
        drop(second);
        drop(first);
        assert!(
            GoogleStore::new(f.path())
                .load::<serde_json::Value>()
                .unwrap()
                .is_none()
        );
    }
}
