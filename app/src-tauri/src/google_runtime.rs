use crate::{
    google::{GoogleState, GoogleView},
    google_queue::GoogleQueue,
    notification::unix_now_ms,
    settings::{CardSize, SettingsState},
    sound::SoundState,
};
use std::{
    sync::{
        Mutex,
        atomic::{AtomicU64, Ordering},
    },
    time::Duration,
};
use tauri::{AppHandle, Emitter, Manager, WebviewUrl, WebviewWindowBuilder, WindowEvent};

pub struct Runtime {
    pub coordinator: tokio::sync::Mutex<()>,
    pub generation: AtomicU64,
    error: Mutex<Option<String>>,
}
impl Runtime {
    pub fn new() -> Self {
        Self {
            coordinator: tokio::sync::Mutex::new(()),
            generation: AtomicU64::new(0),
            error: Mutex::new(None),
        }
    }
    pub fn cancel_pending(&self, app: &AppHandle) {
        self.generation.fetch_add(1, Ordering::SeqCst);
        app.state::<GoogleState>().cancel();
    }
    pub fn error(&self, app: &AppHandle, value: Option<String>) {
        if let Ok(mut error) = self.error.lock() {
            *error = value.filter(|v| v != "google_cancelled");
        }
        if let Ok(view) = view(app) {
            let _ = app.emit_to("main", "google-view-changed", view);
        }
        report(app, "state");
    }
}
pub fn view(app: &AppHandle) -> Result<GoogleView, String> {
    let mut view = app.state::<GoogleState>().load()?;
    view.sync_error = app.state::<GoogleQueue>().error().or_else(|| {
        app.state::<Runtime>()
            .error
            .lock()
            .ok()
            .and_then(|v| v.clone())
    });
    Ok(view)
}
pub fn selected(app: &AppHandle) -> Vec<String> {
    app.state::<GoogleState>().selected_ids()
}
pub fn cards(app: &AppHandle) -> Vec<crate::google_queue::CardView> {
    let settings = app.state::<SettingsState>().load().unwrap_or_default();
    app.state::<GoogleQueue>()
        .visible(&selected(app), &settings.card_size)
}
pub fn render(app: &AppHandle, fresh: bool) -> Result<(), String> {
    let cards = cards(app);
    let Some(panel) = app.get_webview_window("google-notifications") else {
        return Ok(());
    };
    let _ = panel.emit("google-notifications-changed", &cards);
    if cards.is_empty() {
        panel.hide().map_err(|_| "notification_window_failed")?;
        if !crate::notification_visible(app) {
            app.state::<SoundState>().pause();
        }
        return Ok(());
    }
    let size = cards[0].card.card_size.clone();
    let (width, row) = match size {
        CardSize::Small => (376.0, 130.0),
        CardSize::Large => (560.0, 154.0),
    };
    let monitor = panel
        .current_monitor()
        .ok()
        .flatten()
        .or_else(|| panel.primary_monitor().ok().flatten());
    let height = monitor
        .as_ref()
        .map(|m| {
            (cards.len() as f64 * row)
                .min((m.work_area().size.height as f64 / m.scale_factor() - 48.0).max(row))
        })
        .unwrap_or(row);
    panel
        .set_size(tauri::LogicalSize::new(width, height))
        .map_err(|_| "notification_window_failed")?;
    crate::position_at_work_area_bottom_right(&panel).map_err(|_| "notification_window_failed")?;
    let was_visible = panel.is_visible().unwrap_or(false);
    panel.show().map_err(|_| "notification_window_failed")?;
    if fresh || !was_visible {
        crate::sound_presented(app, fresh);
    }
    if fresh {
        report(app, "presented");
    }
    Ok(())
}
pub async fn sync(app: &AppHandle, refresh: bool) -> Result<(), String> {
    if let Some(error) = app.state::<GoogleQueue>().error() {
        return Err(error);
    }
    if !app.state::<GoogleState>().load()?.connected {
        return Ok(());
    }
    if refresh {
        app.state::<GoogleState>().refresh().await?;
    }
    let now = unix_now_ms()?;
    let queue = app.state::<GoogleQueue>();
    let incoming = app
        .state::<GoogleState>()
        .read_notifications(now, queue.earliest(now))
        .await?;
    let selected = selected(app);
    queue.merge(incoming, &selected, now)?;
    let count = queue.tick(&selected, unix_now_ms()?)?;
    render(app, count > 0)?;
    report(app, "sync");
    Ok(())
}
pub fn report(app: &AppHandle, reason: &'static str) {
    if std::env::var("ALTER_GENESIS_GOOGLE_DIAGNOSTICS").as_deref() != Ok("1") {
        return;
    }
    let counts = app.state::<GoogleQueue>().counts();
    if let Ok(view) = view(app) {
        eprintln!(
            "google_runtime time={} reason={} connected={} selected={} scheduled={} visible={} snoozed={} dismissed={} error={}",
            unix_now_ms().unwrap_or(0),
            reason,
            view.connected,
            view.selected_ids.len(),
            counts[0],
            counts[1],
            counts[2],
            counts[3],
            view.error.or(view.sync_error).as_deref().unwrap_or("none")
        );
    }
}
pub fn request(app: AppHandle) {
    let generation = app
        .state::<Runtime>()
        .generation
        .fetch_add(1, Ordering::SeqCst)
        + 1;
    tauri::async_runtime::spawn(async move {
        tokio::time::sleep(Duration::from_millis(250)).await;
        let runtime = app.state::<Runtime>();
        let _guard = runtime.coordinator.lock().await;
        if runtime.generation.load(Ordering::SeqCst) != generation {
            return;
        }
        let result = sync(&app, true).await;
        let _ = render(&app, false);
        runtime.error(&app, result.err());
    });
}
pub fn start(app: AppHandle) -> Result<(), Box<dyn std::error::Error>> {
    let panel = WebviewWindowBuilder::new(
        &app,
        "google-notifications",
        WebviewUrl::App("index.html".into()),
    )
    .title("ALTER GENESIS")
    .inner_size(376.0, 130.0)
    .resizable(false)
    .decorations(false)
    .always_on_top(true)
    .skip_taskbar(true)
    .focused(false)
    .shadow(true)
    .visible(false)
    .build()?;
    panel.on_window_event(|event| {
        if let WindowEvent::CloseRequested { api, .. } = event {
            api.prevent_close();
        }
    });
    if let Some(main) = app.get_webview_window("main") {
        let handle = app.clone();
        main.on_window_event(move |event| {
            if matches!(event, WindowEvent::CloseRequested { .. }) {
                handle.exit(0);
            }
        });
    }
    let _ = render(&app, false);
    request(app.clone());
    tauri::async_runtime::spawn(async move {
        let mut last_poll = std::time::Instant::now();
        loop {
            tokio::time::sleep(Duration::from_secs(1)).await;
            let runtime = app.state::<Runtime>();
            if let Ok(_guard) = runtime.coordinator.try_lock()
                && app.state::<GoogleQueue>().error().is_none()
            {
                match unix_now_ms()
                    .and_then(|now| app.state::<GoogleQueue>().tick(&selected(&app), now))
                {
                    Ok(count) if count > 0 => {
                        let _ = render(&app, true);
                    }
                    Err(error) => app.state::<Runtime>().error(&app, Some(error)),
                    _ => (),
                }
            }
            if last_poll.elapsed() >= Duration::from_secs(300) {
                last_poll = std::time::Instant::now();
                request(app.clone());
            }
        }
    });
    Ok(())
}
