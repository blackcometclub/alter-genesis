mod settings;

use settings::{AppSettings, SettingsState};
use std::path::PathBuf;
use tauri::Manager;

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

fn settings_path(app: &tauri::App) -> Result<PathBuf, Box<dyn std::error::Error>> {
    if let Some(path) = std::env::var_os("ALTER_GENESIS_SETTINGS_PATH") {
        return Ok(PathBuf::from(path));
    }

    Ok(app.path().app_local_data_dir()?.join("settings.json"))
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .setup(|app| {
            app.manage(SettingsState::new(settings_path(app)?));
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![load_settings, save_settings])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
