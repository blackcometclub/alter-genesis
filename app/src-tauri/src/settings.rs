use serde::{Deserialize, Serialize};
use std::{
    fs::{self, OpenOptions},
    io::Write,
    path::{Path, PathBuf},
    sync::Mutex,
};

const SETTINGS_FORMAT_VERSION: u32 = 1;

#[derive(Clone, Debug, Default, Deserialize, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Language {
    #[default]
    System,
    English,
    Japanese,
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum CardSize {
    #[default]
    Small,
    Large,
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum NotificationSound {
    None,
    #[default]
    Koron,
    Kirari,
    Pikon,
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum SoundRepeat {
    #[default]
    None,
    OneMinute,
    ThreeMinutes,
    FiveMinutes,
    TenMinutes,
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq, Eq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct AppSettings {
    pub language: Language,
    pub card_size: CardSize,
    pub notification_sound: NotificationSound,
    pub sound_repeat: SoundRepeat,
}

#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct SettingsDocument {
    version: u32,
    settings: AppSettings,
}

#[derive(Debug)]
enum SettingsStoreError {
    Io,
    Malformed,
    UnsupportedVersion,
    Protected,
}

impl SettingsStoreError {
    fn code(&self) -> &'static str {
        match self {
            Self::Io => "settings_io_error",
            Self::Malformed => "settings_malformed",
            Self::UnsupportedVersion => "settings_unsupported_version",
            Self::Protected => "settings_protected",
        }
    }
}

impl From<std::io::Error> for SettingsStoreError {
    fn from(_: std::io::Error) -> Self {
        Self::Io
    }
}

struct SettingsStore {
    path: PathBuf,
    protected: bool,
}

impl SettingsStore {
    fn new(path: PathBuf) -> Self {
        Self {
            path,
            protected: false,
        }
    }

    fn load(&mut self) -> Result<AppSettings, SettingsStoreError> {
        if self.protected {
            return Err(SettingsStoreError::Protected);
        }

        let bytes = match fs::read(&self.path) {
            Ok(bytes) => bytes,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return Ok(AppSettings::default());
            }
            Err(error) => return Err(error.into()),
        };

        let document: SettingsDocument = match serde_json::from_slice(&bytes) {
            Ok(document) => document,
            Err(_) => {
                self.protected = true;
                return Err(SettingsStoreError::Malformed);
            }
        };

        if document.version != SETTINGS_FORMAT_VERSION {
            self.protected = true;
            return Err(SettingsStoreError::UnsupportedVersion);
        }

        Ok(document.settings)
    }

    fn save(&mut self, settings: AppSettings) -> Result<AppSettings, SettingsStoreError> {
        if self.protected {
            return Err(SettingsStoreError::Protected);
        }

        let parent = self.path.parent().unwrap_or_else(|| Path::new("."));
        fs::create_dir_all(parent)?;

        let document = SettingsDocument {
            version: SETTINGS_FORMAT_VERSION,
            settings: settings.clone(),
        };
        let mut bytes = serde_json::to_vec_pretty(&document)
            .expect("serializing AppSettings with fixed fields cannot fail");
        bytes.push(b'\n');

        let temporary_path = self.path.with_extension("json.tmp");
        let write_result = (|| -> Result<(), SettingsStoreError> {
            let mut file = OpenOptions::new()
                .create(true)
                .truncate(true)
                .write(true)
                .open(&temporary_path)?;
            file.write_all(&bytes)?;
            file.sync_all()?;
            fs::rename(&temporary_path, &self.path)?;
            Ok(())
        })();

        if write_result.is_err() {
            let _ = fs::remove_file(&temporary_path);
        }
        write_result?;

        Ok(settings)
    }
}

pub struct SettingsState {
    store: Mutex<SettingsStore>,
}

impl SettingsState {
    pub fn new(path: PathBuf) -> Self {
        Self {
            store: Mutex::new(SettingsStore::new(path)),
        }
    }

    pub fn load(&self) -> Result<AppSettings, String> {
        self.store
            .lock()
            .map_err(|_| "settings_state_unavailable".to_owned())?
            .load()
            .map_err(|error| error.code().to_owned())
    }

    pub fn save(&self, settings: AppSettings) -> Result<AppSettings, String> {
        self.store
            .lock()
            .map_err(|_| "settings_state_unavailable".to_owned())?
            .save(settings)
            .map_err(|error| error.code().to_owned())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{
        sync::atomic::{AtomicU64, Ordering},
        time::{SystemTime, UNIX_EPOCH},
    };

    static TEST_SEQUENCE: AtomicU64 = AtomicU64::new(0);

    fn isolated_path(test_name: &str) -> PathBuf {
        let timestamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("clock should be after the Unix epoch")
            .as_nanos();
        let sequence = TEST_SEQUENCE.fetch_add(1, Ordering::Relaxed);
        std::env::temp_dir()
            .join(format!(
                "alter-genesis-settings-{}-{timestamp}-{sequence}-{test_name}",
                std::process::id()
            ))
            .join("settings.json")
    }

    fn cleanup(path: &Path) {
        if let Some(directory) = path.parent() {
            let _ = fs::remove_dir_all(directory);
        }
    }

    #[test]
    fn missing_file_uses_product_defaults() {
        let path = isolated_path("defaults");
        let mut store = SettingsStore::new(path.clone());

        assert_eq!(store.load().unwrap(), AppSettings::default());
        assert!(!path.exists());

        cleanup(&path);
    }

    #[test]
    fn saved_settings_round_trip_and_replace_the_previous_value() {
        let path = isolated_path("round-trip");
        let mut store = SettingsStore::new(path.clone());
        let first = AppSettings {
            language: Language::English,
            card_size: CardSize::Large,
            notification_sound: NotificationSound::Kirari,
            sound_repeat: SoundRepeat::ThreeMinutes,
        };
        let second = AppSettings {
            language: Language::Japanese,
            card_size: CardSize::Small,
            notification_sound: NotificationSound::Pikon,
            sound_repeat: SoundRepeat::TenMinutes,
        };

        assert_eq!(store.save(first).unwrap().language, Language::English);
        assert_eq!(store.save(second.clone()).unwrap(), second);

        let mut reopened = SettingsStore::new(path.clone());
        assert_eq!(reopened.load().unwrap(), second);
        assert!(!path.with_extension("json.tmp").exists());

        cleanup(&path);
    }

    #[test]
    fn malformed_file_is_not_overwritten_in_the_same_run() {
        let path = isolated_path("malformed");
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(&path, b"{ definitely not json").unwrap();
        let original = fs::read(&path).unwrap();
        let mut store = SettingsStore::new(path.clone());

        assert!(matches!(store.load(), Err(SettingsStoreError::Malformed)));
        assert!(matches!(
            store.save(AppSettings::default()),
            Err(SettingsStoreError::Protected)
        ));
        assert_eq!(fs::read(&path).unwrap(), original);

        cleanup(&path);
    }

    #[test]
    fn newer_format_is_protected_instead_of_downgraded() {
        let path = isolated_path("newer-version");
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        let document = serde_json::json!({
            "version": SETTINGS_FORMAT_VERSION + 1,
            "settings": AppSettings::default(),
        });
        fs::write(&path, serde_json::to_vec_pretty(&document).unwrap()).unwrap();
        let original = fs::read(&path).unwrap();
        let mut store = SettingsStore::new(path.clone());

        assert!(matches!(
            store.load(),
            Err(SettingsStoreError::UnsupportedVersion)
        ));
        assert!(matches!(
            store.save(AppSettings::default()),
            Err(SettingsStoreError::Protected)
        ));
        assert_eq!(fs::read(&path).unwrap(), original);

        cleanup(&path);
    }

    #[test]
    fn unknown_field_is_protected_instead_of_discarded() {
        let path = isolated_path("unknown-field");
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        let document = serde_json::json!({
            "version": SETTINGS_FORMAT_VERSION,
            "settings": {
                "language": "system",
                "card_size": "small",
                "notification_sound": "koron",
                "sound_repeat": "none",
                "future_setting": true,
            },
        });
        fs::write(&path, serde_json::to_vec_pretty(&document).unwrap()).unwrap();
        let original = fs::read(&path).unwrap();
        let mut store = SettingsStore::new(path.clone());

        assert!(matches!(store.load(), Err(SettingsStoreError::Malformed)));
        assert!(matches!(
            store.save(AppSettings::default()),
            Err(SettingsStoreError::Protected)
        ));
        assert_eq!(fs::read(&path).unwrap(), original);

        cleanup(&path);
    }
}
