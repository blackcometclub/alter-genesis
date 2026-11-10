use crate::{google::Calendar, notification::NotificationCard, settings::CardSize};
use chrono::{DateTime, Local, NaiveDate, TimeZone, Utc};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::collections::HashSet;

pub const DAY: u64 = 86_400_000;
pub const EVENTS_SCOPE: &str = "https://www.googleapis.com/auth/calendar.events.readonly";
const INVALID: &str = "google_events_invalid";

#[derive(Clone, Debug, Deserialize, PartialEq, Eq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Reminder {
    pub source_id: String,
    pub card: NotificationCard,
    pub due_ms: u64,
    pub all_day: bool,
    pub untitled: bool,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Eq)]
struct Alarm {
    method: String,
    minutes: Option<i64>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct EventAlarms {
    use_default: bool,
    #[serde(default)]
    overrides: Vec<Alarm>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Start {
    date: Option<String>,
    date_time: Option<String>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Attendee {
    #[serde(rename = "self", default)]
    myself: bool,
    response_status: Option<String>,
}
#[derive(Deserialize)]
struct Event {
    id: String,
    summary: Option<String>,
    status: Option<String>,
    start: Option<Start>,
    reminders: Option<EventAlarms>,
    #[serde(default)]
    attendees: Vec<Attendee>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Page {
    #[serde(default)]
    items: Vec<Event>,
    next_page_token: Option<String>,
    default_reminders: Option<Vec<Alarm>>,
}

fn millis(time: DateTime<impl TimeZone>) -> Result<u64, String> {
    u64::try_from(time.timestamp_millis()).map_err(|_| INVALID.into())
}
fn stamp(value: u64) -> Result<String, String> {
    DateTime::<Utc>::from_timestamp_millis(i64::try_from(value).map_err(|_| INVALID)?)
        .map(|time| time.to_rfc3339())
        .ok_or(INVALID.into())
}
fn identity(calendar: &str, event: &str, start: &str, alarm: &str) -> String {
    let value = serde_json::to_vec(&[calendar, event, start, alarm]).expect("string array");
    format!("google-{:x}", Sha256::digest(value))
}

fn project(
    calendar: &Calendar,
    event: Event,
    defaults: &[Alarm],
    now: u64,
) -> Result<Vec<Reminder>, String> {
    if event.status.as_deref() == Some("cancelled")
        || event
            .attendees
            .iter()
            .any(|a| a.myself && a.response_status.as_deref() == Some("declined"))
    {
        return Ok(vec![]);
    }
    if event.id.trim().is_empty() || event.id.len() > 2048 {
        return Err(INVALID.into());
    }
    let start = event.start.ok_or(INVALID)?;
    let title = event
        .summary
        .unwrap_or_default()
        .replace(['\r', '\n'], " ")
        .trim()
        .to_owned();
    if title.len() > 16384 {
        return Err(INVALID.into());
    }
    let untitled = title.is_empty();
    let title = if untitled {
        "Untitled".to_owned()
    } else {
        title
    };
    let (start_text, key, all_day, alarms) = if let Some(date) = start.date {
        if start.date_time.is_some() {
            return Err(INVALID.into());
        }
        let day = NaiveDate::parse_from_str(&date, "%Y-%m-%d").map_err(|_| INVALID)?;
        // Explicit product decision: all-day events are shown at 09:00 in the PC's local zone.
        let due = Local
            .from_local_datetime(&day.and_hms_opt(9, 0, 0).ok_or(INVALID)?)
            .single()
            .ok_or(INVALID)?;
        (
            "All day".to_owned(),
            date,
            true,
            vec![("all-day-09:00".to_owned(), millis(due)?)],
        )
    } else {
        let time =
            DateTime::parse_from_rfc3339(&start.date_time.ok_or(INVALID)?).map_err(|_| INVALID)?;
        let epoch = millis(time)?;
        let reminders = event.reminders.ok_or(INVALID)?;
        let rules = if reminders.use_default {
            defaults
        } else {
            &reminders.overrides
        };
        let mut minutes = std::collections::BTreeSet::new();
        for rule in rules.iter().filter(|r| r.method == "popup") {
            let value = rule.minutes.ok_or(INVALID)?;
            if !(0..=40320).contains(&value) {
                return Err(INVALID.into());
            }
            minutes.insert(value as u64);
        }
        let alarms = minutes
            .into_iter()
            .map(|m| {
                epoch
                    .checked_sub(m * 60_000)
                    .map(|due| (m.to_string(), due))
                    .ok_or_else(|| INVALID.to_owned())
            })
            .collect::<Result<Vec<_>, _>>()?;
        (
            time.with_timezone(&Local).format("%H:%M").to_string(),
            time.with_timezone(&Utc).to_rfc3339(),
            false,
            alarms,
        )
    };
    Ok(alarms
        .into_iter()
        .filter(|(_, due)| {
            *due >= now.saturating_sub(30 * DAY) && *due < now.saturating_add(30 * DAY)
        })
        .map(|(alarm, due)| {
            let id = identity(&calendar.id, &event.id, &key, &alarm);
            Reminder {
                source_id: calendar.id.clone(),
                card: NotificationCard {
                    id,
                    start_time: start_text.clone(),
                    title: title.clone(),
                    calendar_color: calendar.color.clone(),
                    card_size: CardSize::Small,
                },
                due_ms: due,
                all_day,
                untitled,
            }
        })
        .collect())
}

pub async fn fetch(
    http: &reqwest::Client,
    access: &str,
    calendars: &[Calendar],
    selected: &[String],
    now: u64,
    from: u64,
) -> Result<Vec<Reminder>, String> {
    fetch_at(
        http,
        access,
        calendars,
        selected,
        now,
        from,
        "https://www.googleapis.com/calendar/v3/calendars/",
    )
    .await
}

#[allow(clippy::too_many_arguments)]
async fn fetch_at(
    http: &reqwest::Client,
    access: &str,
    calendars: &[Calendar],
    selected: &[String],
    now: u64,
    from: u64,
    base: &str,
) -> Result<Vec<Reminder>, String> {
    let mut output = Vec::new();
    for calendar in calendars.iter().filter(|c| selected.contains(&c.id)) {
        let mut url = url::Url::parse(base).expect("static URL");
        url.path_segments_mut()
            .map_err(|_| INVALID)?
            .pop_if_empty()
            .push(&calendar.id)
            .push("events");
        let mut token = None;
        let mut tokens = HashSet::new();
        let mut events = Vec::new();
        let mut ids = HashSet::new();
        let mut defaults: Option<Vec<Alarm>> = None;
        for page_number in 0..100 {
            let mut request = http.get(url.clone()).bearer_auth(access).query(&[
                ("singleEvents", "true".to_owned()), ("showDeleted", "false".to_owned()),
                ("maxResults", "250".to_owned()), ("timeZone", "UTC".to_owned()),
                ("timeMin", stamp(from.max(now.saturating_sub(30 * DAY)))?),
                ("timeMax", stamp(now.saturating_add(58 * DAY))?),
                ("fields", "nextPageToken,defaultReminders(method,minutes),items(id,summary,status,start(date,dateTime),reminders(useDefault,overrides(method,minutes)),attendees(self,responseStatus))".into()),
            ]);
            if let Some(token) = &token {
                request = request.query(&[("pageToken", token)]);
            }
            let mut response = request.send().await.map_err(|e| {
                if e.is_timeout() {
                    "google_timeout"
                } else {
                    "google_network_error"
                }
            })?;
            match response.status().as_u16() {
                200 => (),
                401 => return Err("google_reconnect_required".into()),
                403 => return Err("google_events_permission_required".into()),
                _ => return Err("google_network_error".into()),
            }
            let mut bytes = Vec::new();
            while let Some(chunk) = response.chunk().await.map_err(|_| "google_network_error")? {
                if bytes.len() + chunk.len() > 2 * 1024 * 1024 {
                    return Err(INVALID.into());
                }
                bytes.extend_from_slice(&chunk);
            }
            let page: Page = serde_json::from_slice(&bytes).map_err(|_| INVALID)?;
            if let Some(value) = page.default_reminders {
                if defaults.as_ref().is_some_and(|old| old != &value) {
                    return Err(INVALID.into());
                }
                defaults = Some(value);
            }
            for event in page.items {
                if !ids.insert(event.id.clone()) || ids.len() > 10000 {
                    return Err(INVALID.into());
                }
                events.push(event);
            }
            token = page.next_page_token.filter(|value| !value.is_empty());
            if let Some(value) = &token {
                if page_number == 99 || value.len() > 8192 || !tokens.insert(value.clone()) {
                    return Err(INVALID.into());
                }
            } else {
                break;
            }
        }
        for event in events {
            output.extend(project(
                calendar,
                event,
                defaults.as_deref().unwrap_or_default(),
                now,
            )?);
            if output.len() > 50000 {
                return Err(INVALID.into());
            }
        }
    }
    Ok(output)
}

#[cfg(test)]
mod tests {
    use super::*;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    #[tokio::test]
    async fn all_pages_and_defaults_are_read_and_partial_failures_never_escape() {
        for fail in [false, true] {
            let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
            let address = listener.local_addr().unwrap();
            let server = tokio::spawn(async move {
                for index in 0..2 {
                    let (mut stream, _) = listener.accept().await.unwrap();
                    let mut request = vec![0; 8192];
                    let count = stream.read(&mut request).await.unwrap();
                    let request = String::from_utf8_lossy(&request[..count]);
                    assert!(request.contains("singleEvents=true"));
                    assert!(request.contains("Bearer fixture"));
                    if index == 1 {
                        assert!(request.contains("pageToken=next"));
                    }
                    let body = if index == 0 {
                        r#"{"defaultReminders":[{"method":"popup","minutes":10}],"nextPageToken":"next","items":[{"id":"a","start":{"dateTime":"2026-11-11T15:00:00+09:00"},"reminders":{"useDefault":true}}]}"#
                    } else {
                        r#"{"items":[{"id":"b","start":{"dateTime":"2026-11-11T15:00:00+09:00"},"reminders":{"useDefault":false,"overrides":[{"method":"popup","minutes":2}]}}]}"#
                    };
                    let status = if fail && index == 1 {
                        "500 Error"
                    } else {
                        "200 OK"
                    };
                    stream.write_all(format!("HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",body.len()).as_bytes()).await.unwrap();
                }
            });
            let now =
                millis(DateTime::parse_from_rfc3339("2026-11-11T00:00:00Z").unwrap()).unwrap();
            let result = fetch_at(
                &reqwest::Client::new(),
                "fixture",
                &[calendar()],
                &["fixture".into()],
                now,
                now,
                &format!("http://{address}/calendars/"),
            )
            .await;
            server.await.unwrap();
            if fail {
                assert_eq!(result.unwrap_err(), "google_network_error");
            } else {
                let reminders = result.unwrap();
                assert_eq!(reminders.len(), 2);
                assert_eq!(reminders[1].due_ms - reminders[0].due_ms, 480_000);
            }
        }
    }
    fn calendar() -> Calendar {
        Calendar {
            id: "fixture".into(),
            name: "Fixture".into(),
            color: "#123456".into(),
            primary: true,
        }
    }
    fn event(reminders: serde_json::Value) -> Event {
        serde_json::from_value(serde_json::json!({"id":"event", "summary":"Fixture", "start":{"dateTime":"2026-11-11T15:00:00+09:00"}, "reminders":reminders})).unwrap()
    }
    #[test]
    fn defaults_overrides_none_email_and_multiple_use_google_minutes() {
        let defaults = vec![Alarm {
            method: "popup".into(),
            minutes: Some(10),
        }];
        let now = millis(DateTime::parse_from_rfc3339("2026-11-11T00:00:00Z").unwrap()).unwrap();
        let start = millis(DateTime::parse_from_rfc3339("2026-11-11T06:00:00Z").unwrap()).unwrap();
        let result = project(
            &calendar(),
            event(serde_json::json!({"useDefault":true})),
            &defaults,
            now,
        )
        .unwrap();
        assert_eq!(result[0].due_ms, start - 600_000);
        for rules in [
            serde_json::json!({"useDefault":false}),
            serde_json::json!({"useDefault":false,"overrides":[{"method":"email","minutes":3}]}),
        ] {
            assert!(
                project(&calendar(), event(rules), &defaults, now)
                    .unwrap()
                    .is_empty()
            );
        }
        let result = project(&calendar(), event(serde_json::json!({"useDefault":false,"overrides":[{"method":"popup","minutes":2},{"method":"popup","minutes":2},{"method":"popup","minutes":5}]})), &defaults, now).unwrap();
        assert_eq!(result.len(), 2);
        assert!(result.iter().any(|r| r.due_ms == start - 120_000));
        assert_ne!(result[0].card.id, result[1].card.id);
    }
    #[test]
    fn cancellation_decline_invalid_rules_and_large_lead_are_not_guessed() {
        let now = millis(DateTime::parse_from_rfc3339("2026-11-11T00:00:00Z").unwrap()).unwrap();
        let mut cancelled = event(serde_json::json!({"useDefault":true}));
        cancelled.status = Some("cancelled".into());
        cancelled.start = None;
        assert!(
            project(&calendar(), cancelled, &[], now)
                .unwrap()
                .is_empty()
        );
        let mut declined = event(serde_json::json!({"useDefault":true}));
        declined.attendees.push(Attendee {
            myself: true,
            response_status: Some("declined".into()),
        });
        assert!(project(&calendar(), declined, &[], now).unwrap().is_empty());
        assert!(project(&calendar(), event(serde_json::json!({"useDefault":false,"overrides":[{"method":"popup","minutes":-1}]})), &[], now).is_err());
        let mut distant = event(
            serde_json::json!({"useDefault":false,"overrides":[{"method":"popup","minutes":40320}]}),
        );
        distant.start = Some(Start {
            date: None,
            date_time: Some("2026-12-20T00:00:00Z".into()),
        });
        assert_eq!(project(&calendar(), distant, &[], now).unwrap().len(), 1);
    }
    #[test]
    fn all_day_uses_explicit_local_nine_and_identifiers_are_stable() {
        let mut item = event(serde_json::json!({"useDefault":false}));
        item.start = Some(Start {
            date: Some("2026-11-12".into()),
            date_time: None,
        });
        let now = millis(DateTime::parse_from_rfc3339("2026-11-11T00:00:00Z").unwrap()).unwrap();
        let result = project(&calendar(), item, &[], now).unwrap();
        let local = DateTime::from_timestamp_millis(result[0].due_ms as i64)
            .unwrap()
            .with_timezone(&Local);
        assert_eq!(
            local.format("%Y-%m-%d %H:%M").to_string(),
            "2026-11-12 09:00"
        );
        assert_eq!(identity("a", "b", "c", "2"), identity("a", "b", "c", "2"));
        assert_ne!(identity("a", "b", "c", "2"), identity("a", "b", "d", "2"));
    }
}
