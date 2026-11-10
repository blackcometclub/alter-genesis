mod google;
mod google_auth;
mod google_events;
mod google_queue;
mod google_runtime;
mod google_store;
mod notification;
mod notification_store;
mod settings;
mod sound;
mod startup;
mod tray;

use google::{GoogleState, GoogleView};
use notification::{NotificationCard, NotificationState, unix_now_ms};
use settings::{AppSettings, CardSize, SettingsState};
use sound::SoundState;
use std::path::PathBuf;
use std::sync::{
    Arc,
    atomic::{AtomicBool, Ordering},
};
use std::time::Duration;
use tauri::{
    AppHandle, Manager, PhysicalPosition, WebviewUrl, WebviewWindow, WebviewWindowBuilder,
    WindowEvent,
};
use tauri_plugin_opener::OpenerExt;

#[tauri::command]
fn load_settings(state: tauri::State<'_, SettingsState>) -> Result<AppSettings, String> {
    state.load()
}

#[tauri::command]
fn save_settings(
    app: AppHandle,
    state: tauri::State<'_, SettingsState>,
    settings: AppSettings,
) -> Result<AppSettings, String> {
    let saved = state.save(settings)?;
    tray::language_changed(&app, &saved.language);
    if let Ok(now) = unix_now_ms() {
        app.state::<SoundState>()
            .configure(&saved, || notification_visible(&app), now);
    }
    if let Some(panel) = app.get_webview_window("google-notifications") {
        use tauri::Emitter;
        let _ = panel.emit("settings-changed", &saved);
        let _ = google_runtime::render(&app, false);
    }
    Ok(saved)
}

#[tauri::command]
fn preview_sound(window: WebviewWindow, state: tauri::State<'_, SoundState>) -> Result<(), String> {
    if window.label() != "main" {
        return Err("sound_window_required".into());
    }
    state.preview()
}

fn google_window(window: &WebviewWindow) -> Result<(), String> {
    if window.label() == "main" {
        Ok(())
    } else {
        Err("google_window_required".into())
    }
}
fn google_size(window: &WebviewWindow, view: &GoogleView) {
    let height = if view.connected { 648.0 } else { 548.0 };
    let _ = window.set_size(tauri::LogicalSize::new(408.0, height));
}
#[tauri::command]
fn load_google(
    app: AppHandle,
    window: WebviewWindow,
    state: tauri::State<'_, GoogleState>,
) -> Result<GoogleView, String> {
    google_window(&window)?;
    let _ = &state;
    let view = google_runtime::view(&app)?;
    google_size(&window, &view);
    Ok(view)
}
#[tauri::command]
async fn connect_google(
    app: AppHandle,
    window: WebviewWindow,
    state: tauri::State<'_, GoogleState>,
) -> Result<GoogleView, String> {
    google_window(&window)?;
    let runtime = app.state::<google_runtime::Runtime>();
    runtime.cancel_pending(&app);
    let _guard = runtime.coordinator.lock().await;
    let browser_app = app.clone();
    let result = state
        .connect(move |url| {
            browser_app
                .opener()
                .open_url(url, None::<&str>)
                .map_err(|_| "google_browser_failed".into())
        })
        .await;
    let result = match result {
        Ok(_) => google_runtime::sync(&app, false).await,
        Err(error) => Err(error),
    };
    runtime.error(&app, result.as_ref().err().cloned());
    result?;
    let view = google_runtime::view(&app)?;
    google_size(&window, &view);
    Ok(view)
}
#[tauri::command]
async fn refresh_google(
    app: AppHandle,
    window: WebviewWindow,
    state: tauri::State<'_, GoogleState>,
) -> Result<GoogleView, String> {
    google_window(&window)?;
    let _ = &state;
    let runtime = app.state::<google_runtime::Runtime>();
    runtime.cancel_pending(&app);
    let _guard = runtime.coordinator.lock().await;
    let result = google_runtime::sync(&app, true).await;
    let _ = google_runtime::render(&app, false);
    runtime.error(&app, result.as_ref().err().cloned());
    result?;
    let view = google_runtime::view(&app)?;
    google_size(&window, &view);
    Ok(view)
}
#[tauri::command]
fn cancel_google(
    app: AppHandle,
    window: WebviewWindow,
    state: tauri::State<'_, GoogleState>,
) -> Result<(), String> {
    google_window(&window)?;
    let _ = &state;
    app.state::<google_runtime::Runtime>().cancel_pending(&app);
    Ok(())
}
#[tauri::command]
async fn disconnect_google(
    app: AppHandle,
    window: WebviewWindow,
    state: tauri::State<'_, GoogleState>,
) -> Result<GoogleView, String> {
    google_window(&window)?;
    let runtime = app.state::<google_runtime::Runtime>();
    runtime.cancel_pending(&app);
    let _guard = runtime.coordinator.lock().await;
    state.disconnect()?;
    let result = app.state::<google_queue::GoogleQueue>().clear();
    let _ = google_runtime::render(&app, false);
    runtime.error(&app, result.err());
    let view = google_runtime::view(&app)?;
    google_size(&window, &view);
    Ok(view)
}

#[tauri::command]
async fn set_google_calendar_selected(
    app: AppHandle,
    window: WebviewWindow,
    state: tauri::State<'_, GoogleState>,
    id: String,
    selected: bool,
) -> Result<GoogleView, String> {
    google_window(&window)?;
    let runtime = app.state::<google_runtime::Runtime>();
    runtime.cancel_pending(&app);
    let guard = runtime.coordinator.lock().await;
    state.set_selected(&id, selected)?;
    let result = app
        .state::<google_queue::GoogleQueue>()
        .retain_sources(&google_runtime::selected(&app));
    let _ = google_runtime::render(&app, false);
    runtime.error(&app, result.err());
    let view = google_runtime::view(&app)?;
    drop(guard);
    google_runtime::request(app.clone());
    Ok(view)
}

#[tauri::command]
fn load_google_notifications(
    app: AppHandle,
    window: WebviewWindow,
) -> Result<Vec<google_queue::CardView>, String> {
    if window.label() != "google-notifications" {
        return Err("notification_window_required".into());
    }
    Ok(google_runtime::cards(&app))
}
#[tauri::command]
async fn act_google_notification(
    app: AppHandle,
    window: WebviewWindow,
    id: String,
    snooze: bool,
) -> Result<(), String> {
    if window.label() != "google-notifications" {
        return Err("notification_window_required".into());
    }
    app.state::<google_queue::GoogleQueue>()
        .act(&id, snooze, unix_now_ms()?)?;
    google_runtime::render(&app, false)?;
    google_runtime::report(&app, "card_action");
    Ok(())
}

fn notification_visible(app: &AppHandle) -> bool {
    app.state::<NotificationState>()
        .load()
        .ok()
        .flatten()
        .is_some()
        || app
            .state::<google_queue::GoogleQueue>()
            .has_visible(&google_runtime::selected(app))
}

fn sound_presented(app: &AppHandle, play_initial: bool) {
    if let Err(error) = unix_now_ms().and_then(|now| {
        app.state::<SoundState>()
            .presented(now, play_initial, || notification_visible(app))
    }) {
        eprintln!("{error}");
    }
}

fn start_sound_timer(app: AppHandle) {
    tauri::async_runtime::spawn(async move {
        loop {
            tokio::time::sleep(Duration::from_secs(1)).await;
            if let Err(error) = unix_now_ms().and_then(|now| {
                app.state::<SoundState>()
                    .tick(|| notification_visible(&app), now)
            }) {
                eprintln!("{error}");
            }
        }
    });
}

#[tauri::command]
fn load_notification(
    state: tauri::State<'_, NotificationState>,
) -> Result<Option<NotificationCard>, String> {
    state.panel_card()
}

#[tauri::command]
fn snooze_notification(
    app: AppHandle,
    window: WebviewWindow,
    state: tauri::State<'_, NotificationState>,
    id: String,
) -> Result<(), String> {
    if window.label() != "notification-panel" {
        return Err("notification_window_required".into());
    }
    // Hide first so a window failure leaves the active notification intact.
    window.hide().map_err(|error| error.to_string())?;
    let generation = match unix_now_ms().and_then(|now| state.snooze(&id, now)) {
        Ok(token) => token,
        Err(error) => {
            let _ = window.show();
            return Err(error);
        }
    };
    app.state::<SoundState>().pause();
    start_snooze_timer(app, window, generation);
    Ok(())
}

fn start_snooze_timer(app: AppHandle, window: WebviewWindow, generation: u64) {
    tauri::async_runtime::spawn(async move {
        loop {
            let result = unix_now_ms()
                .and_then(|now| app.state::<NotificationState>().remaining(generation, now));
            match result {
                Ok(Some(wait)) if wait.is_zero() => {
                    match unix_now_ms().and_then(|now| {
                        app.state::<NotificationState>()
                            .restore_due(generation, now)
                    }) {
                        Ok(true) => {
                            if let Err(error) = window.show() {
                                eprintln!("notification_show_failed: {error}");
                            } else {
                                sound_presented(&app, true);
                            }
                            break;
                        }
                        Ok(false) => continue,
                        Err(error) => {
                            eprintln!("{error}");
                            break;
                        }
                    }
                }
                // Recheck the wall clock after suspend or a system-clock adjustment.
                Ok(Some(wait)) => tokio::time::sleep(wait.min(Duration::from_secs(1))).await,
                Ok(None) => break,
                Err(error) => {
                    eprintln!("{error}");
                    break;
                }
            }
        }
    });
}

#[tauri::command]
fn dismiss_notification(
    app: AppHandle,
    state: tauri::State<'_, NotificationState>,
    id: String,
) -> Result<(), String> {
    let preview_mode = state.dismiss(&id)?;
    app.state::<SoundState>().pause();

    if preview_mode && let Some(main) = app.get_webview_window("main") {
        main.close().map_err(|error| error.to_string())?;
    }

    Ok(())
}

fn settings_path(app: &tauri::App) -> Result<PathBuf, Box<dyn std::error::Error>> {
    if let Some(path) = std::env::var_os("ALTER_GENESIS_SETTINGS_PATH") {
        return Ok(PathBuf::from(path));
    }

    Ok(app.path().app_local_data_dir()?.join("settings.json"))
}

fn notification_preview_requested() -> bool {
    std::env::args().any(|argument| argument == "--notification-preview")
}

fn notification_window_size(card_size: &CardSize) -> (f64, f64) {
    match card_size {
        CardSize::Small => (376.0, 130.0),
        CardSize::Large => (560.0, 154.0),
    }
}

fn position_at_work_area_bottom_right(window: &WebviewWindow) -> tauri::Result<()> {
    let Some(monitor) = window.primary_monitor()? else {
        return Ok(());
    };

    let work_area = monitor.work_area();
    let size = window.outer_size()?;
    let margin = (24.0 * monitor.scale_factor()).round() as i32;
    let width = i32::try_from(size.width).unwrap_or(i32::MAX);
    let height = i32::try_from(size.height).unwrap_or(i32::MAX);
    let right = work_area.position.x + i32::try_from(work_area.size.width).unwrap_or(i32::MAX);
    let bottom = work_area.position.y + i32::try_from(work_area.size.height).unwrap_or(i32::MAX);
    let x = (right - width - margin).max(work_area.position.x);
    let y = (bottom - height - margin).max(work_area.position.y);

    window.set_position(PhysicalPosition::new(x, y))
}

fn open_notification_preview(
    app: &tauri::App,
    card_size: &CardSize,
    visible: bool,
) -> Result<(), Box<dyn std::error::Error>> {
    if let Some(main) = app.get_webview_window("main") {
        main.hide()?;
    }

    let (width, height) = notification_window_size(card_size);
    let panel = WebviewWindowBuilder::new(
        app,
        "notification-panel",
        WebviewUrl::App("index.html".into()),
    )
    .title("ALTER GENESIS")
    .inner_size(width, height)
    .resizable(false)
    .decorations(false)
    .always_on_top(true)
    .skip_taskbar(true)
    .focused(false)
    .shadow(true)
    .visible(false)
    .build()?;

    position_at_work_area_bottom_right(&panel)?;

    let app_handle = app.handle().clone();
    panel.on_window_event(move |event| {
        if matches!(event, WindowEvent::Destroyed) {
            app_handle.exit(0);
        }
    });

    if visible {
        panel.show()?;
        sound_presented(app.handle(), app.state::<NotificationState>().sound_on_open);
    }
    if let Some(generation) = app.state::<NotificationState>().pending_generation() {
        start_snooze_timer(app.handle().clone(), panel, generation);
    }
    Ok(())
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let preview_requested = notification_preview_requested();
    let startup_requested = startup::requested(std::env::args().skip(1));
    let reopen_requested = Arc::new(AtomicBool::new(false));
    let mut builder = tauri::Builder::default();
    if !preview_requested {
        let reopen_on_second = Arc::clone(&reopen_requested);
        // Register first: later launches must exit before stores, windows, and tray setup.
        builder = builder.plugin(tauri_plugin_single_instance::init(
            move |app, args, _cwd| {
                if startup::requested(args.into_iter().skip(1)) {
                    return;
                }
                reopen_on_second.store(true, Ordering::SeqCst);
                if tray::show_settings(app) {
                    reopen_on_second.store(false, Ordering::SeqCst);
                }
            },
        ));
    }

    builder
        .plugin(tauri_plugin_opener::init())
        .setup(move |app| {
            let path = settings_path(app)?;
            let notification_path = path.with_file_name("notifications.json");
            let google_path = path.with_file_name("google.dpapi");
            let google_queue_path = path.with_file_name("google-notifications.dpapi");
            let settings_state = SettingsState::new(path);
            let loaded_settings = settings_state.load();
            let current_settings = loaded_settings.clone().unwrap_or_default();
            let sound_settings = if loaded_settings.is_ok() {
                current_settings.clone()
            } else {
                AppSettings {
                    notification_sound: settings::NotificationSound::None,
                    ..current_settings.clone()
                }
            };
            let preview_notification = preview_requested
                .then(|| NotificationCard::preview(current_settings.card_size.clone()));

            app.manage(settings_state);
            app.manage(GoogleState::new(google_path)?);
            app.manage(google_queue::GoogleQueue::new(google_queue_path));
            app.manage(google_runtime::Runtime::new());
            app.manage(SoundState::new(&sound_settings));
            let notification_state = NotificationState::open(
                notification_path,
                preview_notification,
                preview_requested,
                unix_now_ms()?,
            );
            let panel_card = notification_state.panel_card().ok().flatten();
            let visible = notification_state.load().ok().flatten().is_some();
            app.manage(notification_state);

            if let Some(card) = panel_card {
                open_notification_preview(app, &card.card_size, visible)?;
            }
            start_sound_timer(app.handle().clone());
            if !preview_requested {
                tray::start(app.handle(), &current_settings.language)?;
                google_runtime::start(app.handle().clone())?;
                if reopen_requested.swap(false, Ordering::SeqCst) || !startup_requested {
                    tray::show_settings(app.handle());
                }
            }

            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            startup::load_startup,
            startup::set_startup,
            load_settings,
            save_settings,
            preview_sound,
            load_google,
            connect_google,
            refresh_google,
            cancel_google,
            disconnect_google,
            set_google_calendar_selected,
            load_google_notifications,
            act_google_notification,
            load_notification,
            dismiss_notification,
            snooze_notification
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn notification_window_sizes_match_the_confirmed_wpf_presets() {
        assert_eq!(notification_window_size(&CardSize::Small), (376.0, 130.0));
        assert_eq!(notification_window_size(&CardSize::Large), (560.0, 154.0));
    }
}
