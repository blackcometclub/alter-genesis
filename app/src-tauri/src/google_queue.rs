use crate::{
    google_events::{DAY, Reminder},
    google_store::GoogleStore,
    notification::NotificationCard,
    settings::CardSize,
};
use serde::{Deserialize, Serialize};
use std::{
    collections::{HashMap, HashSet},
    path::PathBuf,
    sync::Mutex,
};

#[derive(Clone, Deserialize, PartialEq, Eq, Serialize)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
enum Status {
    Scheduled,
    Visible,
    Snoozed { until_ms: u64 },
    Dismissed,
}
#[derive(Clone, Deserialize, PartialEq, Eq, Serialize)]
#[serde(deny_unknown_fields)]
struct Entry {
    reminder: Reminder,
    status: Status,
}
#[derive(Clone, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct Document {
    version: u32,
    items: Vec<Entry>,
}
struct Session {
    store: GoogleStore,
    items: Vec<Entry>,
    error: Option<String>,
}
pub struct GoogleQueue {
    session: Mutex<Session>,
}

#[derive(Clone, Serialize)]
pub struct CardView {
    #[serde(flatten)]
    pub card: NotificationCard,
    pub all_day: bool,
    pub untitled: bool,
}

impl Session {
    fn save(&mut self, items: Vec<Entry>) -> Result<(), String> {
        if let Some(error) = &self.error {
            return Err(error.clone());
        }
        if items.len() > 50000 {
            return Err("google_queue_protected".into());
        }
        self.store
            .save(&Document {
                version: 1,
                items: items.clone(),
            })
            .map_err(|_| "google_queue_save_failed")?;
        self.items = items;
        Ok(())
    }
}

impl GoogleQueue {
    pub fn new(path: PathBuf) -> Self {
        let mut store = GoogleStore::new(path);
        let (items, error) = match store.load::<Document>() {
            Ok(None) => (vec![], None),
            Ok(Some(document)) if valid(&document) => (document.items, None),
            _ => (vec![], Some("google_queue_protected".into())),
        };
        Self {
            session: Mutex::new(Session {
                store,
                items,
                error,
            }),
        }
    }
    pub fn error(&self) -> Option<String> {
        self.session.lock().ok()?.error.clone()
    }
    pub fn counts(&self) -> [usize; 4] {
        let mut counts = [0; 4];
        if let Ok(session) = self.session.lock() {
            for entry in &session.items {
                counts[match entry.status {
                    Status::Scheduled => 0,
                    Status::Visible => 1,
                    Status::Snoozed { .. } => 2,
                    Status::Dismissed => 3,
                }] += 1;
            }
        }
        counts
    }
    pub fn earliest(&self, now: u64) -> u64 {
        self.session
            .lock()
            .map(|s| {
                s.items
                    .iter()
                    .filter(|e| e.status == Status::Scheduled)
                    .map(|e| e.reminder.due_ms)
                    .fold(now, u64::min)
                    .max(now.saturating_sub(30 * DAY))
            })
            .unwrap_or(now)
    }
    pub fn visible(&self, selected: &[String], size: &CardSize) -> Vec<CardView> {
        let Ok(session) = self.session.lock() else {
            return vec![];
        };
        let mut result: Vec<_> = session
            .items
            .iter()
            .filter(|e| e.status == Status::Visible && selected.contains(&e.reminder.source_id))
            .collect();
        result.sort_by_key(|e| (e.reminder.due_ms, &e.reminder.card.id));
        result
            .into_iter()
            .map(|e| {
                let mut card = e.reminder.card.clone();
                card.card_size = size.clone();
                CardView {
                    card,
                    all_day: e.reminder.all_day,
                    untitled: e.reminder.untitled,
                }
            })
            .collect()
    }
    pub fn has_visible(&self, selected: &[String]) -> bool {
        self.session.lock().is_ok_and(|s| {
            s.items
                .iter()
                .any(|e| e.status == Status::Visible && selected.contains(&e.reminder.source_id))
        })
    }
    pub fn merge(
        &self,
        incoming: Vec<Reminder>,
        selected: &[String],
        now: u64,
    ) -> Result<(), String> {
        let mut session = self.session.lock().map_err(|_| "google_queue_protected")?;
        if incoming.iter().any(|r| !selected.contains(&r.source_id)) {
            return Err("google_events_invalid".into());
        }
        let mut next: HashMap<String, Entry> = session
            .items
            .iter()
            .filter(|entry| {
                entry.status != Status::Scheduled
                    && (selected.contains(&entry.reminder.source_id)
                        || entry.status == Status::Dismissed)
                    && (entry.status != Status::Dismissed
                        || entry.reminder.due_ms >= now.saturating_sub(30 * DAY))
            })
            .map(|entry| (entry.reminder.card.id.clone(), entry.clone()))
            .collect();
        let previous: HashSet<_> = session
            .items
            .iter()
            .filter(|e| e.status == Status::Scheduled)
            .map(|e| e.reminder.card.id.as_str())
            .collect();
        let mut seen = HashSet::new();
        for reminder in incoming {
            let id = reminder.card.id.clone();
            if !seen.insert(id.clone()) {
                return Err("google_events_invalid".into());
            }
            if !next.contains_key(&id) && (reminder.due_ms >= now || previous.contains(id.as_str()))
            {
                next.insert(
                    id,
                    Entry {
                        reminder,
                        status: Status::Scheduled,
                    },
                );
            }
        }
        let mut items: Vec<_> = next.into_values().collect();
        items.sort_by(|a, b| a.reminder.card.id.cmp(&b.reminder.card.id));
        session.save(items)
    }
    pub fn retain_sources(&self, selected: &[String]) -> Result<(), String> {
        let mut session = self.session.lock().map_err(|_| "google_queue_protected")?;
        let items = session
            .items
            .iter()
            .filter(|e| selected.contains(&e.reminder.source_id) || e.status == Status::Dismissed)
            .cloned()
            .collect();
        session.save(items)
    }
    pub fn clear(&self) -> Result<(), String> {
        self.session
            .lock()
            .map_err(|_| "google_queue_protected")?
            .save(vec![])
    }
    pub fn tick(&self, selected: &[String], now: u64) -> Result<usize, String> {
        let mut session = self.session.lock().map_err(|_| "google_queue_protected")?;
        if let Some(error) = &session.error {
            return Err(error.clone());
        }
        let due = |entry: &Entry| {
            selected.contains(&entry.reminder.source_id)
                && match entry.status {
                    Status::Scheduled => entry.reminder.due_ms <= now,
                    Status::Snoozed { until_ms } => until_ms <= now,
                    _ => false,
                }
        };
        if !session.items.iter().any(due) {
            return Ok(0);
        }
        let mut items = session.items.clone();
        let mut count = 0;
        for entry in &mut items {
            if due(entry) {
                entry.status = Status::Visible;
                count += 1;
            }
        }
        if count > 0 {
            session.save(items)?;
        }
        Ok(count)
    }
    pub fn act(&self, id: &str, snooze: bool, now: u64) -> Result<(), String> {
        let mut session = self.session.lock().map_err(|_| "google_queue_protected")?;
        let mut items = session.items.clone();
        let entry = items
            .iter_mut()
            .find(|e| e.reminder.card.id == id && e.status == Status::Visible)
            .ok_or("notification_not_found")?;
        entry.status = if snooze {
            Status::Snoozed {
                until_ms: now
                    .checked_add(300_000)
                    .ok_or("notification_invalid_time")?,
            }
        } else {
            Status::Dismissed
        };
        session.save(items)
    }
}
fn valid(document: &Document) -> bool {
    document.version == 1
        && document.items.len() <= 50000
        && document
            .items
            .iter()
            .map(|e| &e.reminder.card.id)
            .collect::<HashSet<_>>()
            .len()
            == document.items.len()
        && document.items.iter().all(|e| {
            let card = &e.reminder.card;
            let color = card.calendar_color.as_bytes();
            !card.id.is_empty()
                && card.id.len() <= 1024
                && !e.reminder.source_id.is_empty()
                && e.reminder.source_id.len() <= 2048
                && !card.title.is_empty()
                && card.title.len() <= 16384
                && !card.start_time.is_empty()
                && card.start_time.len() <= 128
                && color.len() == 7
                && color[0] == b'#'
                && color[1..].iter().all(u8::is_ascii_hexdigit)
                && e.reminder.due_ms <= 253_402_300_799_999
                && !matches!(e.status,Status::Snoozed{until_ms} if until_ms>253_402_300_799_999)
        })
}

#[cfg(test)]
mod tests {
    use super::*;
    struct Fixture(PathBuf);
    impl Fixture {
        fn new() -> Self {
            let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
                .join("../../artifacts/google-queue-tests");
            std::fs::create_dir_all(&root).unwrap();
            let path = root.join(oauth2::CsrfToken::new_random().secret());
            std::fs::create_dir(&path).unwrap();
            Self(path)
        }
        fn queue(&self) -> GoogleQueue {
            GoogleQueue::new(self.0.join("queue.dpapi"))
        }
    }
    impl Drop for Fixture {
        fn drop(&mut self) {
            let path = std::fs::canonicalize(&self.0).unwrap();
            let root = std::fs::canonicalize(
                PathBuf::from(env!("CARGO_MANIFEST_DIR"))
                    .join("../../artifacts/google-queue-tests"),
            )
            .unwrap();
            assert_eq!(path.parent(), Some(root.as_path()));
            std::fs::remove_dir_all(path).unwrap();
        }
    }
    fn reminder(id: &str, due: u64) -> Reminder {
        Reminder {
            source_id: "source".into(),
            card: NotificationCard {
                id: id.into(),
                start_time: "15:00".into(),
                title: "Fixture".into(),
                calendar_color: "#123456".into(),
                card_size: CardSize::Small,
            },
            due_ms: due,
            all_day: false,
            untitled: false,
        }
    }
    fn selected() -> Vec<String> {
        vec!["source".into()]
    }
    #[test]
    fn due_batch_snooze_close_restart_and_refresh_do_not_resurrect() {
        let f = Fixture::new();
        let q = f.queue();
        q.merge(
            vec![reminder("a", 200), reminder("b", 200)],
            &selected(),
            100,
        )
        .unwrap();
        assert_eq!(q.tick(&selected(), 199).unwrap(), 0);
        assert_eq!(q.tick(&selected(), 200).unwrap(), 2);
        q.act("a", true, 200).unwrap();
        q.act("b", false, 200).unwrap();
        assert!(q.visible(&selected(), &CardSize::Small).is_empty());
        drop(q);
        let q = f.queue();
        q.merge(
            vec![reminder("a", 200), reminder("b", 200)],
            &selected(),
            201,
        )
        .unwrap();
        assert_eq!(q.tick(&selected(), 300199).unwrap(), 0);
        assert_eq!(q.tick(&selected(), 300200).unwrap(), 1);
        assert_eq!(q.visible(&selected(), &CardSize::Large)[0].card.id, "a");
        assert_eq!(q.tick(&selected(), 400000).unwrap(), 0);
    }
    #[test]
    fn future_changes_cancellation_and_source_deselection_are_atomic() {
        let f = Fixture::new();
        let q = f.queue();
        q.merge(
            vec![reminder("a", 200), reminder("b", 200)],
            &selected(),
            100,
        )
        .unwrap();
        q.merge(vec![reminder("a", 300)], &selected(), 100).unwrap();
        assert_eq!(q.tick(&selected(), 200).unwrap(), 0);
        assert_eq!(q.tick(&selected(), 300).unwrap(), 1);
        q.retain_sources(&[]).unwrap();
        assert!(q.visible(&selected(), &CardSize::Small).is_empty());
        q.merge(vec![reminder("old", 10)], &selected(), 100)
            .unwrap();
        assert_eq!(q.tick(&selected(), 100).unwrap(), 0);
    }
    #[test]
    fn failed_write_keeps_saved_reservations_and_original_bytes() {
        let f = Fixture::new();
        let q = f.queue();
        q.merge(vec![reminder("a", 200)], &selected(), 100).unwrap();
        let path = f.0.join("queue.dpapi");
        let bytes = std::fs::read(&path).unwrap();
        let backup = f.0.join("saved");
        std::fs::rename(&path, &backup).unwrap();
        std::fs::create_dir(&path).unwrap();
        assert!(q.tick(&selected(), 200).is_err());
        assert!(q.visible(&selected(), &CardSize::Small).is_empty());
        assert_eq!(std::fs::read(&backup).unwrap(), bytes);
        std::fs::remove_dir(&path).unwrap();
        std::fs::rename(backup, path).unwrap();
        assert_eq!(q.tick(&selected(), 201).unwrap(), 1);
    }
    #[test]
    fn malformed_file_is_not_overwritten() {
        let f = Fixture::new();
        let path = f.0.join("queue.dpapi");
        std::fs::write(&path, b"broken").unwrap();
        let q = f.queue();
        assert!(q.merge(vec![], &[], 0).is_err());
        assert_eq!(std::fs::read(path).unwrap(), b"broken");
    }
}
