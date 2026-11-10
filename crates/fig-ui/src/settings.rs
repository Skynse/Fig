//! The app's on-disk contracts, ported from `Fig.App/Services` and `Fig.Core/Input`:
//!
//! - `~/.fig/preferences.json` (`PreferenceLoader`/`PreferenceService`, `Preferences.cs`)
//! - `~/.fig/config/workspace.json` (`WorkspaceLayout`)
//! - `~/.fig/config/gestures.json` (`GestureRegistry`)
//! - `~/.fig/projects` (`ProjectStore`, already shared with the core port)
//! - `~/.fig/templates`, `~/.fig/presets`, `~/.fig/logs`, `~/.fig/models/whisper`
//!
//! These files are written by the C# build, so they are read and written byte-for-byte in the same
//! JSON shape (PascalCase properties, the same defaults, unknown keys preserved, a `.bad` copy when
//! the file cannot be parsed). Nothing about the user's existing data changes when the Rust build
//! takes over.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

use fig_project::ProjectStore;

/// The project store at `~/.fig/projects` (`AppPaths.ProjectsDir`).
pub fn project_store() -> ProjectStore {
    ProjectStore::new(projects_dir())
}

// ---------------- paths (AppPaths.cs) ----------------

/// `~/.fig`, or `FIG_DATA_DIR` for tests/portable runs.
pub fn data_dir() -> PathBuf {
    if let Ok(dir) = std::env::var("FIG_DATA_DIR") {
        return PathBuf::from(dir);
    }
    let home = std::env::var("HOME").map(PathBuf::from).unwrap_or_else(|_| PathBuf::from("."));
    home.join(".fig")
}

pub fn preferences_path() -> PathBuf {
    data_dir().join("preferences.json")
}

pub fn config_dir() -> PathBuf {
    data_dir().join("config")
}

pub fn projects_dir() -> PathBuf {
    data_dir().join("projects")
}

pub fn templates_dir() -> PathBuf {
    data_dir().join("templates")
}

pub fn presets_dir() -> PathBuf {
    data_dir().join("presets")
}

pub fn log_dir() -> PathBuf {
    data_dir().join("logs")
}

pub fn speech_models_dir() -> PathBuf {
    data_dir().join("models").join("whisper")
}

pub fn workspace_layout_path() -> PathBuf {
    config_dir().join("workspace.json")
}

pub fn gesture_config_path() -> PathBuf {
    config_dir().join("gestures.json")
}

/// `AppPaths.EnsureDirectories`.
pub fn ensure_directories() {
    let _ = std::fs::create_dir_all(config_dir());
    let _ = std::fs::create_dir_all(projects_dir());
    let _ = std::fs::create_dir_all(templates_dir());
    let _ = std::fs::create_dir_all(presets_dir());
}

// ---------------- preferences.json (Preferences.cs) ----------------

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, Default)]
pub enum RendererChoice {
    #[default]
    Automatic,
    Gpu,
    Cpu,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, Default)]
pub enum EncoderChoice {
    #[default]
    Automatic,
    Nvenc,
    QuickSync,
    Amf,
    Vaapi,
    Software,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct GeneralPreferences {
    pub autosave_seconds: f64,
    pub ask_before_closing_unsaved: bool,
}

impl Default for GeneralPreferences {
    fn default() -> Self {
        GeneralPreferences { autosave_seconds: 0.8, ask_before_closing_unsaved: true }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct EditingPreferences {
    pub snap: bool,
    pub snap_reach_px: f64,
    pub linked_selection: bool,
    pub undo_steps: i32,
}

impl Default for EditingPreferences {
    fn default() -> Self {
        EditingPreferences { snap: true, snap_reach_px: 9.0, linked_selection: true, undo_steps: 100 }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct PlaybackPreferences {
    pub audio_scrub: bool,
    /// The C# property is `Loop`.
    #[serde(rename = "Loop")]
    pub loop_playback: bool,
    /// The timeline pages along with the playhead while playing.
    pub follow_playhead: bool,
}

impl Default for PlaybackPreferences {
    fn default() -> Self {
        PlaybackPreferences { audio_scrub: true, loop_playback: false, follow_playhead: true }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct ExportPreferences {
    pub quality: i32,
    pub folder: Option<String>,
    pub encoder: EncoderChoice,
}

impl Default for ExportPreferences {
    fn default() -> Self {
        ExportPreferences { quality: 23, folder: None, encoder: EncoderChoice::Automatic }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct RenderingPreferences {
    pub renderer: RendererChoice,
}

impl Default for RenderingPreferences {
    fn default() -> Self {
        RenderingPreferences { renderer: RendererChoice::Automatic }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize, Default)]
#[serde(rename_all = "PascalCase", default)]
pub struct KeyboardPreferences {
    /// Only the shortcuts the user changed: action id → gesture ("" means none).
    pub shortcuts: BTreeMap<String, String>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct CaptionPreferences {
    pub model: String,
    pub language: String,
    pub translate: bool,
    pub grouping: i32,
    pub style_all: bool,
}

impl Default for CaptionPreferences {
    fn default() -> Self {
        CaptionPreferences { model: "base".into(), language: "auto".into(), translate: false, grouping: 1, style_all: true }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct Preferences {
    pub schema_version: i32,
    pub general: GeneralPreferences,
    pub editing: EditingPreferences,
    pub playback: PlaybackPreferences,
    pub export: ExportPreferences,
    pub rendering: RenderingPreferences,
    pub keyboard: KeyboardPreferences,
    pub captions: CaptionPreferences,
    /// Keys this build doesn't know (`JsonExtensionData`): kept so saving doesn't erase them.
    #[serde(flatten)]
    pub unknown: BTreeMap<String, serde_json::Value>,
}

impl Default for Preferences {
    fn default() -> Self {
        Preferences {
            schema_version: 1,
            general: GeneralPreferences::default(),
            editing: EditingPreferences::default(),
            playback: PlaybackPreferences::default(),
            export: ExportPreferences::default(),
            rendering: RenderingPreferences::default(),
            keyboard: KeyboardPreferences::default(),
            captions: CaptionPreferences::default(),
            unknown: BTreeMap::new(),
        }
    }
}

impl Preferences {
    /// `Preferences.Normalize`: a hand-edited file can't take values out of range.
    pub fn normalize(&mut self) {
        self.general.autosave_seconds = self.general.autosave_seconds.clamp(0.2, 30.0);
        self.editing.snap_reach_px = self.editing.snap_reach_px.clamp(2.0, 40.0);
        self.editing.undo_steps = self.editing.undo_steps.clamp(10, 1000);
        self.export.quality = self.export.quality.clamp(0, 51);
    }

    /// `PreferenceLoader.Load`: never fails; a broken file is kept as `.bad` and defaults apply.
    pub fn load() -> Preferences {
        Preferences::load_from(&preferences_path())
    }

    pub fn load_from(path: &Path) -> Preferences {
        let Ok(text) = std::fs::read_to_string(path) else { return Preferences::default() };
        match serde_json::from_str::<Preferences>(&text) {
            Ok(mut prefs) => {
                prefs.normalize();
                prefs
            }
            Err(_) => {
                let _ = std::fs::rename(path, path.with_extension("json.bad"));
                Preferences::default()
            }
        }
    }

    /// `PreferenceLoader.Save`: pretty JSON, written to `.tmp` then renamed over the real file.
    pub fn save(&self) -> std::io::Result<()> {
        self.save_to(&preferences_path())
    }

    pub fn save_to(&self, path: &Path) -> std::io::Result<()> {
        if let Some(dir) = path.parent() {
            std::fs::create_dir_all(dir)?;
        }
        let text = serde_json::to_string_pretty(self)?;
        let temp = path.with_extension("json.tmp");
        std::fs::write(&temp, text)?;
        std::fs::rename(temp, path)
    }
}

// ---------------- workspace.json (WorkspaceLayout.cs) ----------------

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct WorkspaceLayout {
    pub left: f64,
    pub right: f64,
    pub timeline: f64,
    pub left_collapsed: bool,
    pub right_collapsed: bool,
}

impl Default for WorkspaceLayout {
    fn default() -> Self {
        WorkspaceLayout { left: 0.20, right: 0.22, timeline: 0.44, left_collapsed: false, right_collapsed: false }
    }
}

impl WorkspaceLayout {
    pub fn load() -> WorkspaceLayout {
        Self::load_from(&workspace_layout_path())
    }

    /// `WorkspaceLayout.Load`: a missing or bad file falls back to the defaults, clamped.
    pub fn load_from(path: &Path) -> WorkspaceLayout {
        let Ok(text) = std::fs::read_to_string(path) else { return WorkspaceLayout::default() };
        let mut layout: WorkspaceLayout = serde_json::from_str(&text).unwrap_or_default();
        layout.clamp();
        layout
    }

    pub fn save(&self) -> std::io::Result<()> {
        self.save_to(&workspace_layout_path())
    }

    pub fn save_to(&self, path: &Path) -> std::io::Result<()> {
        if let Some(dir) = path.parent() {
            std::fs::create_dir_all(dir)?;
        }
        let mut layout = self.clone();
        layout.clamp();
        std::fs::write(path, serde_json::to_string(&layout)?)
    }

    fn clamp(&mut self) {
        self.left = self.left.clamp(0.12, 0.40);
        self.right = self.right.clamp(0.12, 0.40);
        self.timeline = self.timeline.clamp(0.20, 0.75);
    }
}

// ---------------- gestures.json (Fig.Core/Input/GestureRegistry.cs) ----------------

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TimelineGesture {
    None,
    ZoomIn,
    ZoomOut,
    ScrollHorizontal,
    ScrollVertical,
    Pan,
    MoveClip,
    TallerTracks,
    ShorterTracks,
}

impl TimelineGesture {
    fn parse(name: &str) -> TimelineGesture {
        match name.to_ascii_lowercase().as_str() {
            "zoomin" => TimelineGesture::ZoomIn,
            "zoomout" => TimelineGesture::ZoomOut,
            "scrollhorizontal" => TimelineGesture::ScrollHorizontal,
            "scrollvertical" => TimelineGesture::ScrollVertical,
            "pan" => TimelineGesture::Pan,
            "moveclip" => TimelineGesture::MoveClip,
            "tallertracks" => TimelineGesture::TallerTracks,
            "shortertracks" => TimelineGesture::ShorterTracks,
            _ => TimelineGesture::None,
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct GestureBinding {
    #[serde(rename = "Pattern")]
    pub pattern: String,
    #[serde(rename = "Gesture")]
    pub gesture: String,
}

/// The wheel/drag bindings, with `GestureRegistry`'s defaults, the user's overlay, and the
/// navigation migration it applies on load (plain wheel scrolls tracks, middle-drag pans time).
#[derive(Clone, Debug, Default)]
pub struct GestureMap {
    bindings: BTreeMap<String, TimelineGesture>,
}

impl GestureMap {
    pub fn load() -> GestureMap {
        Self::load_from(&gesture_config_path())
    }

    pub fn load_from(path: &Path) -> GestureMap {
        let mut map = GestureMap::defaults();
        let Ok(text) = std::fs::read_to_string(path) else { return map };
        if let Ok(bindings) = serde_json::from_str::<Vec<GestureBinding>>(&text) {
            for binding in bindings {
                if binding.pattern.is_empty() {
                    continue;
                }
                if let Some(pattern) = canonical_pattern(&binding.pattern) {
                    map.bindings.insert(pattern, TimelineGesture::parse(&binding.gesture));
                }
            }
        }
        map.migrate_navigation_defaults();
        map
    }

    pub fn defaults() -> GestureMap {
        let mut map = GestureMap::default();
        for (pattern, gesture) in [
            ("Ctrl+WheelUp", TimelineGesture::ZoomIn),
            ("Ctrl+WheelDown", TimelineGesture::ZoomOut),
            ("Shift+WheelUp", TimelineGesture::ScrollHorizontal),
            ("Shift+WheelDown", TimelineGesture::ScrollHorizontal),
            ("Alt+WheelUp", TimelineGesture::TallerTracks),
            ("Alt+WheelDown", TimelineGesture::ShorterTracks),
            ("WheelUp", TimelineGesture::ScrollVertical),
            ("WheelDown", TimelineGesture::ScrollVertical),
            ("Middle", TimelineGesture::Pan),
            ("Middle+Move", TimelineGesture::Pan),
            ("Left+Move", TimelineGesture::MoveClip),
        ] {
            map.bindings.insert(canonical_pattern(pattern).unwrap_or_default(), gesture);
        }
        map
    }

    fn migrate_navigation_defaults(&mut self) {
        for direction in ["WheelUp", "WheelDown"] {
            if self.bindings.get(direction) == Some(&TimelineGesture::ScrollHorizontal) {
                self.bindings.insert(direction.into(), TimelineGesture::ScrollVertical);
            }
        }
        self.bindings.insert("Middle".into(), TimelineGesture::Pan);
    }

    pub fn resolve(&self, pattern: &str) -> TimelineGesture {
        canonical_pattern(pattern).and_then(|p| self.bindings.get(&p).copied()).unwrap_or(TimelineGesture::None)
    }

    /// What the wheel does with these modifiers (`TimelineView.OnPointerWheelChanged`).
    pub fn wheel(&self, ctrl: bool, shift: bool, alt: bool, up: bool) -> TimelineGesture {
        let mut p = String::new();
        for (on, name) in [(ctrl, "Ctrl+"), (shift, "Shift+"), (alt, "Alt+")] {
            if on {
                p.push_str(name);
            }
        }
        p.push_str(if up { "WheelUp" } else { "WheelDown" });
        self.resolve(&p)
    }

}

/// A pointer pattern in `GesturePatternParser.Serialize`'s order ("Ctrl+Shift+WheelUp", "Middle"); "Move" is
/// implied. None for an unknown token.
pub fn canonical_pattern(pattern: &str) -> Option<String> {
    let (mut ctrl, mut shift, mut alt) = (false, false, false);
    let (mut wheel, mut button) = (None, None);
    for part in pattern.split('+').map(str::trim).filter(|p| !p.is_empty()) {
        match part.to_ascii_lowercase().as_str() {
            "ctrl" => ctrl = true,
            "shift" => shift = true,
            "alt" => alt = true,
            "wheel" => wheel = Some("Wheel"),
            "wheelup" => wheel = Some("WheelUp"),
            "wheeldown" => wheel = Some("WheelDown"),
            "left" => button = Some("Left"),
            "middle" => button = Some("Middle"),
            "right" => button = Some("Right"),
            "move" => {}
            _ => return None,
        }
    }
    let mut parts: Vec<&str> = Vec::new();
    for (on, name) in [(ctrl, "Ctrl"), (shift, "Shift"), (alt, "Alt")] {
        if on {
            parts.push(name);
        }
    }
    parts.extend(wheel);
    parts.extend(button);
    Some(if parts.is_empty() { "None".into() } else { parts.join("+") })
}

// ---------------- keyboard gestures (ShortcutCatalog.cs) ----------------

/// One parsed shortcut: modifiers plus a key. Key names follow Avalonia's `KeyGesture.Parse`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct KeyCombo {
    pub ctrl: bool,
    pub shift: bool,
    pub alt: bool,
    pub key: char,
    /// Non-character keys, matched against `rune::Key`'s variants.
    pub special: Option<SpecialKey>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SpecialKey {
    Left,
    Right,
    Up,
    Down,
    Home,
    End,
    PageUp,
    PageDown,
    Delete,
    Backspace,
    Enter,
    Tab,
    Escape,
    Space,
    Function(u8),
}

/// Parses a stored gesture ("Ctrl+Shift+Z", "Ctrl+OemComma", "Left", "F5").
pub fn parse_gesture(gesture: &str) -> Option<KeyCombo> {
    let gesture = gesture.trim();
    if gesture.is_empty() {
        return None;
    }
    let mut combo = KeyCombo { ctrl: false, shift: false, alt: false, key: '\0', special: None };
    let mut parts: Vec<&str> = gesture.split('+').collect();
    // "Ctrl++" splits into ["Ctrl", "", ""]: the key is '+'
    if gesture.ends_with("++") {
        parts.retain(|p| !p.is_empty());
        parts.push("+");
    }
    let key_name = parts.pop()?;
    for modifier in parts {
        match modifier.to_ascii_lowercase().as_str() {
            "ctrl" | "control" | "cmd" | "meta" | "super" => combo.ctrl = true,
            "shift" => combo.shift = true,
            "alt" | "option" => combo.alt = true,
            _ => return None,
        }
    }
    let special = match key_name.to_ascii_lowercase().as_str() {
        "left" => Some(SpecialKey::Left),
        "right" => Some(SpecialKey::Right),
        "up" => Some(SpecialKey::Up),
        "down" => Some(SpecialKey::Down),
        "home" => Some(SpecialKey::Home),
        "end" => Some(SpecialKey::End),
        "pageup" => Some(SpecialKey::PageUp),
        "pagedown" => Some(SpecialKey::PageDown),
        "delete" => Some(SpecialKey::Delete),
        "backspace" => Some(SpecialKey::Backspace),
        "enter" | "return" => Some(SpecialKey::Enter),
        "tab" => Some(SpecialKey::Tab),
        "escape" => Some(SpecialKey::Escape),
        "space" => Some(SpecialKey::Space),
        name if name.len() > 1 && name.starts_with('f') => name[1..].parse().ok().map(SpecialKey::Function),
        _ => None,
    };
    if let Some(special) = special {
        combo.special = Some(special);
        return Some(combo);
    }
    let key = match key_name {
        "OemComma" => ',',
        "OemPeriod" => '.',
        "OemPlus" => '+',
        "OemMinus" => '-',
        "OemQuestion" => '/',
        "OemSemicolon" => ';',
        "OemQuotes" => '\'',
        "OemOpenBrackets" => '[',
        "OemCloseBrackets" => ']',
        "OemPipe" => '\\',
        "OemTilde" => '`',
        name if name.len() == 1 => name.chars().next()?.to_ascii_lowercase(),
        name if name.starts_with('D') && name.len() == 2 => name[1..].chars().next()?,
        _ => return None,
    };
    combo.key = key;
    Some(combo)
}

/// How a gesture is shown to people: "Ctrl+," rather than "Ctrl+OemComma" (`ShortcutCatalog.Display`).
pub fn display_gesture(gesture: &str) -> String {
    let Some(combo) = parse_gesture(gesture) else { return "None".into() };
    let mut out = String::new();
    if combo.ctrl {
        out.push_str("Ctrl+");
    }
    if combo.shift {
        out.push_str("Shift+");
    }
    if combo.alt {
        out.push_str("Alt+");
    }
    match combo.special {
        Some(SpecialKey::Function(n)) => out.push_str(&format!("F{n}")),
        Some(special) => out.push_str(match special {
            SpecialKey::Left => "Left",
            SpecialKey::Right => "Right",
            SpecialKey::Up => "Up",
            SpecialKey::Down => "Down",
            SpecialKey::Home => "Home",
            SpecialKey::End => "End",
            SpecialKey::PageUp => "Page Up",
            SpecialKey::PageDown => "Page Down",
            SpecialKey::Delete => "Delete",
            SpecialKey::Backspace => "Backspace",
            SpecialKey::Enter => "Enter",
            SpecialKey::Tab => "Tab",
            SpecialKey::Escape => "Escape",
            SpecialKey::Space => "Space",
            SpecialKey::Function(_) => unreachable!(),
        }),
        None => out.push(combo.key.to_ascii_uppercase()),
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn pointer_patterns_compare_in_one_spelling() {
        assert_eq!(canonical_pattern("Middle+Move").as_deref(), Some("Middle"));
        assert_eq!(canonical_pattern("wheelup+alt+ctrl").as_deref(), Some("Ctrl+Alt+WheelUp"));
        assert_eq!(canonical_pattern("Hyper+Wheel"), None);
        assert_eq!(GestureMap::defaults().wheel(true, false, false, true), TimelineGesture::ZoomIn);
    }

    const REAL_PREFERENCES: &str = r#"{
      "SchemaVersion": 1,
      "General": { "AutosaveSeconds": 15, "AskBeforeClosingUnsaved": true },
      "Editing": { "Snap": true, "SnapReachPx": 9, "LinkedSelection": true, "UndoSteps": 100 },
      "Playback": { "AudioScrub": true, "Loop": false },
      "Export": { "Quality": 23, "Folder": "~/Videos", "Encoder": "Automatic" },
      "Rendering": { "Renderer": "Automatic" },
      "Keyboard": { "Shortcuts": { "edit.redo": "Ctrl+Shift+Z", "edit.split": "B" } },
      "Captions": { "Model": "base", "Language": "auto", "Translate": false, "Grouping": 3, "StyleAll": true }
    }"#;

    #[test]
    fn reads_the_csharp_preferences_file() {
        let prefs: Preferences = serde_json::from_str(REAL_PREFERENCES).unwrap();
        assert_eq!(prefs.general.autosave_seconds, 15.0);
        assert_eq!(prefs.export.folder.as_deref(), Some("~/Videos"));
        assert_eq!(prefs.export.encoder, EncoderChoice::Automatic);
        assert_eq!(prefs.rendering.renderer, RendererChoice::Automatic);
        assert_eq!(prefs.captions.grouping, 3);
        assert_eq!(prefs.keyboard.shortcuts.get("edit.redo").map(String::as_str), Some("Ctrl+Shift+Z"));
    }

    #[test]
    fn saving_preserves_unknown_keys_and_shape() {
        let mut prefs: Preferences = serde_json::from_str(REAL_PREFERENCES).unwrap();
        prefs.unknown.insert("FutureSetting".into(), serde_json::json!({ "a": 1 }));
        let text = serde_json::to_string_pretty(&prefs).unwrap();
        assert!(text.contains("\"SchemaVersion\""));
        assert!(text.contains("\"FutureSetting\""));
        assert!(text.contains("\"Shortcuts\""));
        assert_eq!(serde_json::from_str::<Preferences>(&text).unwrap().schema_version, 1);
    }

    #[test]
    fn normalize_clamps_like_the_csharp_build() {
        let mut prefs = Preferences::default();
        prefs.general.autosave_seconds = 0.0;
        prefs.editing.snap_reach_px = 100.0;
        prefs.editing.undo_steps = 1;
        prefs.export.quality = 99;
        prefs.normalize();
        assert_eq!(prefs.general.autosave_seconds, 0.2);
        assert_eq!(prefs.editing.snap_reach_px, 40.0);
        assert_eq!(prefs.editing.undo_steps, 10);
        assert_eq!(prefs.export.quality, 51);
    }

    #[test]
    fn preferences_round_trip_through_a_file() {
        let dir = std::env::temp_dir().join(format!("fig-prefs-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("preferences.json");
        let prefs: Preferences = serde_json::from_str(REAL_PREFERENCES).unwrap();
        prefs.save_to(&path).unwrap();
        let loaded = Preferences::load_from(&path);
        assert_eq!(loaded.general.autosave_seconds, 15.0);
        assert_eq!(loaded.keyboard.shortcuts.get("edit.split").map(String::as_str), Some("B"));
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn reads_the_csharp_workspace_layout() {
        let json = r#"{"Left":0.3206751054852321,"Right":0.2521097046413502,"Timeline":0.42526315789473684,"LeftCollapsed":false,"RightCollapsed":false}"#;
        let layout: WorkspaceLayout = serde_json::from_str(json).unwrap();
        assert!((layout.left - 0.3207).abs() < 0.001);
        assert!((layout.timeline - 0.4253).abs() < 0.001);
        let out = serde_json::to_string(&layout).unwrap();
        assert!(out.contains("\"Left\"") && out.contains("\"Timeline\""));
    }

    #[test]
    fn gesture_file_overlays_defaults_and_migrates_navigation() {
        let dir = std::env::temp_dir().join(format!("fig-gestures-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("gestures.json");
        std::fs::write(
            &path,
            r#"[
              {"Pattern":"Ctrl+WheelUp","Gesture":"ZoomIn"},
              {"Pattern":"WheelUp","Gesture":"ScrollHorizontal"},
              {"Pattern":"WheelDown","Gesture":"ScrollHorizontal"},
              {"Pattern":"Left","Gesture":"MoveClip"}
            ]"#,
        )
        .unwrap();
        let map = GestureMap::load_from(&path);
        assert_eq!(map.resolve("Ctrl+WheelUp"), TimelineGesture::ZoomIn);
        // the plain wheel is migrated back to vertical track scrolling, as GestureRegistry does
        assert_eq!(map.resolve("WheelUp"), TimelineGesture::ScrollVertical);
        assert_eq!(map.resolve("Middle"), TimelineGesture::Pan);
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn parses_csharp_gestures_and_displays_them() {
        assert_eq!(display_gesture("Ctrl+OemComma"), "Ctrl+,");
        assert_eq!(display_gesture("Ctrl+OemPlus"), "Ctrl++");
        assert_eq!(display_gesture("Shift+Left"), "Shift+Left");
        let combo = parse_gesture("Ctrl+Shift+Z").unwrap();
        assert!(combo.ctrl && combo.shift);
        assert_eq!(combo.key, 'z');
        assert!(parse_gesture("Ctrl+D1").is_some());
        assert!(parse_gesture("").is_none());
    }
}


/// Loads a project thumbnail (C# writes JPEG/PNG beside the project) as Rune pixels, scaled to the
/// card's box.
pub fn thumbnail_pixels(path: &Path, width: u32, height: u32) -> Option<rune::Pixels> {
    let image = image::open(path).ok()?;
    let rgba = image.to_rgba8();
    let (sw, sh) = rgba.dimensions();
    if sw == 0 || sh == 0 {
        return None;
    }
    let mut out = vec![0u8; (width * height * 4) as usize];
    for y in 0..height {
        let sy = (u64::from(y) * u64::from(sh) / u64::from(height)).min(u64::from(sh - 1)) as u32;
        for x in 0..width {
            let sx = (u64::from(x) * u64::from(sw) / u64::from(width)).min(u64::from(sw - 1)) as u32;
            let src = rgba.get_pixel(sx, sy);
            let dst = ((y * width + x) * 4) as usize;
            out[dst..dst + 4].copy_from_slice(&src.0);
        }
    }
    let pixels = rune::Pixels::new();
    pixels.set(width, height, out);
    Some(pixels)
}
