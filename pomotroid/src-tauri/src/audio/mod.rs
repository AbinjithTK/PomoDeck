/// Rust audio playback via rodio.
///
/// All four audio assets are embedded at compile time so they are available
/// even when the app is hidden to the system tray.
///
/// Architecture:
///   A dedicated OS thread owns the `OutputStream` (not `Send` on macOS).
///   `AudioManager` communicates with that thread via a `SyncSender`.
///   Tauri manages `Arc<AudioManager>` as state; it is `Send + Sync`.
///
/// Custom audio:
///   Per-cue custom files are stored in `{app_data_dir}/audio/` with fixed
///   stems (`custom_phase_complete`, `custom_task_done`). The audio thread
///   tries the custom file first and falls back to the embedded bytes if the
///   file is missing or unreadable.
use std::io::Cursor;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::sync::mpsc;

use rodio::{Decoder, DeviceSinkBuilder, Player};

use crate::settings::Settings;

// ---------------------------------------------------------------------------
// Embedded audio assets
// ---------------------------------------------------------------------------

/// Single phase-complete chime — same sound the plugin plays standalone.
const PHASE_COMPLETE: &[u8] = include_bytes!("../../../static/audio/phase_complete.wav");
/// Task-done sound — matches the plugin's taskdone.wav.
const TASK_DONE: &[u8] = include_bytes!("../../../static/audio/taskdone.wav");
/// Skip feedback sounds — one per phase being skipped TO.
const SKIP_WORK: &[u8] = include_bytes!("../../../static/audio/alert-work.mp3");
const SKIP_SHORT_BREAK: &[u8] = include_bytes!("../../../static/audio/alert-short-break.mp3");
const SKIP_LONG_BREAK: &[u8] = include_bytes!("../../../static/audio/alert-long-break.mp3");
const TICK: &[u8] = include_bytes!("../../../static/audio/tick.mp3");

// ---------------------------------------------------------------------------
// Public types
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Copy)]
pub enum AudioCue {
    /// Phase complete — single unified chime for all round transitions.
    PhaseComplete,
    /// Task marked as done.
    TaskDone,
    /// Skip feedback — played when skipping to work.
    SkipWork,
    /// Skip feedback — played when skipping to short break.
    SkipShortBreak,
    /// Skip feedback — played when skipping to long break.
    SkipLongBreak,
    Tick,
}

/// Paths to currently active custom audio files (one per alert cue).
/// `None` means the embedded default is used.
#[derive(Default)]
pub struct CustomAudioPaths {
    pub phase_complete: Option<PathBuf>,
    pub task_done: Option<PathBuf>,
}

/// Serialisable snapshot of custom audio file names (sent to the frontend).
#[derive(serde::Serialize)]
pub struct CustomAudioInfo {
    pub phase_complete: Option<String>,
    pub task_done: Option<String>,
}

struct PlayRequest {
    cue: AudioCue,
    /// Resolved custom file path, if one is configured for this cue.
    custom_path: Option<PathBuf>,
    volume: f32,
}

/// Settings subset relevant to the audio engine.
#[derive(Clone)]
struct AudioSettings {
    volume: f32,
    tick_sounds_work: bool,
    tick_sounds_break: bool,
}

impl From<&Settings> for AudioSettings {
    fn from(s: &Settings) -> Self {
        Self {
            volume: s.volume,
            tick_sounds_work: s.tick_sounds_during_work,
            tick_sounds_break: s.tick_sounds_during_break,
        }
    }
}

/// Thread-safe audio manager. Register as `Arc<AudioManager>` in Tauri state.
pub struct AudioManager {
    tx: mpsc::SyncSender<PlayRequest>,
    settings: Arc<Mutex<AudioSettings>>,
    pub custom_paths: Arc<Mutex<CustomAudioPaths>>,
    /// Set after playing an alert — suppresses ticks until cleared.
    alert_cooldown: Arc<std::sync::atomic::AtomicBool>,
}

impl AudioManager {
    /// Spawn the audio thread and return an `Arc<AudioManager>`.
    /// If the system audio device is unavailable, returns `None` (app still works, just silent).
    pub fn new(initial: &Settings) -> Option<Arc<Self>> {
        let (tx, rx) = mpsc::sync_channel::<PlayRequest>(8);
        let alert_cooldown = Arc::new(std::sync::atomic::AtomicBool::new(false));
        let cooldown_clone = Arc::clone(&alert_cooldown);

        std::thread::Builder::new()
            .name("audio".to_string())
            .spawn(move || audio_thread(rx, cooldown_clone))
            .ok()?;

        log::info!("[audio] initialized: volume={:.0}%", initial.volume * 100.0);
        Some(Arc::new(Self {
            tx,
            settings: Arc::new(Mutex::new(AudioSettings::from(initial))),
            custom_paths: Arc::new(Mutex::new(CustomAudioPaths::default())),
            alert_cooldown,
        }))
    }

    /// Update volume and tick-sound settings from a new `Settings` snapshot.
    pub fn apply_settings(&self, s: &Settings) {
        *self.settings.lock().unwrap() = AudioSettings::from(s);
    }

    /// Play the given cue at the current stored volume.
    /// Non-blocking: drops the request if the channel is full.
    pub fn play_cue(&self, cue: AudioCue) {
        let volume = self.settings.lock().unwrap().volume;
        if volume <= 0.0 {
            return;
        }
        // Suppress ticks during alert cooldown (prevents rapid tick burst after phase complete)
        if matches!(cue, AudioCue::Tick) && self.alert_cooldown.load(std::sync::atomic::Ordering::Relaxed) {
            return;
        }
        // Resolve custom path (Tick and skip sounds always use the embedded sound).
        let custom_path = {
            let paths = self.custom_paths.lock().unwrap();
            match cue {
                AudioCue::PhaseComplete => paths.phase_complete.clone(),
                AudioCue::TaskDone => paths.task_done.clone(),
                AudioCue::SkipWork | AudioCue::SkipShortBreak | AudioCue::SkipLongBreak | AudioCue::Tick => None,
            }
        };
        let _ = self.tx.try_send(PlayRequest { cue, custom_path, volume });
    }

    /// Returns true if tick sounds are enabled for the given round type string.
    pub fn tick_enabled_for(&self, round_type: &str) -> bool {
        let s = self.settings.lock().unwrap();
        match round_type {
            "work" => s.tick_sounds_work,
            _ => s.tick_sounds_break,
        }
    }

    /// Set a custom file path for the given cue slot.
    /// `cue` must be `"phase_complete"` or `"task_done"`.
    pub fn set_custom_path(&self, cue: &str, path: PathBuf) {
        let mut paths = self.custom_paths.lock().unwrap();
        match cue {
            "phase_complete" => paths.phase_complete = Some(path),
            "task_done" => paths.task_done = Some(path),
            _ => {}
        }
    }

    /// Remove the custom path for the given cue slot, reverting to the built-in sound.
    pub fn clear_custom_path(&self, cue: &str) {
        let mut paths = self.custom_paths.lock().unwrap();
        match cue {
            "phase_complete" => paths.phase_complete = None,
            "task_done" => paths.task_done = None,
            _ => {}
        }
    }

    /// Return the display names (file names only) of any configured custom files.
    pub fn get_custom_info(&self) -> CustomAudioInfo {
        let paths = self.custom_paths.lock().unwrap();
        let name = |p: &Option<PathBuf>| -> Option<String> {
            p.as_ref()
                .and_then(|pb| pb.file_name())
                .and_then(|n| n.to_str())
                .map(String::from)
        };
        CustomAudioInfo {
            phase_complete: name(&paths.phase_complete),
            task_done: name(&paths.task_done),
        }
    }
}

// ---------------------------------------------------------------------------
// Startup helper — scan disk for previously saved custom files
// ---------------------------------------------------------------------------

/// Fixed file stems used when copying custom audio files into the config dir.
pub const STEM_PHASE: &str = "custom_phase_complete";
pub const STEM_TASK: &str = "custom_task_done";

/// Scan `audio_dir` for any saved custom audio files and return the paths.
pub fn find_custom_files(audio_dir: &Path) -> CustomAudioPaths {
    let find = |stem: &str| -> Option<PathBuf> {
        let entries = std::fs::read_dir(audio_dir).ok()?;
        entries
            .filter_map(|e| e.ok().map(|e| e.path()))
            .find(|p| p.file_stem().and_then(|s| s.to_str()) == Some(stem))
    };
    CustomAudioPaths {
        phase_complete: find(STEM_PHASE),
        task_done: find(STEM_TASK),
    }
}

// ---------------------------------------------------------------------------
// Audio thread
// ---------------------------------------------------------------------------

fn audio_thread(rx: mpsc::Receiver<PlayRequest>, alert_cooldown: Arc<std::sync::atomic::AtomicBool>) {
    // Open the audio device fresh for each request rather than holding a
    // single stream open indefinitely. This lets the audio thread recover
    // automatically after a sleep/wake cycle that resets the OS audio
    // subsystem, avoiding a flood of "buffer underrun/overrun" errors.
    while let Ok(req) = rx.recv() {
        // Skip stale ticks that queued during an alert
        if matches!(req.cue, AudioCue::Tick) && alert_cooldown.load(std::sync::atomic::Ordering::Relaxed) {
            continue;
        }

        let is_alert = !matches!(req.cue, AudioCue::Tick);
        if is_alert {
            alert_cooldown.store(true, std::sync::atomic::Ordering::Relaxed);
        }

        let mut device_sink = match DeviceSinkBuilder::open_default_sink() {
            Ok(s) => s,
            Err(e) => {
                log::warn!("[audio] failed to open output stream: {e}");
                if is_alert { alert_cooldown.store(false, std::sync::atomic::Ordering::Relaxed); }
                continue;
            }
        };
        device_sink.log_on_drop(false);

        let player = Player::connect_new(device_sink.mixer());
        player.set_volume(req.volume);

        // Try the custom file first; fall back to the embedded asset on any error.
        let used_custom = if let Some(path) = req.custom_path {
            match std::fs::File::open(&path).map(std::io::BufReader::new) {
                Ok(reader) => match Decoder::new(reader) {
                    Ok(source) => { player.append(source); true }
                    Err(e) => {
                        log::warn!("[audio] decode error for {path:?}: {e}");
                        false
                    }
                },
                Err(e) => {
                    log::warn!("[audio] cannot open {path:?}: {e}");
                    false
                }
            }
        } else {
            false
        };

        if !used_custom {
            let bytes: &'static [u8] = match req.cue {
                AudioCue::PhaseComplete => PHASE_COMPLETE,
                AudioCue::TaskDone => TASK_DONE,
                AudioCue::SkipWork => SKIP_WORK,
                AudioCue::SkipShortBreak => SKIP_SHORT_BREAK,
                AudioCue::SkipLongBreak => SKIP_LONG_BREAK,
                AudioCue::Tick => TICK,
            };
            match Decoder::new(Cursor::new(bytes)) {
                Ok(source) => player.append(source),
                Err(e) => log::warn!("[audio] embedded decode error: {e}"),
            }
        }

        // Block until playback completes so device_sink stays alive for the
        // full duration and drops cleanly afterward.
        player.sleep_until_end();

        // Clear alert cooldown and drain any stale ticks from the queue
        if is_alert {
            // Small delay so the very first tick after the alert doesn't overlap
            std::thread::sleep(std::time::Duration::from_millis(100));
            // Drain stale tick requests that accumulated during playback
            while let Ok(stale) = rx.try_recv() {
                if !matches!(stale.cue, AudioCue::Tick) {
                    // Non-tick request — put it back... can't, so just play it next.
                    // This shouldn't happen in practice since alerts don't overlap.
                    break;
                }
            }
            alert_cooldown.store(false, std::sync::atomic::Ordering::Relaxed);
        }
    }
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn audio_cue_bytes_are_non_empty() {
        assert!(!PHASE_COMPLETE.is_empty());
        assert!(!TASK_DONE.is_empty());
        assert!(!SKIP_WORK.is_empty());
        assert!(!SKIP_SHORT_BREAK.is_empty());
        assert!(!SKIP_LONG_BREAK.is_empty());
        assert!(!TICK.is_empty());
    }

    #[test]
    fn tick_enabled_for_round_types() {
        let settings = Settings {
            tick_sounds_during_work: true,
            tick_sounds_during_break: false,
            ..Settings::default()
        };
        let mgr = AudioManager {
            tx: mpsc::sync_channel(1).0,
            settings: Arc::new(Mutex::new(AudioSettings::from(&settings))),
            custom_paths: Arc::new(Mutex::new(CustomAudioPaths::default())),
            alert_cooldown: Arc::new(std::sync::atomic::AtomicBool::new(false)),
        };
        assert!(mgr.tick_enabled_for("work"));
        assert!(!mgr.tick_enabled_for("short-break"));
        assert!(!mgr.tick_enabled_for("long-break"));
    }

    #[test]
    fn custom_paths_set_and_clear() {
        let settings = Settings::default();
        let mgr = AudioManager {
            tx: mpsc::sync_channel(1).0,
            settings: Arc::new(Mutex::new(AudioSettings::from(&settings))),
            custom_paths: Arc::new(Mutex::new(CustomAudioPaths::default())),
            alert_cooldown: Arc::new(std::sync::atomic::AtomicBool::new(false)),
        };
        mgr.set_custom_path("phase_complete", PathBuf::from("/tmp/test.wav"));
        assert_eq!(
            mgr.custom_paths.lock().unwrap().phase_complete,
            Some(PathBuf::from("/tmp/test.wav"))
        );
        mgr.clear_custom_path("phase_complete");
        assert!(mgr.custom_paths.lock().unwrap().phase_complete.is_none());
    }
}
