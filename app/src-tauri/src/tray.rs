use crate::{google::GoogleState, settings::Language, sound::SoundState};
use tauri::{
    AppHandle, Manager, WindowEvent,
    menu::{Menu, MenuItem},
    tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent},
};

const TRAY_ID: &str = "alter-genesis-tray";
const SETTINGS_ID: &str = "tray-settings";
const EXIT_ID: &str = "tray-exit";

struct TrayState {
    settings: MenuItem<tauri::Wry>,
    exit: MenuItem<tauri::Wry>,
}

fn system_locale() -> String {
    #[cfg(windows)]
    {
        use windows_sys::Win32::Globalization::GetUserDefaultLocaleName;
        let mut name = [0u16; 85]; // Windows SDK's LOCALE_NAME_MAX_LENGTH, including the terminator.
        // Windows writes at most the provided UTF-16 buffer length, including the terminator.
        let count = unsafe { GetUserDefaultLocaleName(name.as_mut_ptr(), name.len() as i32) };
        if count > 1 {
            return String::from_utf16_lossy(&name[..count as usize - 1]);
        }
    }
    "en".into()
}

fn labels(language: &Language) -> (&'static str, &'static str) {
    let japanese = match language {
        Language::Japanese => true,
        Language::English => false,
        Language::System => system_locale().to_ascii_lowercase().starts_with("ja"),
    };
    if japanese {
        ("設定", "終了")
    } else {
        ("Settings", "Exit")
    }
}

fn report(reason: &str) {
    if std::env::var("ALTER_GENESIS_GOOGLE_DIAGNOSTICS").as_deref() == Ok("1") {
        eprintln!("tray_runtime reason={reason}");
    }
}

fn show_settings(app: &AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        let result = window
            .unminimize()
            .and_then(|_| window.show())
            .and_then(|_| window.set_focus());
        match result {
            Ok(()) => report("settings_shown"),
            Err(_) => eprintln!("tray_settings_show_failed"),
        }
    }
}

pub fn language_changed(app: &AppHandle, language: &Language) {
    if let Some(state) = app.try_state::<TrayState>() {
        let (settings, exit) = labels(language);
        if state.settings.set_text(settings).is_err() || state.exit.set_text(exit).is_err() {
            eprintln!("tray_menu_update_failed");
        }
    }
}

pub fn start(app: &AppHandle, language: &Language) -> Result<(), Box<dyn std::error::Error>> {
    let (settings_text, exit_text) = labels(language);
    let settings = MenuItem::with_id(app, SETTINGS_ID, settings_text, true, None::<&str>)?;
    let exit = MenuItem::with_id(app, EXIT_ID, exit_text, true, None::<&str>)?;
    let menu = Menu::with_items(app, &[&settings, &exit])?;
    let icon = app
        .default_window_icon()
        .ok_or("tray_icon_missing")?
        .clone();
    let tooltip = if std::env::var_os("ALTER_GENESIS_SETTINGS_PATH").is_some() {
        "ALTER GENESIS（検証用）"
    } else {
        "ALTER GENESIS"
    };
    TrayIconBuilder::with_id(TRAY_ID)
        .icon(icon)
        .tooltip(tooltip)
        .menu(&menu)
        .show_menu_on_left_click(false)
        .on_menu_event(|app, event| match event.id.as_ref() {
            SETTINGS_ID => show_settings(app),
            EXIT_ID => {
                report("exit_requested");
                app.state::<GoogleState>().cancel();
                app.state::<SoundState>().pause();
                app.exit(0);
            }
            _ => (),
        })
        .on_tray_icon_event(|tray, event| {
            if matches!(
                event,
                TrayIconEvent::Click {
                    button: MouseButton::Left,
                    button_state: MouseButtonState::Up,
                    ..
                }
            ) {
                show_settings(tray.app_handle());
            }
        })
        .build(app)?;
    app.manage(TrayState { settings, exit });

    // Install close-to-hide only after a working tray provides a route back and an exit action.
    let main = app
        .get_webview_window("main")
        .ok_or("tray_settings_missing")?;
    let window = main.clone();
    main.on_window_event(move |event| {
        if let WindowEvent::CloseRequested { api, .. } = event {
            api.prevent_close();
            match window.hide() {
                Ok(()) => report("settings_hidden"),
                Err(_) => eprintln!("tray_settings_hide_failed"),
            }
        }
    });
    report("ready");
    Ok(())
}
