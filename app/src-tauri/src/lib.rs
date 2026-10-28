use tauri::{Manager, WebviewUrl, WebviewWindowBuilder};

#[tauri::command]
fn open_test_panel(app: tauri::AppHandle) -> Result<(), String> {
    if let Some(panel) = app.get_webview_window("notification-panel") {
        panel.show().map_err(|error| error.to_string())?;
        panel.set_focus().map_err(|error| error.to_string())?;
        return Ok(());
    }

    WebviewWindowBuilder::new(
        &app,
        "notification-panel",
        WebviewUrl::App("index.html?panel=1".into()),
    )
    .title("刻限通知 - ALTER GENESIS")
    .inner_size(420.0, 286.0)
    .min_inner_size(380.0, 260.0)
    .resizable(false)
    .decorations(false)
    .always_on_top(true)
    .skip_taskbar(true)
    .center()
    .build()
    .map(|_| ())
    .map_err(|error| error.to_string())
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .invoke_handler(tauri::generate_handler![open_test_panel])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
