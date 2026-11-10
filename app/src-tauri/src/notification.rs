use crate::notification_store::{NotificationStore, SavedNotification};
use crate::settings::CardSize;
use serde::{Deserialize, Serialize};
use std::{
    path::PathBuf,
    sync::Mutex,
    time::{Duration, SystemTime, UNIX_EPOCH},
};

pub const SNOOZE_DURATION: Duration = Duration::from_secs(5 * 60);

pub fn unix_now_ms() -> Result<u64, String> {
    let elapsed = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(|_| "notification_invalid_time")?;
    u64::try_from(elapsed.as_millis()).map_err(|_| "notification_invalid_time".to_owned())
}

#[derive(Clone, Debug, Deserialize, PartialEq, Eq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct NotificationCard {
    pub id: String,
    pub start_time: String,
    pub title: String,
    pub calendar_color: String,
    pub card_size: CardSize,
}

impl NotificationCard {
    pub fn preview(card_size: CardSize) -> Self {
        Self {
            id: "notification-preview".to_owned(),
            start_time: "15:00".to_owned(),
            title: "歯医者の予約".to_owned(),
            calendar_color: "#74899E".to_owned(),
            card_size,
        }
    }
}

struct NotificationSession {
    active: Option<NotificationCard>,
    preview_mode: bool,
    pending: Option<(NotificationCard, u64, u64)>,
    generation: u64,
    store: NotificationStore,
}

impl NotificationSession {
    fn check(&self) -> Result<(), String> {
        self.store.error.clone().map_or(Ok(()), Err)
    }
}

pub struct NotificationState {
    session: Mutex<NotificationSession>,
}

impl NotificationState {
    pub fn open(
        path: PathBuf,
        preview: Option<NotificationCard>,
        preview_mode: bool,
        now: u64,
    ) -> Self {
        let mut store = NotificationStore::open(path);
        let record = match store.load() {
            Ok(Some(saved)) => saved,
            Ok(None) => {
                let seed = preview.map(|card| SavedNotification {
                    card,
                    snooze_until_unix_ms: None,
                });
                if seed.is_some()
                    && let Err(error) = store.save(seed.clone())
                {
                    store.error = Some(error);
                }
                seed
            }
            Err(_) => None,
        };
        let (active, pending) = match record {
            Some(SavedNotification {
                card,
                snooze_until_unix_ms: Some(deadline),
            }) if deadline > now => (None, Some((card, deadline, 1))),
            Some(SavedNotification { card, .. }) => (Some(card), None),
            None => (None, None),
        };
        Self {
            session: Mutex::new(NotificationSession {
                active,
                pending,
                preview_mode,
                generation: 1,
                store,
            }),
        }
    }

    pub fn load(&self) -> Result<Option<NotificationCard>, String> {
        let session = self
            .session
            .lock()
            .map_err(|_| "notification_state_unavailable")?;
        session.check()?;
        Ok(session.active.clone())
    }

    // A newly created hidden panel must render its waiting card without closing itself.
    pub fn panel_card(&self) -> Result<Option<NotificationCard>, String> {
        let session = self
            .session
            .lock()
            .map_err(|_| "notification_state_unavailable")?;
        session.check()?;
        Ok(session
            .active
            .clone()
            .or_else(|| session.pending.as_ref().map(|(card, _, _)| card.clone())))
    }

    pub fn pending_generation(&self) -> Option<u64> {
        self.session
            .lock()
            .ok()?
            .pending
            .as_ref()
            .map(|(_, _, token)| *token)
    }

    pub fn remaining(&self, token: u64, now: u64) -> Result<Option<Duration>, String> {
        let session = self
            .session
            .lock()
            .map_err(|_| "notification_state_unavailable")?;
        session.check()?;
        Ok(session
            .pending
            .as_ref()
            .filter(|(_, _, generation)| *generation == token)
            .map(|(_, deadline, _)| Duration::from_millis(deadline.saturating_sub(now))))
    }

    pub fn dismiss(&self, id: &str) -> Result<bool, String> {
        let mut session = self
            .session
            .lock()
            .map_err(|_| "notification_state_unavailable")?;
        session.check()?;
        if session.active.as_ref().is_none_or(|card| card.id != id)
            && session
                .pending
                .as_ref()
                .is_none_or(|(card, _, _)| card.id != id)
        {
            return Err("notification_not_found".to_owned());
        }
        session.store.save(None)?;
        session.active = None;
        session.pending = None;
        session.generation = session.generation.wrapping_add(1);
        Ok(session.preview_mode)
    }

    pub fn snooze(&self, id: &str, now: u64) -> Result<u64, String> {
        let mut session = self
            .session
            .lock()
            .map_err(|_| "notification_state_unavailable")?;
        session.check()?;
        let card = session
            .active
            .as_ref()
            .filter(|card| card.id == id)
            .cloned()
            .ok_or("notification_not_found")?;
        let deadline = now
            .checked_add(SNOOZE_DURATION.as_millis() as u64)
            .ok_or("notification_invalid_time")?;
        session.store.save(Some(SavedNotification {
            card: card.clone(),
            snooze_until_unix_ms: Some(deadline),
        }))?;
        session.active = None;
        session.generation = session.generation.wrapping_add(1);
        let generation = session.generation;
        session.pending = Some((card, deadline, generation));
        Ok(generation)
    }

    pub fn restore_due(&self, generation: u64, now: u64) -> Result<bool, String> {
        let mut session = self
            .session
            .lock()
            .map_err(|_| "notification_state_unavailable")?;
        session.check()?;
        if !session
            .pending
            .as_ref()
            .is_some_and(|(_, deadline, token)| *token == generation && now >= *deadline)
        {
            return Ok(false);
        }
        let (card, _, _) = session.pending.take().ok_or("notification_not_found")?;
        // An expired on-disk snooze is also visible on restart. No disk write is needed here.
        session.active = Some(card);
        Ok(true)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    struct Fixture(PathBuf);
    impl Fixture {
        fn new() -> Self {
            let stamp = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos();
            Self(std::env::temp_dir().join(format!(
                "alter-genesis-notification-{}-{stamp}",
                std::process::id()
            )))
        }
        fn path(&self) -> PathBuf {
            self.0.join("notifications.json")
        }
        fn open(&self, now: u64) -> NotificationState {
            NotificationState::open(
                self.path(),
                Some(NotificationCard::preview(CardSize::Small)),
                true,
                now,
            )
        }
    }
    impl Drop for Fixture {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    #[test]
    fn restart_preserves_the_same_card_and_remaining_time() {
        let f = Fixture::new();
        let state = f.open(1_000_000);
        let card = state.load().unwrap().unwrap();
        state.snooze(&card.id, 1_000_000).unwrap();
        drop(state);
        let resumed = f.open(1_120_000);
        assert_eq!(resumed.load().unwrap(), None);
        assert_eq!(resumed.panel_card().unwrap(), Some(card.clone()));
        let token = resumed.pending_generation().unwrap();
        assert_eq!(
            resumed.remaining(token, 1_120_000).unwrap(),
            Some(Duration::from_secs(180))
        );
        assert!(!resumed.restore_due(token, 1_299_999).unwrap());
        assert!(resumed.restore_due(token, 1_300_000).unwrap());
        assert_eq!(resumed.load().unwrap(), Some(card));
        assert!(!resumed.restore_due(token, 1_300_001).unwrap());
    }

    #[test]
    fn overdue_and_visible_cards_restore_but_dismissed_cards_do_not() {
        let f = Fixture::new();
        let state = f.open(1_000_000);
        let card = state.load().unwrap().unwrap();
        drop(state);
        let state = f.open(1_001_000);
        assert_eq!(state.load().unwrap(), Some(card.clone()));
        state.snooze(&card.id, 1_001_000).unwrap();
        drop(state);
        let state = NotificationState::open(f.path(), None, false, 1_500_000);
        assert_eq!(state.load().unwrap(), Some(card.clone()));
        assert_eq!(state.pending_generation(), None);
        state.dismiss(&card.id).unwrap();
        drop(state);
        assert_eq!(f.open(1_600_000).load().unwrap(), None);
    }

    #[test]
    fn duplicate_wrong_ids_and_stale_timers_do_not_change_reservations() {
        let f = Fixture::new();
        let state = f.open(1_000_000);
        assert!(state.snooze("other", 1_000_000).is_err());
        let id = state.load().unwrap().unwrap().id;
        let token = state.snooze(&id, 1_000_000).unwrap();
        assert!(state.snooze(&id, 1_000_000).is_err());
        assert!(!state.restore_due(token.wrapping_sub(1), 2_000_000).unwrap());
        assert!(state.restore_due(token, 2_000_000).unwrap());
        let next = state.snooze(&id, 2_000_000).unwrap();
        assert!(!state.restore_due(token, 3_000_000).unwrap());
        state.dismiss(&id).unwrap();
        assert!(!state.restore_due(next, 3_000_000).unwrap());
    }

    #[test]
    fn corrupt_future_and_invalid_documents_are_never_overwritten() {
        let card = NotificationCard::preview(CardSize::Small);
        for bytes in [b"not json".to_vec(),
            serde_json::to_vec(&serde_json::json!({"version":2,"notification":null})).unwrap(),
            serde_json::to_vec(&serde_json::json!({"version":1})).unwrap(),
            serde_json::to_vec(&serde_json::json!({"version":1,"notification":{"card":card,"snooze_until_unix_ms":0,"future":true}})).unwrap(),
            serde_json::to_vec(&serde_json::json!({"version":1,"notification":{"card":{"id":"","start_time":"15:00","title":"x","calendar_color":"bad","card_size":"small"},"snooze_until_unix_ms":null}})).unwrap()] {
            let f = Fixture::new();
            fs::create_dir_all(&f.0).unwrap();
            fs::write(f.path(), &bytes).unwrap();
            let state = f.open(1_000_000);
            assert_eq!(state.load().unwrap_err(), "notification_protected");
            assert!(state.snooze(&card.id, 1_000_000).is_err());
            assert!(state.dismiss(&card.id).is_err());
            assert_eq!(fs::read(f.path()).unwrap(), bytes);
        }
    }

    #[test]
    fn failed_writes_keep_the_card_and_original_bytes() {
        let f = Fixture::new();
        let state = f.open(1_000_000);
        let card = state.load().unwrap().unwrap();
        let original = fs::read(f.path()).unwrap();
        let backup = f.0.join("original.json");
        fs::rename(f.path(), &backup).unwrap();
        fs::create_dir(f.path()).unwrap();
        assert!(state.snooze(&card.id, 1_000_000).is_err());
        assert_eq!(state.load().unwrap(), Some(card.clone()));
        assert!(state.dismiss(&card.id).is_err());
        assert_eq!(state.load().unwrap(), Some(card));
        assert_eq!(fs::read(&backup).unwrap(), original);
        fs::remove_dir(f.path()).unwrap();
        fs::rename(backup, f.path()).unwrap();
        assert!(state.snooze("notification-preview", 1_000_000).is_ok());
    }

    #[test]
    fn live_store_lock_blocks_second_owner_and_releases_on_exit() {
        let f = Fixture::new();
        let first = f.open(1_000_000);
        let second = f.open(1_000_000);
        assert_eq!(second.load().unwrap_err(), "notification_in_use");
        drop(second);
        drop(first);
        assert!(f.open(1_000_000).load().unwrap().is_some());
    }

    #[test]
    fn clock_change_does_not_release_a_card_before_its_saved_deadline() {
        let f = Fixture::new();
        let state = f.open(1_000_000);
        let token = state.snooze("notification-preview", 1_000_000).unwrap();
        assert!(!state.restore_due(token, 900_000).unwrap());
        assert!(state.restore_due(token, 2_000_000).unwrap());
    }
}
