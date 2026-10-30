use crate::settings::CardSize;
use serde::Serialize;
use std::sync::Mutex;

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

        match session.active.as_ref() {
            Some(notification) if notification.id == id => {
                session.active = None;
                Ok(session.preview_mode)
            }
            _ => Err("notification_not_found".to_owned()),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

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
