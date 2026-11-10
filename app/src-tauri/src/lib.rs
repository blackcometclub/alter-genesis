mod notification;
mod notification_store;
mod settings;

use notification::{NotificationCard, NotificationState, unix_now_ms};
use settings::{AppSettings, CardSize, SettingsState};
use std::path::PathBuf;
use std::time::Duration;
use tauri::{
    AppHandle, Manager, PhysicalPosition, WebviewUrl, WebviewWindow, WebviewWindowBuilder,
    WindowEvent,
};

#[tauri::command]
fn load_settings(state: tauri::State<'_, SettingsState>) -> Result<AppSettings, String> {
    state.load()
}

#[tauri::command]
fn save_settings(
    state: tauri::State<'_, SettingsState>,
    settings: AppSettings,
) -> Result<AppSettings, String> {
    state.save(settings)
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
    }
    if let Some(generation) = app.state::<NotificationState>().pending_generation() {
        start_snooze_timer(app.handle().clone(), panel, generation);
    }
    Ok(())
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let preview_requested = notification_preview_requested();

    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .setup(move |app| {
            let path = settings_path(app)?;
            let notification_path = path.with_file_name("notifications.json");
            let settings_state = SettingsState::new(path);
            let current_settings = settings_state.load().unwrap_or_default();
            let preview_notification = preview_requested
                .then(|| NotificationCard::preview(current_settings.card_size.clone()));

            app.manage(settings_state);
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

            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            load_settings,
            save_settings,
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
