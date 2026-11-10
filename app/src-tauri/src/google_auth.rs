use oauth2::{
    AuthType, AuthUrl, ClientId, ClientSecret, CsrfToken, EndpointNotSet, EndpointSet, RedirectUrl,
    Scope, TokenUrl, basic::BasicClient,
};
use serde::Deserialize;
use std::time::Duration;
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    net::TcpListener,
};
use zeroize::Zeroize;

pub const LIST_SCOPE: &str = "https://www.googleapis.com/auth/calendar.calendarlist.readonly";
pub type GoogleClient =
    BasicClient<EndpointSet, EndpointNotSet, EndpointNotSet, EndpointNotSet, EndpointSet>;

#[derive(Clone, Deserialize)]
pub struct ClientConfig {
    pub client_id: String,
    client_secret: String,
}
impl Drop for ClientConfig {
    fn drop(&mut self) {
        self.client_secret.zeroize();
    }
}
impl ClientConfig {
    pub fn parse(bytes: &[u8]) -> Result<Self, String> {
        if bytes.len() > 16_384 {
            return Err("google_not_configured".into());
        }
        let value: serde_json::Value =
            serde_json::from_slice(bytes).map_err(|_| "google_not_configured")?;
        if value.get("web").is_some() {
            return Err("google_not_configured".into());
        }
        let selected = value.get("installed").unwrap_or(&value).clone();
        let config: Self = serde_json::from_value(selected).map_err(|_| "google_not_configured")?;
        if !config.client_id.ends_with(".apps.googleusercontent.com")
            || config.client_id.len() > 512
            || config.client_id.bytes().any(|b| b.is_ascii_whitespace())
            || config.client_secret.is_empty()
            || config.client_secret.len() > 1024
        {
            return Err("google_not_configured".into());
        }
        Ok(config)
    }
    pub fn embedded() -> Result<Self, String> {
        Self::parse(include_bytes!(concat!(
            env!("OUT_DIR"),
            "/google-client.json"
        )))
    }
    pub fn client(&self, redirect: Option<String>) -> Result<GoogleClient, String> {
        let client = BasicClient::new(ClientId::new(self.client_id.clone()))
            .set_client_secret(ClientSecret::new(self.client_secret.clone()))
            .set_auth_type(AuthType::RequestBody)
            .set_auth_uri(
                AuthUrl::new("https://accounts.google.com/o/oauth2/v2/auth".into())
                    .map_err(|_| "google_auth_failed")?,
            )
            .set_token_uri(
                TokenUrl::new("https://oauth2.googleapis.com/token".into())
                    .map_err(|_| "google_auth_failed")?,
            );
        match redirect {
            Some(uri) => {
                Ok(client
                    .set_redirect_uri(RedirectUrl::new(uri).map_err(|_| "google_auth_failed")?))
            }
            None => Ok(client),
        }
    }
}

pub fn authorization(client: &GoogleClient) -> (url::Url, CsrfToken, oauth2::PkceCodeVerifier) {
    let (challenge, verifier) = oauth2::PkceCodeChallenge::new_random_sha256();
    let (url, state) = client
        .authorize_url(CsrfToken::new_random)
        .add_scope(Scope::new(LIST_SCOPE.into()))
        .add_scope(Scope::new(crate::google_events::EVENTS_SCOPE.into()))
        .set_pkce_challenge(challenge)
        .add_extra_param("access_type", "offline")
        .add_extra_param("prompt", "select_account consent")
        .url();
    (url, state, verifier)
}

// Ignore unrelated/local forged requests without consuming the one expected login response.
fn callback(request: &str, host: &str, expected: &CsrfToken) -> Option<Result<String, String>> {
    let mut lines = request.split("\r\n");
    let first = lines.next()?;
    let mut parts = first.split_whitespace();
    if parts.next()? != "GET" {
        return None;
    }
    let target = parts.next()?;
    if !target.starts_with("/oauth2callback?") {
        return None;
    }
    let hosts: Vec<_> = lines
        .filter_map(|line| line.split_once(':'))
        .filter(|(key, _)| key.eq_ignore_ascii_case("host"))
        .map(|(_, value)| value.trim())
        .collect();
    if hosts != [host] {
        return None;
    }
    let url = url::Url::parse(&format!("http://{host}{target}")).ok()?;
    if url.path() != "/oauth2callback" {
        return None;
    }
    let pairs: Vec<_> = url.query_pairs().collect();
    let values = |key: &str| {
        pairs
            .iter()
            .filter(|(k, _)| k == key)
            .map(|(_, v)| v.as_ref())
            .collect::<Vec<_>>()
    };
    let states = values("state");
    if states.len() != 1 || CsrfToken::new(states[0].to_owned()) != *expected {
        return None;
    }
    let codes = values("code");
    let errors = values("error");
    if errors.len() == 1 && codes.is_empty() {
        return Some(Err(if errors[0] == "access_denied" {
            "google_cancelled"
        } else {
            "google_auth_failed"
        }
        .into()));
    }
    if errors.is_empty() && codes.len() == 1 && !codes[0].is_empty() && codes[0].len() < 4096 {
        Some(Ok(codes[0].to_owned()))
    } else {
        None
    }
}

pub async fn receive_code(listener: TcpListener, state: CsrfToken) -> Result<String, String> {
    let host = listener
        .local_addr()
        .map_err(|_| "google_callback_failed")?
        .to_string();
    let wait = async {
        for _ in 0..128 {
            let (mut stream, peer) = listener
                .accept()
                .await
                .map_err(|_| "google_callback_failed")?;
            if !peer.ip().is_loopback() {
                continue;
            }
            let mut bytes = Vec::new();
            let read = tokio::time::timeout(Duration::from_secs(4), async {
                let mut chunk = [0u8; 1024];
                while bytes.len() < 16_384 && !bytes.windows(4).any(|w| w == b"\r\n\r\n") {
                    let count = stream.read(&mut chunk).await?;
                    if count == 0 {
                        break;
                    }
                    bytes.extend_from_slice(&chunk[..count]);
                }
                Ok::<_, std::io::Error>(())
            })
            .await;
            if !matches!(read, Ok(Ok(()))) {
                continue;
            }
            let result = if bytes.len() < 16_384 {
                std::str::from_utf8(&bytes)
                    .ok()
                    .and_then(|r| callback(r, &host, &state))
            } else {
                None
            };
            let valid = result.is_some();
            let body = if valid {
                "<!doctype html><html lang=\"ja\"><meta charset=\"utf-8\"><title>ALTER GENESIS</title><body><h1>ALTER GENESIS</h1><p>ログイン結果を受け取りました。アプリに戻ってください。</p><p>You can close this tab and return to the app.</p></body></html>"
            } else {
                "Invalid callback."
            };
            let status = if valid { "200 OK" } else { "400 Bad Request" };
            let response = format!(
                "HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {}\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'; base-uri 'none'; frame-ancestors 'none'\r\nConnection: close\r\n\r\n{body}",
                body.len()
            );
            let _ = tokio::time::timeout(
                Duration::from_secs(2),
                stream.write_all(response.as_bytes()),
            )
            .await;
            if let Some(result) = result {
                return result;
            }
        }
        Err("google_callback_failed".into())
    };
    tokio::time::timeout(Duration::from_secs(600), wait)
        .await
        .map_err(|_| "google_login_timeout".to_owned())?
}

#[cfg(test)]
mod tests {
    use super::*;
    fn config() -> ClientConfig {
        ClientConfig::parse(
            br#"{"client_id":"fixture.apps.googleusercontent.com","client_secret":"fixture"}"#,
        )
        .unwrap()
    }
    #[test]
    fn authorization_uses_pkce_and_readonly_list_and_event_scopes() {
        let client = config()
            .client(Some("http://127.0.0.1:54321/oauth2callback".into()))
            .unwrap();
        let (url, state, verifier) = authorization(&client);
        let query: std::collections::HashMap<_, _> = url.query_pairs().collect();
        assert_eq!(url.host_str(), Some("accounts.google.com"));
        let scopes: std::collections::HashSet<_> = query.get("scope").unwrap().split(' ').collect();
        assert_eq!(
            scopes,
            [LIST_SCOPE, crate::google_events::EVENTS_SCOPE]
                .into_iter()
                .collect()
        );
        assert_eq!(query.get("code_challenge_method").unwrap(), "S256");
        assert_eq!(query.get("state").unwrap(), state.secret());
        assert!(verifier.secret().len() >= 43);
        assert_eq!(query.get("access_type").unwrap(), "offline");
    }
    #[test]
    fn callback_requires_exact_host_state_path_and_single_code() {
        let state = CsrfToken::new("expected-state".into());
        let make = |target: &str| format!("GET {target} HTTP/1.1\r\nHost: 127.0.0.1:54321\r\n\r\n");
        assert_eq!(
            callback(
                &make("/oauth2callback?state=expected-state&code=fixture"),
                "127.0.0.1:54321",
                &state
            )
            .unwrap()
            .unwrap(),
            "fixture"
        );
        for target in [
            "/oauth2callback?code=fixture",
            "/oauth2callback?state=wrong&code=fixture",
            "/oauth2callback?state=expected-state&code=a&code=b",
            "/favicon.ico?state=expected-state&code=a",
            "/oauth2callback?state=expected-state&code=a&error=access_denied",
        ] {
            assert!(callback(&make(target), "127.0.0.1:54321", &state).is_none());
        }
        assert!(
            callback(
                &make("/oauth2callback?state=expected-state&code=fixture"),
                "localhost:54321",
                &state
            )
            .is_none()
        );
        assert_eq!(
            callback(
                &make("/oauth2callback?state=expected-state&error=access_denied"),
                "127.0.0.1:54321",
                &state
            )
            .unwrap()
            .unwrap_err(),
            "google_cancelled"
        );
    }
    #[tokio::test]
    async fn unrelated_request_does_not_consume_the_expected_loopback_response() {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = listener.local_addr().unwrap();
        let server = tokio::spawn(receive_code(listener, CsrfToken::new("expected".into())));
        for (state, status) in [("wrong", "400"), ("expected", "200")] {
            let mut socket = tokio::net::TcpStream::connect(address).await.unwrap();
            socket.write_all(format!("GET /oauth2callback?state={state}&code=fixture HTTP/1.1\r\nHost: {address}\r\n\r\n").as_bytes()).await.unwrap();
            let mut response = String::new();
            socket.read_to_string(&mut response).await.unwrap();
            assert!(response.starts_with(&format!("HTTP/1.1 {status}")));
            assert!(!response.contains("code=fixture"));
        }
        assert_eq!(server.await.unwrap().unwrap(), "fixture");
    }
}
