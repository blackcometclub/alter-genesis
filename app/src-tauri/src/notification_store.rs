use crate::notification::NotificationCard;
use serde::{Deserialize, Serialize};
use std::{
    fs::{self, File, OpenOptions},
    io::Write,
    path::PathBuf,
    sync::atomic::{AtomicU64, Ordering},
    time::{SystemTime, UNIX_EPOCH},
};

static WRITE_SEQUENCE: AtomicU64 = AtomicU64::new(0);

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct SavedNotification {
    pub card: NotificationCard,
    pub snooze_until_unix_ms: Option<u64>,
}

#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct Document {
    version: u32,
    notification: Option<SavedNotification>,
}

pub struct NotificationStore {
    path: PathBuf,
    // A separate, stable file remains locked while notifications.json is replaced.
    _lock: Option<File>,
    pub error: Option<String>,
}

impl NotificationStore {
    pub fn open(path: PathBuf) -> Self {
        let mut store = Self {
            path,
            _lock: None,
            error: None,
        };
        let lock = (|| {
            if let Some(parent) = store.path.parent() {
                fs::create_dir_all(parent).map_err(|_| "notification_io_error")?;
            }
            let file = OpenOptions::new()
                .create(true)
                .truncate(false)
                .read(true)
                .write(true)
                .open(store.path.with_extension("lock"))
                .map_err(|_| "notification_io_error")?;
            file.try_lock().map_err(|_| "notification_in_use")?;
            Ok::<File, &str>(file)
        })();
        match lock {
            Ok(file) => store._lock = Some(file),
            Err(error) => store.error = Some(error.to_owned()),
        }
        store
    }

    // Missing document is distinct from a saved, explicitly empty notification.
    pub fn load(&mut self) -> Result<Option<Option<SavedNotification>>, String> {
        if let Some(error) = &self.error {
            return Err(error.clone());
        }
        let result = (|| {
            let bytes = match fs::read(&self.path) {
                Ok(bytes) => bytes,
                Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
                Err(_) => return Err("notification_io_error".to_owned()),
            };
            let value: serde_json::Value =
                serde_json::from_slice(&bytes).map_err(|_| "notification_protected")?;
            if value.get("version").and_then(|v| v.as_u64()) != Some(1) {
                return Err("notification_protected".to_owned());
            }
            // An absent notification field must not be mistaken for a dismissed card.
            if value.get("notification").is_none() {
                return Err("notification_protected".to_owned());
            }
            if let Some(record) = value.get("notification").filter(|record| !record.is_null())
                && record.get("snooze_until_unix_ms").is_none()
            {
                return Err("notification_protected".to_owned());
            }
            let document: Document =
                serde_json::from_value(value).map_err(|_| "notification_protected")?;
            if let Some(record) = &document.notification {
                let card = &record.card;
                let color = card.calendar_color.as_bytes();
                if card.id.trim().is_empty()
                    || card.id.len() > 1024
                    || card.title.trim().is_empty()
                    || card.title.len() > 16384
                    || card.start_time.trim().is_empty()
                    || card.start_time.len() > 128
                    || color.len() != 7
                    || color[0] != b'#'
                    || !color[1..].iter().all(u8::is_ascii_hexdigit)
                    || record
                        .snooze_until_unix_ms
                        .is_some_and(|time| time > 253_402_300_799_999)
                {
                    return Err("notification_protected".to_owned());
                }
            }
            Ok(Some(document.notification))
        })();
        if let Err(error) = &result {
            self.error = Some(error.clone());
        }
        result
    }

    pub fn save(&self, notification: Option<SavedNotification>) -> Result<(), String> {
        if let Some(error) = &self.error {
            return Err(error.clone());
        }
        let bytes = serde_json::to_vec_pretty(&Document {
            version: 1,
            notification,
        })
        .map_err(|_| "notification_io_error")?;
        let stamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_err(|_| "notification_invalid_time")?
            .as_nanos();
        let sequence = WRITE_SEQUENCE.fetch_add(1, Ordering::Relaxed);
        let temporary = self.path.with_extension(format!(
            "json.{}.{stamp}.{sequence}.tmp",
            std::process::id()
        ));
        let mut created = false;
        let result = (|| {
            // Do not overwrite a temporary file left by an interrupted write.
            let mut file = OpenOptions::new()
                .create_new(true)
                .write(true)
                .open(&temporary)?;
            created = true;
            file.write_all(&bytes)?;
            file.sync_all()?;
            drop(file);
            fs::rename(&temporary, &self.path)
        })();
        if result.is_err() && created {
            let _ = fs::remove_file(&temporary);
        }
        result.map_err(|_| "notification_io_error".to_owned())
    }
}
