/// All #[tauri::command] functions exposed to the Svelte frontend via Tauri IPC.
///
/// Commands are grouped by domain: Timer, Settings, Themes, Stats.
/// Each command returns `Result<T, String>` so errors surface cleanly in JS.
use log::LevelFilter;
use tauri::{AppHandle, Emitter, Manager, State};

use std::sync::Arc;

use crate::audio::{self, AudioManager};
use crate::notifications;
use crate::db::{queries, DbState};
use crate::settings::{self, Settings};
use crate::shortcuts;
use crate::themes::{self, Theme};
use crate::timer::{TimerController, TimerSnapshot};
use crate::tray::{self, TrayState};
use crate::websocket::{self, WsState};

// ---------------------------------------------------------------------------
// CMD-01 — Timer commands
// ---------------------------------------------------------------------------

/// Toggle the timer: start if idle, resume if paused, pause if running.
/// This is the primary action bound to the space bar and the play/pause button.
#[tauri::command]
pub fn timer_toggle(timer: State<'_, TimerController>) {
    timer.toggle();
}

/// Reset the timer. Ignored while running — must pause first.
#[tauri::command]
pub fn timer_reset(timer: State<'_, TimerController>) {
    let snap = timer.get_snapshot();
    if snap.is_running {
        log::debug!("[cmd] timer_reset ignored: timer is running");
        return;
    }
    timer.reset();
}

/// Skip the current round: fires Complete immediately and advances to the next.
#[tauri::command]
pub fn timer_skip(timer: State<'_, TimerController>) {
    timer.skip();
}

/// Restart the current round from zero without advancing the sequence.
/// Round type and round number are preserved; only elapsed time is reset.
#[tauri::command]
pub fn timer_restart_round(timer: State<'_, TimerController>) {
    timer.restart_round();
}

/// Return a full snapshot of the current timer state.
/// Called once on frontend mount to hydrate stores.
#[tauri::command]
pub fn timer_get_state(timer: State<'_, TimerController>) -> TimerSnapshot {
    timer.get_snapshot()
}

// ---------------------------------------------------------------------------
// CMD-02 — Settings commands
// ---------------------------------------------------------------------------

/// Return all current settings.
#[tauri::command]
pub fn settings_get(db: State<'_, DbState>) -> Result<Settings, String> {
    let conn = db.lock().map_err(|e| e.to_string())?;
    settings::load(&conn).map_err(|e| {
        log::error!("[settings] failed to load settings: {e}");
        e.to_string()
    })
}

/// Persist a single setting and emit `settings:changed` with the updated set.
///
/// `key` must be one of the DB column names (see `settings::defaults::DEFAULTS`).
/// `value` is always a string; the loader converts it to the appropriate type.
#[tauri::command]
pub fn settings_set(
    key: String,
    value: String,
    db: State<'_, DbState>,
    timer: State<'_, TimerController>,
    tray_state: State<'_, Arc<TrayState>>,
    ws_state: State<'_, Arc<WsState>>,
    app: AppHandle,
) -> Result<Settings, String> {
    log::debug!("[settings] set {key}={value}");
    let new_settings = {
        let conn = db.lock().map_err(|e| e.to_string())?;
        settings::save_setting(&conn, &key, &value).map_err(|e| {
            log::error!("[settings] failed to save '{key}': {e}");
            e.to_string()
        })?;
        // When SIT is turned off, cascade-reset the dependent tray settings so
        // the close-to-tray handler cannot hide the window with no icon to
        // restore from.
        if key == "tray_icon_enabled" && value == "false" {
            settings::save_setting(&conn, "min_to_tray", "false").map_err(|e| e.to_string())?;
            settings::save_setting(&conn, "min_to_tray_on_close", "false").map_err(|e| e.to_string())?;
        }
        settings::load(&conn).map_err(|e| {
            log::error!("[settings] failed to reload after save: {e}");
            e.to_string()
        })?
    };

    // Apply verbose_logging change immediately without a restart.
    if key == "verbose_logging" {
        if new_settings.verbose_logging {
            log::set_max_level(LevelFilter::Debug);
            log::info!("Verbose logging enabled — log level set to DEBUG");
        } else {
            log::set_max_level(LevelFilter::Info);
            log::info!("Verbose logging disabled — log level set to INFO");
        }
    }

    // Keep the timer engine in sync when time-related settings change.
    timer.apply_settings(new_settings.clone());

    // Broadcast an updated snapshot so the frontend immediately reflects any
    // changed settings (round count, durations, etc.) regardless of timer
    // state.  The timer:reset handler only calls timerState.set(), so emitting
    // while running does not interrupt the countdown; the next timer:tick
    // event will reconcile total_secs from the engine within one second.
    app.emit("timer:reset", &timer.get_snapshot()).ok();

    // Propagate volume and tick-sound changes to the audio engine (optional state).
    if let Some(audio) = app.try_state::<Arc<AudioManager>>() {
        audio.apply_settings(&new_settings);
    }

    // Apply always-on-top window flag immediately when the setting changes,
    // accounting for the current round type so break_always_on_top takes
    // effect without waiting for the next round transition.
    if matches!(key.as_str(), "always_on_top" | "break_always_on_top") {
        if let Some(window) = app.get_webview_window("main") {
            let snap = timer.get_snapshot();
            let is_break = snap.round_type != "work";
            let effective_aot = new_settings.always_on_top
                && !(new_settings.break_always_on_top && is_break);
            let _ = window.set_always_on_top(effective_aot);
        }
    }

    // Sync tray countdown mode when the dial setting changes, then immediately
    // re-render the icon so it matches the dial without waiting for a timer event.
    if key == "dial_countdown" {
        *tray_state.countdown_mode.lock().unwrap() = new_settings.dial_countdown;
        let snap = timer.get_snapshot();
        let progress = if snap.total_secs > 0 {
            snap.elapsed_secs as f32 / snap.total_secs as f32
        } else {
            0.0
        };
        tray::update_icon(&tray_state, &snap.round_type, snap.is_paused, progress);
    }

    // Update tray icon colors when the active theme changes.
    if matches!(key.as_str(), "theme_mode" | "theme_light" | "theme_dark") {
        let data_dir = app.path().app_data_dir().map_err(|e| e.to_string())?;
        let tray_theme_name = match new_settings.theme_mode.as_str() {
            "dark" => &new_settings.theme_dark,
            _ => &new_settings.theme_light,
        };
        if let Some(theme) = themes::find(&data_dir, tray_theme_name) {
            *tray_state.colors.lock().unwrap() = tray::TrayColors::from_colors_map(&theme.colors);
            let snap = timer.get_snapshot();
            let progress = if snap.total_secs > 0 {
                snap.elapsed_secs as f32 / snap.total_secs as f32
            } else {
                0.0
            };
            tray::update_icon(&tray_state, &snap.round_type, snap.is_paused, progress);

            // Broadcast theme to plugin via WebSocket
            let payload = serde_json::json!({ "name": theme.name, "colors": theme.colors });
            let _ = ws_state.broadcast_tx.send(websocket::WsEvent::ThemeChanged { payload });
        }
    }

    // Create or destroy the tray when tray_icon_enabled or min_to_tray changes.
    // The tray exists when either flag is true.
    // On Linux, spawn tray creation on a background thread to avoid blocking
    // the main thread on KDE Plasma 6 / Wayland (D-Bus StatusNotifier hang).
    if matches!(key.as_str(), "tray_icon_enabled" | "min_to_tray") {
        if new_settings.tray_icon_enabled || new_settings.min_to_tray {
            #[cfg(target_os = "linux")]
            {
                let app_handle = app.clone();
                let ts = Arc::clone(&tray_state);
                std::thread::spawn(move || {
                    tray::create_tray(&app_handle, &ts);
                });
            }
            #[cfg(not(target_os = "linux"))]
            tray::create_tray(&app, &tray_state);
        } else {
            tray::destroy_tray(&tray_state);
        }
    }

    // Re-register global shortcuts when any shortcut key changes or the enabled flag toggles.
    if matches!(key.as_str(), "shortcut_toggle" | "shortcut_reset" | "shortcut_skip" | "shortcut_restart" | "global_shortcuts_enabled") {
        shortcuts::register_all(&app, &new_settings);
    }

    // Start or stop the WebSocket server when the enabled flag or port changes.
    if matches!(key.as_str(), "websocket_enabled" | "websocket_port") {
        let ws = Arc::clone(&*ws_state);
        let port = new_settings.websocket_port;
        let enabled = new_settings.websocket_enabled;
        let app_clone = app.clone();
        tauri::async_runtime::spawn(async move {
            // Always stop the old server first.
            websocket::stop(&ws).await;
            if enabled {
                websocket::start(port, app_clone, &ws).await;
            }
        });
    }

    app.emit("settings:changed", &new_settings).ok();
    Ok(new_settings)
}

// ---------------------------------------------------------------------------
// CMD-06 — Shortcuts command
// ---------------------------------------------------------------------------

/// Re-register all global shortcuts from the current settings.
/// The frontend can call this after bulk-updating shortcut settings.
#[tauri::command]
pub fn shortcuts_reload(db: State<'_, DbState>, app: AppHandle) -> Result<(), String> {
    let conn = db.lock().map_err(|e| e.to_string())?;
    let s = settings::load(&conn).map_err(|e| e.to_string())?;
    shortcuts::register_all(&app, &s);
    Ok(())
}

/// Reset all settings to factory defaults and return the resulting settings.
#[tauri::command]
pub fn settings_reset_defaults(
    db: State<'_, DbState>,
    timer: State<'_, TimerController>,
    tray_state: State<'_, Arc<TrayState>>,
    app: AppHandle,
) -> Result<Settings, String> {
    log::info!("[settings] reset to defaults");
    let new_settings = {
        let conn = db.lock().map_err(|e| e.to_string())?;
        // Delete all rows so seed_defaults can insert fresh defaults.
        conn.execute("DELETE FROM settings", [])
            .map_err(|e| e.to_string())?;
        settings::seed_defaults(&conn).map_err(|e| e.to_string())?;
        settings::load(&conn).map_err(|e| e.to_string())?
    };

    timer.apply_settings(new_settings.clone());
    *tray_state.countdown_mode.lock().unwrap() = new_settings.dial_countdown;

    // Broadcast a reset snapshot so the frontend dial and display reflect the
    // restored default durations without requiring the user to manually reset.
    {
        let snap = timer.get_snapshot();
        if !snap.is_running && !snap.is_paused {
            app.emit("timer:reset", &snap).ok();
        }
    }

    // After reset, defaults have tray_icon_enabled=false and min_to_tray=false,
    // so destroy any active tray icon.
    tray::destroy_tray(&tray_state);

    let data_dir = app.path().app_data_dir().map_err(|e| e.to_string())?;

    // Clear custom alert sounds: delete files from disk and reset in-memory paths.
    if let Some(audio_state) = app.try_state::<Arc<AudioManager>>() {
        let audio_dir = data_dir.join("audio");
        for stem in [audio::STEM_PHASE, audio::STEM_TASK] {
            if let Ok(entries) = std::fs::read_dir(&audio_dir) {
                for entry in entries.filter_map(|e| e.ok()) {
                    let p = entry.path();
                    if p.file_stem().and_then(|s| s.to_str()) == Some(stem) {
                        let _ = std::fs::remove_file(&p);
                    }
                }
            }
        }
        audio_state.clear_custom_path("phase_complete");
        audio_state.clear_custom_path("task_done");
        log::info!("[audio] custom sounds cleared on settings reset");
    }

    let tray_theme_name = match new_settings.theme_mode.as_str() {
        "dark" => &new_settings.theme_dark,
        _ => &new_settings.theme_light,
    };
    if let Some(theme) = themes::find(&data_dir, tray_theme_name) {
        *tray_state.colors.lock().unwrap() = tray::TrayColors::from_colors_map(&theme.colors);
    }
    shortcuts::register_all(&app, &new_settings);
    app.emit("settings:changed", &new_settings).ok();
    Ok(new_settings)
}

// ---------------------------------------------------------------------------
// CMD-03 — Theme commands
// ---------------------------------------------------------------------------

/// List all available themes (17 bundled + any user-created ones).
#[tauri::command]
pub fn themes_list(app: AppHandle) -> Result<Vec<Theme>, String> {
    let data_dir = app
        .path()
        .app_data_dir()
        .map_err(|e| e.to_string())?;
    Ok(themes::list_all(&data_dir))
}

// ---------------------------------------------------------------------------
// CMD-04 — Sessions commands
// ---------------------------------------------------------------------------

/// Deletes all rows from the `sessions` table (irreversible bulk clear).
/// Emits `sessions:cleared` so any open stats window can refresh immediately.
#[tauri::command]
pub fn sessions_clear(db: State<'_, DbState>, app: AppHandle) -> Result<(), String> {
    log::info!("[sessions] clearing all session history");
    let conn = db.lock().map_err(|e| e.to_string())?;
    let n = conn.execute("DELETE FROM sessions", []).map_err(|e| {
        log::error!("[sessions] failed to clear history: {e}");
        e.to_string()
    })?;
    log::info!("[sessions] cleared {n} rows");
    app.emit("sessions:cleared", ()).ok();
    Ok(())
}

// CMD-05 — Stats commands
// ---------------------------------------------------------------------------

/// Batched stats for Today + This Week tabs (minimises IPC round-trips).
#[tauri::command]
pub fn stats_get_detailed(db: State<'_, DbState>) -> Result<DetailedStats, String> {
    let conn = db.lock().map_err(|e| e.to_string())?;
    let today = queries::get_daily_stats(&conn).map_err(|e| {
        log::error!("[stats] failed to query daily stats: {e}");
        e.to_string()
    })?;
    let week = queries::get_weekly_stats(&conn).map_err(|e| {
        log::error!("[stats] failed to query weekly stats: {e}");
        e.to_string()
    })?;
    let streak = queries::get_streak(&conn).map_err(|e| {
        log::error!("[stats] failed to query streak: {e}");
        e.to_string()
    })?;
    Ok(DetailedStats { today, week, streak })
}

/// Heatmap data + lifetime totals for the All Time tab.
#[tauri::command]
pub fn stats_get_heatmap(db: State<'_, DbState>) -> Result<HeatmapStats, String> {
    let conn = db.lock().map_err(|e| e.to_string())?;
    let entries = queries::get_heatmap_data(&conn).map_err(|e| {
        log::error!("[stats] failed to query heatmap data: {e}");
        e.to_string()
    })?;
    let raw = queries::get_all_time_stats(&conn).map_err(|e| {
        log::error!("[stats] failed to query all-time stats: {e}");
        e.to_string()
    })?;
    let streak = queries::get_streak(&conn).map_err(|e| {
        log::error!("[stats] failed to query streak for heatmap: {e}");
        e.to_string()
    })?;
    Ok(HeatmapStats {
        entries,
        total_rounds: raw.completed_work_sessions as u32,
        total_hours: (raw.total_work_secs / 3600) as u32,
        longest_streak: streak.longest,
    })
}

// ---------------------------------------------------------------------------
// CMD-05 — Window commands
// ---------------------------------------------------------------------------

/// Show or hide the main window.
#[tauri::command]
pub fn window_set_visibility(visible: bool, app: AppHandle) -> Result<(), String> {
    log::debug!("[window] set visibility={visible}");
    let window = app
        .get_webview_window("main")
        .ok_or_else(|| "main window not found".to_string())?;
    if visible {
        window.show().map_err(|e| e.to_string())?;
        window.set_focus().map_err(|e| e.to_string())?;
    } else {
        window.hide().map_err(|e| e.to_string())?;
    }
    Ok(())
}

// ---------------------------------------------------------------------------
// CMD-07 — Audio commands
// ---------------------------------------------------------------------------

/// Copy a user-selected audio file into the app config dir for the given cue slot.
///
/// `cue` must be one of: `"phase_complete"`, `"task_done"`.
/// `src_path` is the full path to the file chosen by the user.
///
/// The file is stored with a fixed stem (e.g. `custom_phase_complete.wav`) so that
/// selecting a new file for the same slot automatically replaces the old one —
/// no orphan files accumulate.
///
/// Returns the original filename for display in the UI.
#[tauri::command]
pub fn audio_set_custom(
    cue: String,
    src_path: String,
    db: State<'_, DbState>,
    app: AppHandle,
) -> Result<String, String> {
    let audio_state = app
        .try_state::<Arc<AudioManager>>()
        .ok_or_else(|| "audio engine is not available".to_string())?;

    let stem = cue_to_stem(&cue)?;

    let audio_dir = app
        .path()
        .app_data_dir()
        .map_err(|e| e.to_string())?
        .join("audio");
    std::fs::create_dir_all(&audio_dir).map_err(|e| e.to_string())?;

    let src = std::path::Path::new(&src_path);
    let ext = src
        .extension()
        .and_then(|e| e.to_str())
        .unwrap_or("mp3");

    // Remove any existing custom file for this slot (preserves zero orphans).
    if let Ok(entries) = std::fs::read_dir(&audio_dir) {
        for entry in entries.filter_map(|e| e.ok()) {
            let p = entry.path();
            if p.file_stem().and_then(|s| s.to_str()) == Some(stem) {
                let _ = std::fs::remove_file(&p);
            }
        }
    }

    let dest = audio_dir.join(format!("{stem}.{ext}"));
    std::fs::copy(src, &dest).map_err(|e| e.to_string())?;

    audio_state.set_custom_path(&cue, dest);

    let display_name = src
        .file_name()
        .and_then(|n| n.to_str())
        .unwrap_or("custom")
        .to_string();

    // Persist the original filename so it survives restarts.
    let name_key = cue_to_name_key(&cue)?;
    let conn = db.lock().map_err(|e| e.to_string())?;
    settings::save_setting(&conn, name_key, &display_name).map_err(|e| e.to_string())?;

    log::info!("[audio] custom sound set cue={cue} file={display_name}");
    Ok(display_name)
}

/// Restore the built-in sound for the given cue slot by deleting the custom file.
#[tauri::command]
pub fn audio_clear_custom(
    cue: String,
    db: State<'_, DbState>,
    app: AppHandle,
) -> Result<(), String> {
    let audio_state = app
        .try_state::<Arc<AudioManager>>()
        .ok_or_else(|| "audio engine is not available".to_string())?;

    let stem = cue_to_stem(&cue)?;

    let audio_dir = app
        .path()
        .app_data_dir()
        .map_err(|e| e.to_string())?
        .join("audio");

    if let Ok(entries) = std::fs::read_dir(&audio_dir) {
        for entry in entries.filter_map(|e| e.ok()) {
            let p = entry.path();
            if p.file_stem().and_then(|s| s.to_str()) == Some(stem) {
                std::fs::remove_file(&p).map_err(|e| e.to_string())?;
            }
        }
    }

    audio_state.clear_custom_path(&cue);

    // Remove the persisted display name.
    let name_key = cue_to_name_key(&cue)?;
    let conn = db.lock().map_err(|e| e.to_string())?;
    conn.execute("DELETE FROM settings WHERE key = ?1", rusqlite::params![name_key])
        .map_err(|e| e.to_string())?;

    log::info!("[audio] custom sound cleared cue={cue}");
    Ok(())
}

/// Return the display names of any currently configured custom audio files.
/// Fields are `null` when the built-in sound is in use for that slot.
#[tauri::command]
pub fn audio_get_custom_info(
    db: State<'_, DbState>,
    app: AppHandle,
) -> Result<audio::CustomAudioInfo, String> {
    let audio_state = app
        .try_state::<Arc<AudioManager>>()
        .ok_or_else(|| "audio engine is not available".to_string())?;

    // Start from the AudioManager's paths (determines which slots are active).
    let mut info = audio_state.get_custom_info();

    // Override each active slot's name with the persisted original filename.
    let conn = db.lock().map_err(|e| e.to_string())?;
    let override_name = |stored: &Option<String>, key: &str| -> Option<String> {
        stored.as_ref()?; // slot not active — leave as None
        settings::get_setting(&conn, key).or_else(|| stored.clone())
    };
    info.phase_complete = override_name(&info.phase_complete, "custom_phase_complete_name");
    info.task_done = override_name(&info.task_done, "custom_task_done_name");

    Ok(info)
}

// ---------------------------------------------------------------------------
// CMD-08 — Notification command
// ---------------------------------------------------------------------------

/// Show a desktop notification with the given title and body.
///
/// String construction (including translation) is the caller's (frontend's)
/// responsibility. This command is a thin platform-dispatch wrapper.
#[tauri::command]
pub fn notification_show(title: String, body: String, app: AppHandle) {
    notifications::show(&app, &title, &body);
}

// ---------------------------------------------------------------------------
// CMD-09 — Diagnostic log commands
// ---------------------------------------------------------------------------

/// Open the application log directory in the OS file manager.
#[tauri::command]
pub fn open_log_dir(app: AppHandle) {
    match app.path().app_log_dir() {
        Ok(log_dir) => {
            if let Err(e) = tauri_plugin_opener::open_path(&log_dir, None::<&str>) {
                log::warn!("[log] failed to open log dir {}: {e}", log_dir.display());
            }
        }
        Err(e) => log::warn!("[log] failed to resolve log dir: {e}"),
    }
}

/// Return the compile-time build version string.
#[tauri::command]
pub fn app_version() -> &'static str {
    env!("APP_BUILD_VERSION")
}

// ---------------------------------------------------------------------------
// CMD-10 — Platform commands
// ---------------------------------------------------------------------------

/// Returns whether the app has macOS Accessibility permission.
/// On macOS, calls AXIsProcessTrusted() from the ApplicationServices framework.
/// On all other platforms, always returns true.
#[tauri::command]
pub fn accessibility_trusted() -> bool {
    #[cfg(target_os = "macos")]
    {
        #[link(name = "ApplicationServices", kind = "framework")]
        extern "C" {
            fn AXIsProcessTrusted() -> bool;
        }
        unsafe { AXIsProcessTrusted() }
    }
    #[cfg(not(target_os = "macos"))]
    {
        true
    }
}

/// Returns whether the system tray is supported on this platform/install.
/// On Linux, probes for libayatana-appindicator3 / libappindicator3 at runtime.
/// On macOS and Windows, always returns true.
#[tauri::command]
pub fn tray_supported() -> bool {
    #[cfg(target_os = "linux")]
    {
        tray::appindicator_available()
    }
    #[cfg(not(target_os = "linux"))]
    {
        true
    }
}

/// Return the application log directory path as a string.
#[tauri::command]
pub fn get_log_dir(app: AppHandle) -> Result<String, String> {
    app.path()
        .app_log_dir()
        .map(|p| p.to_string_lossy().into_owned())
        .map_err(|e| {
            log::warn!("[log] failed to resolve log dir: {e}");
            e.to_string()
        })
}

// ---------------------------------------------------------------------------
// CMD-11 — Updater commands
// ---------------------------------------------------------------------------

/// Information about an available update returned to the frontend.
#[derive(serde::Serialize)]
pub struct UpdateInfo {
    pub version: String,
    pub body: Option<String>,
    pub date: Option<String>,
}

/// Check whether a newer version is available.
/// Returns `Some(UpdateInfo)` when an update is available, or `None` when
/// the running version is already the latest.
/// Errors (e.g. network failure) are surfaced as a string so the frontend
/// can display a non-blocking message.
#[tauri::command]
pub async fn check_update(app: AppHandle) -> Result<Option<UpdateInfo>, String> {
    use tauri_plugin_updater::UpdaterExt;
    log::info!("[updater] checking for updates");
    let updater = app.updater().map_err(|e| {
        log::error!("[updater] failed to build updater: {e}");
        e.to_string()
    })?;
    match updater.check().await {
        Ok(Some(update)) => {
            log::info!("[updater] update available: v{}", update.version);
            Ok(Some(UpdateInfo {
                version: update.version.clone(),
                body: update.body.clone(),
                date: update.date.map(|d| d.to_string()),
            }))
        }
        Ok(None) => {
            log::info!("[updater] already up to date");
            Ok(None)
        }
        Err(e) => {
            log::warn!("[updater] update check failed: {e}");
            Err(e.to_string())
        }
    }
}

/// Download, verify, and install the pending update, then relaunch immediately.
/// Should only be called after `check_update` has returned `Some(UpdateInfo)`.
#[tauri::command]
pub async fn install_update(app: AppHandle) -> Result<(), String> {
    use tauri_plugin_updater::UpdaterExt;
    log::info!("[updater] install requested — checking for update");
    let updater = app.updater().map_err(|e| {
        log::error!("[updater] failed to build updater: {e}");
        e.to_string()
    })?;
    let update = updater
        .check()
        .await
        .map_err(|e| {
            log::error!("[updater] update check failed during install: {e}");
            e.to_string()
        })?
        .ok_or_else(|| {
            log::warn!("[updater] install_update called but no update is available");
            "No update available".to_string()
        })?;
    log::info!("[updater] downloading and installing v{}", update.version);
    update
        .download_and_install(|_, _| {}, || {})
        .await
        .map_err(|e| {
            log::error!("[updater] download/install failed: {e}");
            e.to_string()
        })?;
    log::info!("[updater] install complete — relaunching");
    app.restart();
}

fn cue_to_stem(cue: &str) -> Result<&'static str, String> {
    match cue {
        "phase_complete" => Ok(audio::STEM_PHASE),
        "task_done" => Ok(audio::STEM_TASK),
        _ => Err(format!("unknown audio cue: '{cue}'")),
    }
}

fn cue_to_name_key(cue: &str) -> Result<&'static str, String> {
    match cue {
        "phase_complete" => Ok("custom_phase_complete_name"),
        "task_done" => Ok("custom_task_done_name"),
        _ => Err(format!("unknown audio cue: '{cue}'")),
    }
}

// ---------------------------------------------------------------------------
// Stats payload types
// ---------------------------------------------------------------------------

/// Batched payload for Today + This Week tabs.
#[derive(serde::Serialize)]
pub struct DetailedStats {
    pub today: queries::DailyStats,
    pub week: Vec<queries::DayStat>,
    pub streak: queries::StreakInfo,
}

/// Payload for the All Time tab.
#[derive(serde::Serialize)]
pub struct HeatmapStats {
    pub entries: Vec<queries::HeatmapEntry>,
    pub total_rounds: u32,
    pub total_hours: u32,
    pub longest_streak: u32,
}

#[cfg(test)]
mod tests {
    use rusqlite::Connection;
    use crate::db::migrations;

    fn setup() -> Connection {
        let conn = Connection::open_in_memory().unwrap();
        migrations::run(&conn).unwrap();
        conn
    }

    fn seed_sessions(conn: &Connection) {
        conn.execute_batch("
            INSERT INTO sessions (started_at, ended_at, round_type, duration_secs, completed)
            VALUES (1000, 1060, 'work', 60, 1),
                   (2000, 2300, 'short-break', 300, 1);
        ").unwrap();
    }

    #[test]
    fn sessions_clear_removes_all_rows() {
        let conn = setup();
        seed_sessions(&conn);
        let before: i64 = conn.query_row("SELECT COUNT(*) FROM sessions", [], |r| r.get(0)).unwrap();
        assert_eq!(before, 2);

        let n = conn.execute("DELETE FROM sessions", []).unwrap();
        assert_eq!(n, 2);

        let after: i64 = conn.query_row("SELECT COUNT(*) FROM sessions", [], |r| r.get(0)).unwrap();
        assert_eq!(after, 0);
    }

    #[test]
    fn sessions_clear_on_empty_table_returns_zero() {
        let conn = setup();
        let n = conn.execute("DELETE FROM sessions", []).unwrap();
        assert_eq!(n, 0);
    }
}


// ---------------------------------------------------------------------------
// CMD-12 — Task commands
// ---------------------------------------------------------------------------

#[tauri::command]
pub fn task_list(db: State<'_, DbState>) -> Result<Vec<queries::Task>, String> {
    let conn = db.lock().map_err(|e| e.to_string())?;
    queries::task_list(&conn).map_err(|e| e.to_string())
}

#[tauri::command]
pub fn task_create(
    title: String, estimated_pomos: i64,
    db: State<'_, DbState>, app: AppHandle,
) -> Result<queries::Task, String> {
    let task = {
        let conn = db.lock().map_err(|e| e.to_string())?;
        queries::task_create(&conn, &title, estimated_pomos).map_err(|e| e.to_string())?
    };
    let _ = app.emit("tasks:changed", ());
    // Broadcast to plugin
    if let Some(ws) = app.try_state::<Arc<WsState>>() {
        if let Ok(conn) = db.lock() {
            if let Ok(tasks) = queries::task_list(&conn) {
                let payload = serde_json::to_value(&tasks).unwrap_or_default();
                let _ = ws.broadcast_tx.send(websocket::WsEvent::TasksChanged { payload });
            }
        }
    }
    Ok(task)
}

#[tauri::command]
pub fn task_set_active(id: i64, db: State<'_, DbState>, app: AppHandle) -> Result<(), String> {
    {
        let conn = db.lock().map_err(|e| e.to_string())?;
        queries::task_set_active(&conn, id).map_err(|e| e.to_string())?;
    }
    let _ = app.emit("tasks:changed", ());
    if let Some(ws) = app.try_state::<Arc<WsState>>() {
        if let Ok(conn) = db.lock() {
            if let Ok(tasks) = queries::task_list(&conn) {
                let payload = serde_json::to_value(&tasks).unwrap_or_default();
                let _ = ws.broadcast_tx.send(websocket::WsEvent::TasksChanged { payload });
            }
        }
    }
    Ok(())
}

#[tauri::command]
pub fn task_complete(id: i64, db: State<'_, DbState>, app: AppHandle) -> Result<(), String> {
    {
        let conn = db.lock().map_err(|e| e.to_string())?;
        queries::complete_task(&conn, id).map_err(|e| e.to_string())?;
    }
    // Play the task-done sound (same as plugin standalone)
    if let Some(audio) = app.try_state::<Arc<AudioManager>>() {
        audio.play_cue(audio::AudioCue::TaskDone);
    }
    let _ = app.emit("tasks:changed", ());
    if let Some(ws) = app.try_state::<Arc<WsState>>() {
        if let Ok(conn) = db.lock() {
            if let Ok(tasks) = queries::task_list(&conn) {
                let payload = serde_json::to_value(&tasks).unwrap_or_default();
                let _ = ws.broadcast_tx.send(websocket::WsEvent::TasksChanged { payload });
            }
        }
    }
    Ok(())
}

#[tauri::command]
pub fn task_delete(id: i64, db: State<'_, DbState>, app: AppHandle) -> Result<(), String> {
    {
        let conn = db.lock().map_err(|e| e.to_string())?;
        queries::task_delete(&conn, id).map_err(|e| e.to_string())?;
    }
    let _ = app.emit("tasks:changed", ());
    if let Some(ws) = app.try_state::<Arc<WsState>>() {
        if let Ok(conn) = db.lock() {
            if let Ok(tasks) = queries::task_list(&conn) {
                let payload = serde_json::to_value(&tasks).unwrap_or_default();
                let _ = ws.broadcast_tx.send(websocket::WsEvent::TasksChanged { payload });
            }
        }
    }
    Ok(())
}

#[tauri::command]
pub fn task_update_estimate(id: i64, estimated_pomos: i64, db: State<'_, DbState>) -> Result<(), String> {
    let conn = db.lock().map_err(|e| e.to_string())?;
    queries::task_update_estimate(&conn, id, estimated_pomos).map_err(|e| e.to_string())
}

#[tauri::command]
pub fn task_list_completed(db: State<'_, DbState>) -> Result<Vec<queries::Task>, String> {
    let conn = db.lock().map_err(|e| e.to_string())?;
    queries::task_list_completed(&conn).map_err(|e| e.to_string())
}

#[tauri::command]
pub fn task_reopen(id: i64, db: State<'_, DbState>, app: AppHandle) -> Result<(), String> {
    {
        let conn = db.lock().map_err(|e| e.to_string())?;
        queries::task_reopen(&conn, id).map_err(|e| e.to_string())?;
    }
    let _ = app.emit("tasks:changed", ());
    Ok(())
}

// ---------------------------------------------------------------------------
// CMD-13 — Blocker commands
// ---------------------------------------------------------------------------

#[tauri::command]
pub fn blocker_get_sites(db: State<'_, DbState>) -> Result<Vec<String>, String> {
    let conn = db.lock().map_err(|e| e.to_string())?;
    queries::get_blocked_sites(&conn).map_err(|e| e.to_string())
}

#[tauri::command]
pub fn blocker_set_sites(sites: Vec<String>, db: State<'_, DbState>, app: AppHandle) -> Result<(), String> {
    let conn = db.lock().map_err(|e| e.to_string())?;
    let value = sites.join(",");
    crate::settings::save_setting(&conn, "blocked_sites", &value).map_err(|e| e.to_string())?;

    // Hot-reload: update sites and force browser to re-fetch PAC
    if let Some(wb) = app.try_state::<Arc<crate::blocker::web::WebBlockerState>>() {
        let wb2 = Arc::clone(&wb);
        let new_sites = sites.clone();
        tauri::async_runtime::spawn(async move {
            *wb2.sites.lock().await = new_sites;
            // Toggle proxy off→on to bust browser PAC cache
            if *wb2.active.lock().await {
                crate::blocker::web::emergency_cleanup();
                tokio::time::sleep(std::time::Duration::from_millis(100)).await;
                crate::blocker::web::set_and_notify();
            }
        });
    }
    Ok(())
}

#[tauri::command]
pub fn blocker_get_apps(db: State<'_, DbState>) -> Result<Vec<String>, String> {
    let conn = db.lock().map_err(|e| e.to_string())?;
    let raw: String = conn.query_row(
        "SELECT COALESCE(value, '') FROM settings WHERE key = 'blocked_apps'",
        [], |r| r.get(0),
    ).unwrap_or_default();
    Ok(raw.split('|').map(|s| s.trim().to_string()).filter(|s| !s.is_empty()).collect())
}

#[tauri::command]
pub fn blocker_set_apps(apps: Vec<String>, db: State<'_, DbState>, app: AppHandle) -> Result<(), String> {
    let conn = db.lock().map_err(|e| e.to_string())?;
    let value = apps.join("|");
    crate::settings::save_setting(&conn, "blocked_apps", &value).map_err(|e| e.to_string())?;

    // Hot-reload: update the running app blocker's list immediately
    if let Some(blocker) = app.try_state::<Arc<crate::blocker::AppBlocker>>() {
        blocker.set_blocked_apps(&apps);
    }
    Ok(())
}

#[tauri::command]
pub fn blocker_get_jar(db: State<'_, DbState>) -> Result<Vec<queries::JarEntry>, String> {
    let conn = db.lock().map_err(|e| e.to_string())?;
    queries::jar_today(&conn).map_err(|e| e.to_string())
}

#[tauri::command]
pub fn jar_get_week(db: State<'_, DbState>) -> Result<Vec<queries::WeekJarEntry>, String> {
    let conn = db.lock().map_err(|e| e.to_string())?;
    queries::jar_week(&conn).map_err(|e| e.to_string())
}

#[tauri::command]
pub fn flow_get_state(app: AppHandle) -> Result<serde_json::Value, String> {
    if let Some(ft) = app.try_state::<Arc<crate::flow::FlowTracker>>() {
        let snap = ft.snapshot();
        serde_json::to_value(&snap).map_err(|e| e.to_string())
    } else {
        Ok(serde_json::json!(null))
    }
}

#[tauri::command]
pub fn flow_get_timeline(db: State<'_, DbState>) -> Result<Vec<queries::FlowTimelineEntry>, String> {
    let conn = db.lock().map_err(|e| e.to_string())?;
    queries::flow_timeline_today(&conn).map_err(|e| e.to_string())
}

// ---------------------------------------------------------------------------
// CMD-14 — Installed apps discovery
// ---------------------------------------------------------------------------

#[tauri::command]
pub fn get_installed_apps() -> Vec<InstalledApp> {
    #[cfg(target_os = "windows")]
    { list_installed_apps_windows() }
    #[cfg(not(target_os = "windows"))]
    { Vec::new() }
}

#[derive(serde::Serialize, Clone)]
pub struct InstalledApp {
    pub name: String,
    pub exe_name: String,
}

#[cfg(target_os = "windows")]
fn list_installed_apps_windows() -> Vec<InstalledApp> {
    use std::collections::HashSet;
    let mut apps = Vec::new();
    let mut seen = HashSet::new();

    let paths = [
        r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        r"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];
    let hives = [
        winreg::enums::HKEY_LOCAL_MACHINE,
        winreg::enums::HKEY_CURRENT_USER,
    ];

    for hive in &hives {
        for path in &paths {
            if let Ok(key) = winreg::RegKey::predef(*hive).open_subkey(path) {
                for name in key.enum_keys().flatten() {
                    if let Ok(subkey) = key.open_subkey(&name) {
                        let display: String = subkey.get_value("DisplayName").unwrap_or_default();
                        let icon: String = subkey.get_value("DisplayIcon").unwrap_or_default();
                        let install_loc: String = subkey.get_value("InstallLocation").unwrap_or_default();

                        if display.is_empty() { continue; }

                        // Extract exe name from DisplayIcon or InstallLocation
                        let exe = extract_exe_name(&icon)
                            .or_else(|| extract_exe_name(&install_loc))
                            .unwrap_or_default();

                        if exe.is_empty() || is_system_app(&display) { continue; }

                        let key = exe.to_lowercase();
                        if seen.contains(&key) { continue; }
                        seen.insert(key);

                        apps.push(InstalledApp { name: display, exe_name: exe });
                    }
                }
            }
        }
    }

    apps.sort_by(|a, b| a.name.to_lowercase().cmp(&b.name.to_lowercase()));
    apps
}

#[cfg(target_os = "windows")]
fn extract_exe_name(path: &str) -> Option<String> {
    let clean = path.trim_matches('"').split(',').next().unwrap_or("").trim();
    if clean.is_empty() { return None; }
    let p = std::path::Path::new(clean);
    let stem = p.file_stem()?.to_str()?.to_string();
    if stem.is_empty() { None } else { Some(stem) }
}

#[cfg(target_os = "windows")]
fn is_system_app(name: &str) -> bool {
    let lower = name.to_lowercase();
    lower.contains("microsoft visual c++") || lower.contains("update for") ||
    lower.contains(".net framework") || lower.contains("redistributable") ||
    lower.contains("windows sdk") || lower.contains("windows kit") ||
    lower.starts_with("kb") || lower.contains("hotfix")
}

#[tauri::command]
pub fn get_streak(db: State<'_, DbState>) -> Result<queries::StreakInfo, String> {
    let conn = db.lock().map_err(|e| e.to_string())?;
    queries::get_streak(&conn).map_err(|e| e.to_string())
}
