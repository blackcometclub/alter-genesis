use crate::{
    google_auth::{self, ClientConfig, LIST_SCOPE},
    google_store::GoogleStore,
    notification::unix_now_ms,
};
use oauth2::{AuthorizationCode, RefreshToken, TokenResponse};
use serde::{Deserialize, Serialize};
use std::{
    collections::{HashMap, HashSet},
    path::PathBuf,
    sync::{Arc, Mutex},
    time::Duration,
};
use tokio_util::sync::CancellationToken;
use zeroize::Zeroize;

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Calendar {
    pub id: String,
    pub name: String,
    pub color: String,
    pub primary: bool,
}

#[derive(Clone, Serialize)]
pub struct GoogleView {
    pub configured: bool,
    pub connected: bool,
    pub calendars: Vec<Calendar>,
    pub selected_ids: Vec<String>,
    pub sync_error: Option<String>,
    pub error: Option<String>,
}

#[derive(Clone, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct Record {
    version: u32,
    client_id: String,
    access_token: String,
    refresh_token: String,
    expires_at_ms: u64,
    calendars: Vec<Calendar>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    selected_ids: Option<Vec<String>>,
}
impl Drop for Record {
    fn drop(&mut self) {
        self.access_token.zeroize();
        self.refresh_token.zeroize();
    }
}
impl Record {
    fn validate(&self, config: &ClientConfig) -> Result<(), String> {
        if self.client_id != config.client_id {
            return Err("google_client_changed".into());
        }
        if self.access_token.is_empty()
            || self.refresh_token.is_empty()
            || self.access_token.len() > 16_384
            || self.refresh_token.len() > 16_384
            || self.calendars.len() > 5000
            || self.selected_ids.as_ref().is_some_and(|ids| {
                ids.len() > self.calendars.len()
                    || ids.iter().collect::<HashSet<_>>().len() != ids.len()
                    || ids
                        .iter()
                        .any(|id| !self.calendars.iter().any(|c| &c.id == id))
            })
            || self.calendars.iter().any(|c| {
                c.id.trim().is_empty()
                    || c.id.len() > 2048
                    || c.name.len() > 16_384
                    || !valid_color(&c.color)
            })
            || self
                .calendars
                .iter()
                .map(|c| &c.id)
                .collect::<HashSet<_>>()
                .len()
                != self.calendars.len()
        {
            return Err("google_store_protected".into());
        }
        Ok(())
    }

    fn replace_calendars(&mut self, calendars: Vec<Calendar>) {
        self.selected_ids
            .get_or_insert_with(Vec::new)
            .retain(|id| calendars.iter().any(|c| &c.id == id));
        self.calendars = calendars;
    }

    fn selection_for_reconnection(&self, calendars: &[Calendar]) -> Vec<String> {
        let previous = self.calendars.iter().find(|c| c.primary);
        let next = calendars.iter().find(|c| c.primary);
        if !matches!((previous, next), (Some(a), Some(b)) if a.id == b.id) {
            return vec![];
        }
        self.selected_ids
            .clone()
            .unwrap_or_default()
            .into_iter()
            .filter(|id| calendars.iter().any(|c| &c.id == id))
            .collect()
    }
}

struct Session {
    store: GoogleStore,
    record: Option<Record>,
    error: Option<String>,
}
type OperationSlot = Arc<Mutex<Option<CancellationToken>>>;
struct Operation {
    slot: OperationSlot,
    token: CancellationToken,
}
impl Drop for Operation {
    fn drop(&mut self) {
        if let Ok(mut slot) = self.slot.lock() {
            *slot = None;
        }
    }
}

pub struct GoogleState {
    config: Option<ClientConfig>,
    http: reqwest::Client,
    session: Mutex<Session>,
    operation: OperationSlot,
}

impl GoogleState {
    pub fn new(path: PathBuf) -> Result<Self, String> {
        let config = ClientConfig::embedded().ok();
        Self::with_config(path, config)
    }

    fn with_config(path: PathBuf, config: Option<ClientConfig>) -> Result<Self, String> {
        let mut store = GoogleStore::new(path);
        let loaded = store.load::<Record>();
        let (record, error) = match loaded {
            Ok(Some(record)) => match config
                .as_ref()
                .ok_or("google_not_configured".into())
                .and_then(|c| record.validate(c))
            {
                Ok(()) => (Some(record), None),
                Err(error) => (None, Some(error)),
            },
            Ok(None) => (None, None),
            Err(error) => (None, Some(error)),
        };
        let http = reqwest::Client::builder()
            .redirect(reqwest::redirect::Policy::none())
            .connect_timeout(Duration::from_secs(120))
            .timeout(Duration::from_secs(120))
            .user_agent("ALTER GENESIS")
            .build()
            .map_err(|_| "google_network_error")?;
        Ok(Self {
            config,
            http,
            session: Mutex::new(Session {
                store,
                record,
                error,
            }),
            operation: Arc::new(Mutex::new(None)),
        })
    }

    fn begin(&self) -> Result<Operation, String> {
        let mut slot = self
            .operation
            .lock()
            .map_err(|_| "google_state_unavailable")?;
        if slot.is_some() {
            return Err("google_busy".into());
        }
        let token = CancellationToken::new();
        *slot = Some(token.clone());
        Ok(Operation {
            slot: self.operation.clone(),
            token,
        })
    }

    pub fn cancel(&self) {
        if let Ok(slot) = self.operation.lock()
            && let Some(token) = &*slot
        {
            token.cancel();
        }
    }

    pub fn selected_ids(&self) -> Vec<String> {
        self.session
            .lock()
            .ok()
            .and_then(|s| s.record.as_ref().and_then(|r| r.selected_ids.clone()))
            .unwrap_or_default()
    }

    pub fn load(&self) -> Result<GoogleView, String> {
        let session = self
            .session
            .lock()
            .map_err(|_| "google_state_unavailable")?;
        Ok(GoogleView {
            configured: self.config.is_some(),
            connected: session.record.is_some(),
            calendars: session
                .record
                .as_ref()
                .map(|r| r.calendars.clone())
                .unwrap_or_default(),
            error: session.error.clone(),
            selected_ids: session
                .record
                .as_ref()
                .and_then(|r| r.selected_ids.clone())
                .unwrap_or_default(),
            sync_error: None,
        })
    }

    fn config(&self) -> Result<ClientConfig, String> {
        let session = self
            .session
            .lock()
            .map_err(|_| "google_state_unavailable")?;
        if let Some(error) = &session.error {
            return Err(error.clone());
        }
        self.config.clone().ok_or("google_not_configured".into())
    }

    fn commit(&self, mut record: Record, token: &CancellationToken) -> Result<(), String> {
        // Serialize cancellation with the final save. A cancelled network result never replaces data.
        let _operation = self
            .operation
            .lock()
            .map_err(|_| "google_state_unavailable")?;
        if token.is_cancelled() {
            return Err("google_cancelled".into());
        }
        let mut session = self
            .session
            .lock()
            .map_err(|_| "google_state_unavailable")?;
        if session
            .record
            .as_ref()
            .is_some_and(|old| old.selected_ids.is_none())
        {
            session.store.preserve_original()?;
        }
        record.selected_ids.get_or_insert_with(Vec::new);
        session.store.save(&record)?;
        session.record = Some(record);
        session.error = None;
        Ok(())
    }

    pub async fn connect(
        &self,
        open: impl FnOnce(String) -> Result<(), String> + Send,
    ) -> Result<GoogleView, String> {
        let operation = self.begin()?;
        let action = async {
            let config = self.config()?;
            let listener = tokio::net::TcpListener::bind("127.0.0.1:0")
                .await
                .map_err(|_| "google_callback_failed")?;
            let address = listener
                .local_addr()
                .map_err(|_| "google_callback_failed")?;
            let client = config.client(Some(format!("http://{address}/oauth2callback")))?;
            let (url, state, verifier) = google_auth::authorization(&client);
            open(url.into())?;
            let code = google_auth::receive_code(listener, state).await?;
            let token = client
                .exchange_code(AuthorizationCode::new(code))
                .set_pkce_verifier(verifier)
                .request_async(&self.http)
                .await
                .map_err(oauth_error)?;
            validate_scopes(&token)?;
            let refresh = token
                .refresh_token()
                .ok_or("google_reconnect_required")?
                .secret()
                .clone();
            let calendars =
                fetch_catalog(&self.http, token.access_token().secret(), CATALOG_URL).await?;
            let selected_ids = self
                .session
                .lock()
                .map_err(|_| "google_state_unavailable")?
                .record
                .as_ref()
                .map(|old| old.selection_for_reconnection(&calendars))
                .unwrap_or_default();
            let record = Record {
                version: 1,
                client_id: config.client_id.clone(),
                access_token: token.access_token().secret().clone(),
                refresh_token: refresh,
                expires_at_ms: expires(&token)?,
                calendars,
                selected_ids: Some(selected_ids),
            };
            self.commit(record, &operation.token)?;
            self.load()
        };
        tokio::select! { biased; _ = operation.token.cancelled() => Err("google_cancelled".into()), result = action => result }
    }

    async fn renew(
        &self,
        record: &mut Record,
        config: &ClientConfig,
        cancellation: &CancellationToken,
    ) -> Result<(), String> {
        let token = config
            .client(None)?
            .exchange_refresh_token(&RefreshToken::new(record.refresh_token.clone()))
            .request_async(&self.http)
            .await
            .map_err(oauth_error)?;
        validate_scopes(&token)?;
        record.access_token.zeroize();
        record.access_token = token.access_token().secret().clone();
        if let Some(refresh) = token.refresh_token() {
            record.refresh_token.zeroize();
            record.refresh_token = refresh.secret().clone();
        }
        record.expires_at_ms = expires(&token)?;
        // Preserve rotated credentials even if fetching the next catalog subsequently fails.
        self.commit(record.clone(), cancellation)
    }

    pub async fn refresh(&self) -> Result<GoogleView, String> {
        let operation = self.begin()?;
        let action = async {
            let config = self.config()?;
            let mut record = self
                .session
                .lock()
                .map_err(|_| "google_state_unavailable")?
                .record
                .clone()
                .ok_or("google_not_connected")?;
            let mut renewed = false;
            if record.expires_at_ms <= unix_now_ms()?.saturating_add(60_000) {
                self.renew(&mut record, &config, &operation.token).await?;
                renewed = true;
            }
            let calendars = match fetch_catalog(&self.http, &record.access_token, CATALOG_URL).await
            {
                Err(error) if error == "google_reconnect_required" && !renewed => {
                    self.renew(&mut record, &config, &operation.token).await?;
                    fetch_catalog(&self.http, &record.access_token, CATALOG_URL).await?
                }
                other => other?,
            };
            record.replace_calendars(calendars);
            self.commit(record, &operation.token)?;
            self.load()
        };
        tokio::select! { biased; _ = operation.token.cancelled() => Err("google_cancelled".into()), result = action => result }
    }

    pub fn disconnect(&self) -> Result<GoogleView, String> {
        let _operation = self.begin()?;
        let mut session = self
            .session
            .lock()
            .map_err(|_| "google_state_unavailable")?;
        // Local-only: revoking a shared OAuth client grant could also disconnect the WPF reference app.
        session.store.clear()?;
        session.record = None;
        session.error = None;
        drop(session);
        self.load()
    }

    pub async fn read_notifications(
        &self,
        now: u64,
        from: u64,
    ) -> Result<Vec<crate::google_events::Reminder>, String> {
        let operation = self.begin()?;
        let action = async {
            let config = self.config()?;
            let mut record = self
                .session
                .lock()
                .map_err(|_| "google_state_unavailable")?
                .record
                .clone()
                .ok_or("google_not_connected")?;
            let selected = record.selected_ids.clone().unwrap_or_default();
            if selected.is_empty() {
                return Ok(vec![]);
            }
            let mut renewed = false;
            if record.expires_at_ms <= now.saturating_add(60_000) {
                self.renew(&mut record, &config, &operation.token).await?;
                renewed = true;
            }
            let result = crate::google_events::fetch(
                &self.http,
                &record.access_token,
                &record.calendars,
                &selected,
                now,
                from,
            )
            .await;
            if matches!(&result, Err(error) if error == "google_reconnect_required") && !renewed {
                self.renew(&mut record, &config, &operation.token).await?;
                return crate::google_events::fetch(
                    &self.http,
                    &record.access_token,
                    &record.calendars,
                    &selected,
                    now,
                    from,
                )
                .await;
            }
            result
        };
        tokio::select! { biased; _ = operation.token.cancelled() => Err("google_cancelled".into()), result = action => result }
    }

    pub fn set_selected(&self, id: &str, selected: bool) -> Result<GoogleView, String> {
        let operation = self.begin()?;
        self.config()?;
        let mut record = self
            .session
            .lock()
            .map_err(|_| "google_state_unavailable")?
            .record
            .clone()
            .ok_or("google_not_connected")?;
        if !record.calendars.iter().any(|calendar| calendar.id == id) {
            return Err("google_selection_invalid".into());
        }
        let ids = record.selected_ids.get_or_insert_with(Vec::new);
        if selected && !ids.iter().any(|value| value == id) {
            ids.push(id.to_owned());
        } else if !selected {
            ids.retain(|value| value != id);
        }
        self.commit(record, &operation.token)?;
        self.load()
    }
}

fn expires(token: &oauth2::basic::BasicTokenResponse) -> Result<u64, String> {
    unix_now_ms()?
        .checked_add(
            token
                .expires_in()
                .unwrap_or(Duration::from_secs(3600))
                .as_millis()
                .try_into()
                .map_err(|_| "google_auth_failed")?,
        )
        .ok_or("google_auth_failed".into())
}
fn validate_scopes(token: &oauth2::basic::BasicTokenResponse) -> Result<(), String> {
    if token
        .scopes()
        .is_some_and(|scopes| !scopes.iter().any(|scope| scope.as_str() == LIST_SCOPE))
    {
        Err("google_permission_denied".into())
    } else if token.scopes().is_some_and(|scopes| {
        !scopes
            .iter()
            .any(|scope| scope.as_str() == crate::google_events::EVENTS_SCOPE)
    }) {
        Err("google_events_permission_required".into())
    } else {
        Ok(())
    }
}
fn oauth_error<E: std::error::Error + 'static>(
    error: oauth2::RequestTokenError<E, oauth2::basic::BasicErrorResponse>,
) -> String {
    if matches!(error, oauth2::RequestTokenError::ServerResponse(ref response) if response.error() == &oauth2::basic::BasicErrorResponseType::InvalidGrant)
    {
        "google_reconnect_required".into()
    } else {
        "google_auth_failed".into()
    }
}
fn network_error(error: reqwest::Error) -> String {
    if error.is_timeout() {
        "google_timeout".into()
    } else {
        "google_network_error".into()
    }
}
fn valid_color(color: &str) -> bool {
    color.len() == 7
        && color.starts_with('#')
        && color.as_bytes()[1..].iter().all(u8::is_ascii_hexdigit)
}

const CATALOG_URL: &str = "https://www.googleapis.com/calendar/v3/users/me/calendarList";
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Page {
    #[serde(default)]
    items: Vec<ApiCalendar>,
    next_page_token: Option<String>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct ApiCalendar {
    id: String,
    summary: Option<String>,
    summary_override: Option<String>,
    background_color: Option<String>,
    #[serde(default)]
    primary: bool,
    access_role: Option<String>,
}

async fn fetch_catalog(
    http: &reqwest::Client,
    access: &str,
    endpoint: &str,
) -> Result<Vec<Calendar>, String> {
    let mut calendars = HashMap::new();
    let mut seen = HashSet::new();
    let mut next = None;
    for _ in 0..100 {
        let mut request = http.get(endpoint).bearer_auth(access).query(&[("maxResults", "250"), ("minAccessRole", "reader"),
            ("fields", "items(id,summary,summaryOverride,backgroundColor,primary,accessRole),nextPageToken")]);
        if let Some(token) = &next {
            request = request.query(&[("pageToken", token)]);
        }
        let mut response = request.send().await.map_err(network_error)?;
        match response.status().as_u16() {
            401 => return Err("google_reconnect_required".into()),
            403 => return Err("google_permission_denied".into()),
            200 => (),
            _ => return Err("google_network_error".into()),
        }
        let mut bytes = Vec::new();
        while let Some(chunk) = response.chunk().await.map_err(network_error)? {
            if bytes.len() + chunk.len() > 2 * 1024 * 1024 {
                return Err("google_catalog_invalid".into());
            }
            bytes.extend_from_slice(&chunk);
        }
        let page: Page = serde_json::from_slice(&bytes).map_err(|_| "google_catalog_invalid")?;
        for item in page.items {
            if item.id.trim().is_empty()
                || matches!(item.access_role.as_deref(), Some("freeBusyReader" | "none"))
            {
                continue;
            }
            let name = item
                .summary_override
                .or(item.summary)
                .filter(|s| !s.trim().is_empty())
                .unwrap_or_else(|| item.id.clone());
            if item.id.len() > 2048 || name.len() > 16_384 {
                return Err("google_catalog_invalid".into());
            }
            let color = item
                .background_color
                .filter(|color| valid_color(color))
                .unwrap_or_else(|| "#71859C".into());
            calendars.insert(
                item.id.clone(),
                Calendar {
                    id: item.id,
                    name,
                    color,
                    primary: item.primary,
                },
            );
            if calendars.len() > 5000 {
                return Err("google_catalog_invalid".into());
            }
        }
        next = page.next_page_token.filter(|token| !token.is_empty());
        if let Some(token) = &next {
            if token.len() > 8192 || !seen.insert(token.clone()) {
                return Err("google_catalog_invalid".into());
            }
        } else {
            let mut values: Vec<Calendar> = calendars.into_values().collect();
            values.sort_by(|a, b| {
                b.primary
                    .cmp(&a.primary)
                    .then_with(|| a.name.to_lowercase().cmp(&b.name.to_lowercase()))
            });
            return Ok(values);
        }
    }
    Err("google_catalog_invalid".into())
}

#[cfg(test)]
mod tests {
    use super::*;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    struct Fixture {
        path: PathBuf,
        root: PathBuf,
    }
    impl Fixture {
        fn new() -> Self {
            let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
                .join("../../artifacts/tauri-google-session-tests");
            std::fs::create_dir_all(&root).unwrap();
            let root = std::fs::canonicalize(root).unwrap();
            let path = root.join(format!(
                "{}-{}",
                std::process::id(),
                oauth2::CsrfToken::new_random().secret()
            ));
            std::fs::create_dir(&path).unwrap();
            Self { path, root }
        }
        fn state(&self) -> GoogleState {
            GoogleState::with_config(self.path.join("google.dpapi"), Some(ClientConfig::parse(
                br#"{"client_id":"fixture.apps.googleusercontent.com","client_secret":"fixture"}"#
            ).unwrap())).unwrap()
        }
    }
    impl Drop for Fixture {
        fn drop(&mut self) {
            let path = std::fs::canonicalize(&self.path).unwrap();
            assert_eq!(path.parent(), Some(self.root.as_path()));
            let _ = std::fs::remove_dir_all(path);
        }
    }
    fn record(name: &str) -> Record {
        Record {
            version: 1,
            client_id: "fixture.apps.googleusercontent.com".into(),
            access_token: "fixture-access".into(),
            refresh_token: "fixture-refresh".into(),
            expires_at_ms: 1,
            calendars: vec![Calendar {
                id: "fixture".into(),
                name: name.into(),
                color: "#123456".into(),
                primary: true,
            }],
            selected_ids: Some(vec![]),
        }
    }
    #[test]
    fn cancellation_blocks_late_saves_and_excludes_another_operation() {
        let f = Fixture::new();
        let state = f.state();
        let operation = state.begin().unwrap();
        assert!(matches!(state.begin(), Err(error) if error=="google_busy"));
        state.cancel();
        assert!(operation.token.is_cancelled());
        assert_eq!(
            state.commit(record("New"), &operation.token).unwrap_err(),
            "google_cancelled"
        );
        assert!(!f.path.join("google.dpapi").exists());
        drop(operation);
        assert!(state.begin().is_ok());
    }

    #[test]
    fn legacy_selection_is_empty_backed_up_and_restored_without_losing_credentials() {
        let f = Fixture::new();
        let path = f.path.join("google.dpapi");
        let mut legacy = record("Original");
        legacy.selected_ids = None;
        let store = GoogleStore::new(path.clone());
        store.save(&legacy).unwrap();
        drop(store);
        let original = std::fs::read(&path).unwrap();
        let state = f.state();
        assert!(state.load().unwrap().selected_ids.is_empty());
        assert_eq!(
            state.set_selected("fixture", true).unwrap().selected_ids,
            ["fixture"]
        );
        assert_eq!(
            state.set_selected("fixture", true).unwrap().selected_ids,
            ["fixture"]
        );
        let backup = path.with_extension("dpapi.before-selection.bak");
        assert_eq!(std::fs::read(&backup).unwrap(), original);
        drop(state);
        let state = f.state();
        let view = state.load().unwrap();
        assert!(view.connected);
        assert_eq!(view.selected_ids, ["fixture"]);
        assert_eq!(view.calendars[0].name, "Original");
        assert_eq!(
            state
                .session
                .lock()
                .unwrap()
                .record
                .as_ref()
                .unwrap()
                .refresh_token,
            "fixture-refresh"
        );
        assert!(
            state
                .set_selected("fixture", false)
                .unwrap()
                .selected_ids
                .is_empty()
        );
        drop(state);
        let state = f.state();
        assert!(state.load().unwrap().selected_ids.is_empty());
        assert_eq!(std::fs::read(&backup).unwrap(), original);
        state.disconnect().unwrap();
        assert!(!path.exists());
        assert!(!backup.exists());
    }

    #[test]
    fn failed_selection_save_and_busy_operation_keep_previous_selection_and_bytes() {
        let f = Fixture::new();
        let state = f.state();
        {
            let operation = state.begin().unwrap();
            state.commit(record("Original"), &operation.token).unwrap();
            assert_eq!(
                state.set_selected("fixture", true).err().unwrap(),
                "google_busy"
            );
        }
        let path = f.path.join("google.dpapi");
        let original = std::fs::read(&path).unwrap();
        assert_eq!(
            state.set_selected("unknown", true).err().unwrap(),
            "google_selection_invalid"
        );
        assert_eq!(std::fs::read(&path).unwrap(), original);
        let backup = path.with_extension("test-backup");
        std::fs::rename(&path, &backup).unwrap();
        std::fs::create_dir(&path).unwrap();
        assert!(state.set_selected("fixture", true).is_err());
        assert!(state.load().unwrap().selected_ids.is_empty());
        assert_eq!(std::fs::read(&backup).unwrap(), original);
        std::fs::remove_dir(path).unwrap();
    }

    #[test]
    fn catalog_changes_keep_selected_ids_but_do_not_auto_select_new_or_other_accounts() {
        let mut saved = record("Original");
        saved.selected_ids = Some(vec!["fixture".into()]);
        let mut renamed = saved.calendars[0].clone();
        renamed.name = "Renamed".into();
        let mut added = renamed.clone();
        added.id = "new".into();
        added.primary = false;
        let next = vec![added.clone(), renamed.clone()];
        assert_eq!(saved.selection_for_reconnection(&next), ["fixture"]);
        saved.replace_calendars(next);
        assert_eq!(saved.selected_ids.as_ref().unwrap(), &["fixture"]);
        let mut other_account = renamed;
        other_account.id = "another-account".into();
        assert!(
            saved
                .selection_for_reconnection(&[other_account])
                .is_empty()
        );
        saved.replace_calendars(vec![added]);
        assert!(saved.selected_ids.as_ref().unwrap().is_empty());
    }

    #[test]
    fn invalid_saved_selection_is_protected() {
        let config = ClientConfig::parse(
            br#"{"client_id":"fixture.apps.googleusercontent.com","client_secret":"fixture"}"#,
        )
        .unwrap();
        for ids in [
            vec!["unknown".into()],
            vec!["fixture".into(), "fixture".into()],
        ] {
            let mut saved = record("Original");
            saved.selected_ids = Some(ids);
            assert!(saved.validate(&config).is_err());
        }
    }

    #[test]
    fn an_existing_different_migration_backup_is_never_overwritten() {
        let f = Fixture::new();
        let path = f.path.join("google.dpapi");
        let mut legacy = record("Original");
        legacy.selected_ids = None;
        let store = GoogleStore::new(path.clone());
        store.save(&legacy).unwrap();
        drop(store);
        let original = std::fs::read(&path).unwrap();
        let backup = path.with_extension("dpapi.before-selection.bak");
        std::fs::write(&backup, b"previous-backup").unwrap();
        let state = f.state();
        assert!(state.set_selected("fixture", true).is_err());
        assert!(state.load().unwrap().selected_ids.is_empty());
        assert_eq!(std::fs::read(&path).unwrap(), original);
        assert_eq!(std::fs::read(&backup).unwrap(), b"previous-backup");
    }
    #[test]
    fn failed_save_keeps_the_previous_in_memory_catalog_and_disk_bytes() {
        let f = Fixture::new();
        let state = f.state();
        let operation = state.begin().unwrap();
        state.commit(record("Old"), &operation.token).unwrap();
        let path = f.path.join("google.dpapi");
        let backup = f.path.join("original.dpapi");
        let original = std::fs::read(&path).unwrap();
        std::fs::rename(&path, &backup).unwrap();
        std::fs::create_dir(&path).unwrap();
        assert!(state.commit(record("New"), &operation.token).is_err());
        assert_eq!(state.load().unwrap().calendars[0].name, "Old");
        assert_eq!(std::fs::read(&backup).unwrap(), original);
        std::fs::remove_dir(path).unwrap();
    }
    #[tokio::test]
    async fn repeated_page_token_and_permission_failure_do_not_return_partial_data() {
        for (status, body, count, expected) in [
            (
                "200 OK",
                r#"{"items":[{"id":"fixture","summary":"Partial"}],"nextPageToken":"same"}"#,
                2,
                "google_catalog_invalid",
            ),
            (
                "403 Forbidden",
                "fixture-private-error-must-not-escape",
                1,
                "google_permission_denied",
            ),
        ] {
            let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
            let address = listener.local_addr().unwrap();
            let server = tokio::spawn(async move {
                for _ in 0..count {
                    let (mut stream, _) = listener.accept().await.unwrap();
                    let mut bytes = [0; 8192];
                    let count = stream.read(&mut bytes).await.unwrap();
                    assert!(count > 0);
                    stream.write_all(format!("HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",body.len()).as_bytes()).await.unwrap();
                }
            });
            let error = fetch_catalog(
                &reqwest::Client::new(),
                "fixture-access",
                &format!("http://{address}/catalog"),
            )
            .await
            .unwrap_err();
            server.await.unwrap();
            assert_eq!(error, expected);
        }
    }
    #[tokio::test]
    async fn catalog_pagination_deduplication_color_and_overrides() {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = listener.local_addr().unwrap();
        let server = tokio::spawn(async move {
            for (index, body) in [r##"{"items":[{"id":"two","summary":"Old","backgroundColor":"bad"},{"id":"skip","accessRole":"freeBusyReader"}],"nextPageToken":"next"}"##,
                r##"{"items":[{"id":"two","summary":"New","backgroundColor":"#123456"},{"id":"one","summary":"Name","summaryOverride":"Primary","primary":true}]}"##].iter().enumerate() {
                let (mut stream, _) = listener.accept().await.unwrap();
                let mut request = vec![0;8192]; let n = stream.read(&mut request).await.unwrap();
                let text = String::from_utf8_lossy(&request[..n]);
                assert!(text.contains("Bearer fixture-access"));
                if index == 1 { assert!(text.contains("pageToken=next")); }
                stream.write_all(format!("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",body.len()).as_bytes()).await.unwrap();
            }
        });
        let calendars = fetch_catalog(
            &reqwest::Client::new(),
            "fixture-access",
            &format!("http://{address}/catalog"),
        )
        .await
        .unwrap();
        server.await.unwrap();
        assert_eq!(calendars.len(), 2);
        assert_eq!(calendars[0].name, "Primary");
        assert_eq!(calendars[0].color, "#71859C");
        assert_eq!(calendars[1].name, "New");
        assert_eq!(calendars[1].color, "#123456");
    }
    #[test]
    fn wrong_client_and_invalid_saved_records_are_rejected() {
        let config = ClientConfig::parse(
            br#"{"client_id":"fixture.apps.googleusercontent.com","client_secret":"fixture"}"#,
        )
        .unwrap();
        let mut record = Record {
            version: 1,
            client_id: "other".into(),
            access_token: "fixture".into(),
            refresh_token: "fixture".into(),
            expires_at_ms: 1,
            calendars: vec![],
            selected_ids: Some(vec![]),
        };
        assert_eq!(
            record.validate(&config).unwrap_err(),
            "google_client_changed"
        );
        record.client_id = config.client_id.clone();
        assert!(record.validate(&config).is_ok());
        record.refresh_token.clear();
        assert!(record.validate(&config).is_err());
    }
}
