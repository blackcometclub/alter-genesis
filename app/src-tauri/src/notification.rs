use crate::settings::CardSize;
use serde::Serialize;
use std::sync::Mutex;
use std::time::{Duration, Instant};

pub const SNOOZE_DURATION: Duration = Duration::from_secs(5 * 60);

#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
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
    pending: Option<(NotificationCard, Instant, u64)>,
    generation: u64,
}

pub struct NotificationState {
    session: Mutex<NotificationSession>,
}

impl NotificationState {
    pub fn new(active: Option<NotificationCard>, preview_mode: bool) -> Self {
        Self {
            session: Mutex::new(NotificationSession {
                active,
                preview_mode,
                pending: None,
                generation: 0,
            }),
        }
    }

    pub fn load(&self) -> Result<Option<NotificationCard>, String> {
        self.session
            .lock()
            .map_err(|_| "notification_state_unavailable".to_owned())
            .map(|session| session.active.clone())
    }

    pub fn dismiss(&self, id: &str) -> Result<bool, String> {
        let mut session = self
            .session
            .lock()
            .map_err(|_| "notification_state_unavailable".to_owned())?;

        let matches = session.active.as_ref().is_some_and(|card| card.id == id)
            || session
                .pending
                .as_ref()
                .is_some_and(|(card, _, _)| card.id == id);
        if matches {
            session.active = None;
            session.pending = None;
            session.generation = session.generation.wrapping_add(1);
            Ok(session.preview_mode)
        } else {
            Err("notification_not_found".to_owned())
        }
    }

    pub fn snooze(&self, id: &str, now: Instant) -> Result<u64, String> {
        let mut session = self
            .session
            .lock()
            .map_err(|_| "notification_state_unavailable")?;
        if session.active.as_ref().is_none_or(|card| card.id != id) {
            return Err("notification_not_found".to_owned());
        }
        let card = session.active.take().ok_or("notification_not_found")?;
        session.generation = session.generation.wrapping_add(1);
        let generation = session.generation;
        session.pending = Some((card, now + SNOOZE_DURATION, generation));
        Ok(generation)
    }

    pub fn restore_due(&self, generation: u64, now: Instant) -> Result<bool, String> {
        let mut session = self
            .session
            .lock()
            .map_err(|_| "notification_state_unavailable")?;
        if !session
            .pending
            .as_ref()
            .is_some_and(|(_, deadline, token)| *token == generation && now >= *deadline)
        {
            return Ok(false);
        }
        let (card, _, _) = session.pending.take().ok_or("notification_not_found")?;
        session.active = Some(card);
        Ok(true)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn snooze_waits_five_minutes_and_returns_the_same_card_once() {
        let card = NotificationCard::preview(CardSize::Small);
        let state = NotificationState::new(Some(card.clone()), true);
        let now = Instant::now();
        let token = state.snooze(&card.id, now).unwrap();
        assert_eq!(state.load().unwrap(), None);
        assert!(
            !state
                .restore_due(token, now + Duration::from_secs(299))
                .unwrap()
        );
        assert!(
            state
                .restore_due(token, now + Duration::from_secs(300))
                .unwrap()
        );
        assert_eq!(state.load().unwrap(), Some(card));
        assert!(
            !state
                .restore_due(token, now + Duration::from_secs(301))
                .unwrap()
        );
    }

    #[test]
    fn duplicate_and_wrong_id_do_not_replace_a_pending_snooze() {
        let card = NotificationCard::preview(CardSize::Small);
        let state = NotificationState::new(Some(card.clone()), true);
        let now = Instant::now();
        assert!(state.snooze("other", now).is_err());
        assert_eq!(state.load().unwrap(), Some(card.clone()));
        let token = state.snooze(&card.id, now).unwrap();
        assert!(state.snooze(&card.id, now).is_err());
        assert!(state.restore_due(token, now + SNOOZE_DURATION).unwrap());
    }

    #[test]
    fn dismiss_cancels_waiting_and_old_timers_cannot_restore_new_snoozes() {
        let card = NotificationCard::preview(CardSize::Small);
        let state = NotificationState::new(Some(card.clone()), true);
        let now = Instant::now();
        let first = state.snooze(&card.id, now).unwrap();
        assert!(state.restore_due(first, now + SNOOZE_DURATION).unwrap());
        let second = state.snooze(&card.id, now + SNOOZE_DURATION).unwrap();
        assert!(!state.restore_due(first, now + SNOOZE_DURATION * 2).unwrap());
        state.dismiss(&card.id).unwrap();
        assert!(
            !state
                .restore_due(second, now + SNOOZE_DURATION * 2)
                .unwrap()
        );
        assert_eq!(state.load().unwrap(), None);
    }

    #[test]
    fn preview_card_keeps_the_confirmed_content_and_size() {
        let card = NotificationCard::preview(CardSize::Small);

        assert_eq!(card.id, "notification-preview");
        assert_eq!(card.start_time, "15:00");
        assert_eq!(card.title, "歯医者の予約");
        assert_eq!(card.calendar_color, "#74899E");
        assert_eq!(card.card_size, CardSize::Small);
    }

    #[test]
    fn matching_notification_is_removed_once() {
        let card = NotificationCard::preview(CardSize::Large);
        let state = NotificationState::new(Some(card.clone()), true);

        assert_eq!(state.load().unwrap(), Some(card));
        assert!(state.dismiss("notification-preview").unwrap());
        assert_eq!(state.load().unwrap(), None);
        assert_eq!(
            state.dismiss("notification-preview"),
            Err("notification_not_found".to_owned())
        );
    }

    #[test]
    fn different_notification_id_does_not_change_the_active_card() {
        let card = NotificationCard::preview(CardSize::Small);
        let state = NotificationState::new(Some(card.clone()), false);

        assert_eq!(
            state.dismiss("different-notification"),
            Err("notification_not_found".to_owned())
        );
        assert_eq!(state.load().unwrap(), Some(card));
    }
}
