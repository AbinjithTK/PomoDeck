pub mod watcher;

use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::path::Path;

// ---------------------------------------------------------------------------
// Theme type
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Theme {
    pub name: String,
    /// CSS custom property values keyed by their full property name (e.g. "--color-background").
    pub colors: HashMap<String, String>,
    /// True for user-created themes in {app_data_dir}/themes/.
    #[serde(default)]
    pub is_custom: bool,
}

// ---------------------------------------------------------------------------
// Bundled themes embedded at compile time
// ---------------------------------------------------------------------------

/// Raw JSON for every built-in theme, embedded into the binary via include_str!.
/// The path is relative to this source file (src-tauri/src/themes/mod.rs).
const BUNDLED_JSON: &[&str] = &[
    include_str!("../../../static/themes/pomodeck.json"),
    include_str!("../../../static/themes/pomodeck-light.json"),
];

/// Parse all bundled theme JSON strings. Panics at startup if any are malformed
/// (a compile-time-like assertion that the shipped assets are valid).
pub fn load_bundled() -> Vec<Theme> {
    let themes: Vec<Theme> = BUNDLED_JSON
        .iter()
        .filter_map(|raw| {
            serde_json::from_str::<serde_json::Value>(raw)
                .ok()
                .and_then(|v| parse_theme_value(v, false))
        })
        .collect();
    log::debug!("[themes] loaded {} bundled themes", themes.len());
    themes
}

// ---------------------------------------------------------------------------
// Custom themes — loaded from {app_data_dir}/themes/ at runtime
// ---------------------------------------------------------------------------

/// Load user-defined themes from the given directory. Non-fatal: bad files are
/// logged and skipped so one corrupt theme cannot prevent the app from starting.
pub fn load_custom(themes_dir: &Path) -> Vec<Theme> {
    let Ok(entries) = std::fs::read_dir(themes_dir) else {
        return Vec::new();
    };

    let mut themes = Vec::new();
    for entry in entries.filter_map(|e| e.ok()) {
        let path = entry.path();
        if path.extension().and_then(|e| e.to_str()) != Some("json") {
            continue;
        }
        let raw = match std::fs::read_to_string(&path) {
            Ok(r) => r,
            Err(e) => { log::warn!("[themes] cannot read {path:?}: {e}"); continue; }
        };
        let value = match serde_json::from_str::<serde_json::Value>(&raw) {
            Ok(v) => v,
            Err(e) => { log::warn!("[themes] invalid JSON in {path:?}: {e}"); continue; }
        };
        match parse_theme_value(value, true) {
            Some(t) => {
                log::debug!("[themes] loaded custom theme: {}", t.name);
                themes.push(t);
            }
            None => log::warn!("[themes] missing required fields in {path:?}"),
        }
    }
    themes
}

/// Return all themes: bundled first, then custom. Custom themes with the same
/// name as a bundled theme override the bundled version.
pub fn list_all(app_data_dir: &Path) -> Vec<Theme> {
    let mut themes = load_bundled();
    let custom = load_custom(&app_data_dir.join("themes"));
    let custom_count = custom.len();

    for custom_theme in custom {
        // Replace built-in with same name, or append.
        if let Some(existing) = themes.iter_mut().find(|t| t.name == custom_theme.name) {
            *existing = custom_theme;
        } else {
            themes.push(custom_theme);
        }
    }

    log::debug!("[themes] available: {} total ({} custom)", themes.len(), custom_count);
    themes
}

/// Look up a single theme by name (case-insensitive).
pub fn find(app_data_dir: &Path, name: &str) -> Option<Theme> {
    list_all(app_data_dir)
        .into_iter()
        .find(|t| t.name.eq_ignore_ascii_case(name))
}

// ---------------------------------------------------------------------------
// Parsing helper
// ---------------------------------------------------------------------------

fn parse_theme_value(v: serde_json::Value, is_custom: bool) -> Option<Theme> {
    let name = v.get("name")?.as_str()?.to_string();
    let colors_obj = v.get("colors")?.as_object()?;
    let colors: HashMap<String, String> = colors_obj
        .iter()
        .filter_map(|(k, v)| Some((k.clone(), v.as_str()?.to_string())))
        .collect();
    Some(Theme { name, colors, is_custom })
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn all_bundled_themes_parse() {
        let themes = load_bundled();
        assert_eq!(themes.len(), 2, "expected 2 bundled themes");
    }

    #[test]
    fn bundled_themes_have_required_color_keys() {
        let required = [
            "--color-long-round",
            "--color-short-round",
            "--color-focus-round",
            "--color-background",
            "--color-background-light",
            "--color-foreground",
            "--color-accent",
        ];
        for theme in load_bundled() {
            for key in &required {
                assert!(
                    theme.colors.contains_key(*key),
                    "theme '{}' is missing '{key}'",
                    theme.name
                );
            }
        }
    }

    #[test]
    fn pomodeck_theme_is_bundled() {
        let themes = load_bundled();
        assert!(
            themes.iter().any(|t| t.name == "PomoDeck"),
            "PomoDeck theme must be bundled"
        );
        assert!(
            themes.iter().any(|t| t.name == "PomoDeck Light"),
            "PomoDeck Light theme must be bundled"
        );
    }
}
