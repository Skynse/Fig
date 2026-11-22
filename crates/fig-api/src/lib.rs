//! Fig's control API: what a running editor lets other code read and do.
//!
//! The window implements [`FigApi`]; extensions (an MCP server, a script runner) get it through [`Extension::start`]
//! and drive the editor with it. Nothing here knows about a transport or protocol, and nothing in the editor knows
//! about the extensions. Every call blocks until the editor has done it, and must come from a thread other than the
//! UI thread. Edits go through the editor's undo history, as the user's own do.
//!
//! Times are seconds on the timeline unless a field says otherwise.

use std::path::PathBuf;
use std::sync::Arc;

use serde::Serialize;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Error(pub String);

impl std::fmt::Display for Error {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for Error {}

impl From<String> for Error {
    fn from(s: String) -> Error {
        Error(s)
    }
}

impl From<&str> for Error {
    fn from(s: &str) -> Error {
        Error(s.to_string())
    }
}

pub type Result<T> = std::result::Result<T, Error>;

/// A parameter or property value. Numbers cover sliders, whole numbers and colours (ARGB as a number); a choice can
/// be given by its index or its name.
#[derive(Clone, Debug, PartialEq, Serialize)]
#[serde(untagged)]
pub enum Value {
    Number(f64),
    Bool(bool),
    Text(String),
}

#[derive(Clone, Debug, Serialize)]
pub struct Status {
    /// "home" or "editor".
    pub screen: String,
    /// The project being opened, while it is.
    pub opening: Option<String>,
    pub project: Option<ProjectInfo>,
    pub playhead: f64,
    pub playing: bool,
    pub duration: f64,
    pub fps: f64,
    /// The frame's size in pixels, from its shape and the media in it.
    pub frame: (u32, u32),
    pub unsaved: bool,
    pub can_undo: bool,
    pub can_redo: bool,
    pub selection: Vec<String>,
}

#[derive(Clone, Debug, Serialize)]
pub struct ProjectInfo {
    pub id: String,
    pub name: String,
    /// Last saved, as stored.
    pub updated: String,
}

#[derive(Clone, Debug, Serialize)]
pub struct TimelineInfo {
    pub name: Option<String>,
    pub fps: f64,
    /// "16:9", "9:16"...
    pub shape: String,
    pub duration: f64,
    /// Top to bottom: higher video tracks cover lower ones.
    pub tracks: Vec<TrackInfo>,
    /// Markers on the timeline itself (track and clip markers are on those).
    pub markers: Vec<MarkerInfo>,
}

#[derive(Clone, Debug, Serialize)]
pub struct TrackInfo {
    pub id: String,
    /// "video" or "audio".
    pub kind: String,
    pub label: String,
    pub visible: bool,
    pub muted: bool,
    pub clips: Vec<ClipInfo>,
    pub effects: Vec<EffectInfo>,
    pub markers: Vec<MarkerInfo>,
}

#[derive(Clone, Debug, Serialize)]
pub struct ClipInfo {
    pub id: String,
    /// "video", "audio", "text" or "image".
    pub kind: String,
    /// The media file's name, or a text clip's text.
    pub name: String,
    pub media_id: Option<String>,
    pub start: f64,
    pub duration: f64,
    /// Where it starts and ends in its source, in source seconds.
    pub source_in: Option<f64>,
    pub source_out: Option<f64>,
    pub speed: f64,
    pub enabled: bool,
    pub linked_to: Option<String>,
    pub effects: Vec<EffectInfo>,
    pub markers: Vec<MarkerInfo>,
    /// The transition into it from the clip before, and out of it into the clip after.
    pub transition_in: Option<TransitionInfo>,
    pub transition_out: Option<TransitionInfo>,
}

#[derive(Clone, Debug, Serialize)]
pub struct EffectInfo {
    pub id: String,
    pub type_id: String,
    pub enabled: bool,
    /// On a track: when it starts and how long it lasts.
    pub start: Option<f64>,
    pub duration: Option<f64>,
    pub params: serde_json::Map<String, serde_json::Value>,
}

#[derive(Clone, Debug, Serialize)]
pub struct TransitionInfo {
    pub type_id: String,
    pub duration: f64,
}

#[derive(Clone, Debug, Serialize)]
pub struct MarkerInfo {
    pub id: String,
    pub name: String,
    /// On a clip, from the clip's start; elsewhere, on the timeline.
    pub time: f64,
    /// Zero for a point marker.
    pub duration: f64,
    pub color: String,
}

#[derive(Clone, Debug, Serialize)]
pub struct MediaInfo {
    pub id: String,
    /// "video", "audio" or "image".
    pub kind: String,
    pub name: String,
    pub path: String,
    pub duration: f64,
    pub width: i32,
    pub height: i32,
}

/// A setting with a slider, switch, colour or list: a clip property, an effect's or a transition's parameter.
#[derive(Clone, Debug, Serialize)]
pub struct ParamInfo {
    pub key: String,
    pub label: String,
    /// "number", "integer", "bool", "color" or "choice".
    pub kind: String,
    pub min: f64,
    pub max: f64,
    pub default: f64,
    pub choices: Vec<String>,
    /// Can take keyframes.
    pub keyable: bool,
    /// The value now (at the playhead, for an animated property).
    pub value: Option<serde_json::Value>,
    /// Where the inspector shows it ("Picture / Compositing").
    pub group: String,
}

#[derive(Clone, Debug, Serialize)]
pub struct CatalogEntry {
    pub type_id: String,
    pub name: String,
    pub category: String,
    pub description: String,
    pub params: Vec<ParamInfo>,
    /// A transition's length when none is given.
    pub default_duration: Option<f64>,
}

#[derive(Clone, Debug, Serialize)]
pub struct CommandInfo {
    pub name: String,
    pub label: String,
    pub section: String,
    pub shortcut: String,
}

/// A rendered frame, PNG-encoded.
#[derive(Clone, Debug)]
pub struct Image {
    pub width: u32,
    pub height: u32,
    pub png: Vec<u8>,
}

#[derive(Clone, Debug, Serialize)]
pub struct JobInfo {
    pub name: String,
    pub path: String,
    /// 0 to 1.
    pub progress: f64,
    pub done: bool,
    pub error: Option<String>,
}

#[derive(Clone, Debug, Default)]
pub struct ExportOptions {
    /// The shorter side in pixels (1080 for 1080×1920); the project's preference when none.
    pub short_side: Option<u32>,
    /// Quality as x264's CRF (lower is better); the project's preference when none.
    pub crf: Option<u32>,
    /// Only this stretch of the timeline.
    pub range: Option<(f64, f64)>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TrackKind {
    Video,
    Audio,
}

/// A change to a track; fields left `None` stay as they are.
#[derive(Clone, Debug, Default)]
pub struct TrackChange {
    /// An empty name goes back to the automatic one ("V2").
    pub name: Option<String>,
    pub visible: Option<bool>,
    pub muted: Option<bool>,
    /// Its new place, 0 at the top.
    pub index: Option<usize>,
}

/// What a running Fig lets other code read and do.
pub trait FigApi: Send + Sync {
    // the app and its projects
    fn status(&self) -> Result<Status>;
    fn list_projects(&self) -> Result<Vec<ProjectInfo>>;
    /// Opens a project and returns once the editor shows it (saving the one open first).
    fn open_project(&self, id: &str) -> Result<()>;
    /// Makes a project, opens it and returns its id.
    fn create_project(&self, name: &str) -> Result<String>;
    fn save(&self) -> Result<()>;
    /// Saves and goes back to the project list.
    fn close_project(&self) -> Result<()>;

    // reading
    fn timeline(&self) -> Result<TimelineInfo>;
    fn media(&self) -> Result<Vec<MediaInfo>>;
    /// A clip's properties (the inspector's settings) and their values.
    fn clip_properties(&self, clip_id: &str) -> Result<Vec<ParamInfo>>;
    fn effect_catalog(&self) -> Result<Vec<CatalogEntry>>;
    fn transition_catalog(&self) -> Result<Vec<CatalogEntry>>;
    /// The editor's named actions, for [`FigApi::run_command`].
    fn commands(&self) -> Result<Vec<CommandInfo>>;
    /// The text styles a text clip can start from.
    fn text_presets(&self) -> Result<Vec<String>>;

    // media
    /// Imports files into the project and returns their media ids once they're in.
    fn import_media(&self, paths: Vec<PathBuf>) -> Result<Vec<String>>;

    // clips
    /// Puts media on the timeline at `start` (on `track`, else the first free one of its kind) and returns the new
    /// clip's id (a video with sound returns the video's; its audio is linked to it).
    fn add_clip(&self, media_id: &str, track: Option<&str>, start: f64) -> Result<String>;
    /// Adds a text clip from a preset (the first when none) and returns its id.
    fn add_text(
        &self,
        text: &str,
        preset: Option<&str>,
        track: Option<&str>,
        start: f64,
        duration: Option<f64>,
    ) -> Result<String>;
    /// Moves a clip (and what's linked to it) to `start`, and to another track when given.
    fn move_clip(&self, clip_id: &str, start: f64, track: Option<&str>) -> Result<()>;
    /// Moves a clip's edges to these timeline times, taking more or less of its source.
    fn trim_clip(&self, clip_id: &str, start: Option<f64>, end: Option<f64>) -> Result<()>;
    /// Cuts a clip in two at `at` and returns both halves' ids.
    fn split_clip(&self, clip_id: &str, at: f64) -> Result<Vec<String>>;
    /// Deletes clips, closing the gaps they leave when `ripple`.
    fn delete_clips(&self, clip_ids: &[String], ripple: bool) -> Result<()>;
    fn set_clip_enabled(&self, clip_ids: &[String], enabled: bool) -> Result<()>;
    /// Sets a property (see [`FigApi::clip_properties`]) on clips. On an animated property this sets its key at the
    /// playhead, as the inspector does.
    fn set_property(&self, clip_ids: &[String], key: &str, value: Value) -> Result<()>;
    /// Sets a key on a clip's property at `time` (timeline seconds), animating it.
    fn set_keyframe(&self, clip_id: &str, key: &str, time: f64, value: f64) -> Result<()>;
    /// Removes a property's key at `time`, or all its keys when `time` is none.
    fn remove_keyframes(&self, clip_id: &str, key: &str, time: Option<f64>) -> Result<()>;
    fn set_text(&self, clip_ids: &[String], text: &str) -> Result<()>;
    /// Selects these clips (what selection-based commands act on); an empty list clears it.
    fn select(&self, clip_ids: &[String]) -> Result<()>;

    // tracks
    fn add_track(&self, kind: TrackKind) -> Result<String>;
    fn remove_track(&self, track_id: &str) -> Result<()>;
    fn change_track(&self, track_id: &str, change: TrackChange) -> Result<()>;

    // markers
    /// Adds a marker on a clip (`time` from its start), a track, or the timeline, and returns its id.
    fn add_marker(
        &self,
        owner: Option<&str>,
        time: f64,
        duration: f64,
        name: &str,
        color: Option<&str>,
    ) -> Result<String>;
    fn delete_markers(&self, marker_ids: &[String]) -> Result<()>;

    // effects and transitions
    /// Adds an effect to a clip or a track and returns its id. It covers `range` (start and length, timeline
    /// seconds) when given, else the whole clip or track.
    fn add_effect(&self, owner: &str, type_id: &str, range: Option<(f64, f64)>) -> Result<String>;
    /// Moves or resizes an effect to `range` (start and length, timeline seconds), or spreads it over its whole
    /// owner when none. `move_keyframes` carries its keyframes along with its start.
    fn set_effect_range(
        &self,
        owner: &str,
        effect_id: &str,
        range: Option<(f64, f64)>,
        move_keyframes: bool,
    ) -> Result<()>;
    fn remove_effect(&self, owner: &str, effect_id: &str) -> Result<()>;
    fn set_effect_param(&self, owner: &str, effect_id: &str, key: &str, value: Value) -> Result<()>;
    /// Keys an effect's numeric settings at `time` (timeline seconds), animating them; `easing` (linear, ease-in,
    /// ease-out, ease-in-out, hold) shapes the move into this key. Ease in and out when none.
    fn set_effect_keyframe(
        &self,
        owner: &str,
        effect_id: &str,
        time: f64,
        values: &[(String, f64)],
        easing: Option<&str>,
    ) -> Result<()>;
    /// Puts a transition on the cut between two abutting clips, or on the cut after a clip when `right` is none.
    fn add_transition(&self, left: &str, right: Option<&str>, type_id: &str, duration: Option<f64>) -> Result<()>;
    fn remove_transition(&self, left: &str, right: &str) -> Result<()>;

    // history and playback
    fn undo(&self) -> Result<bool>;
    fn redo(&self) -> Result<bool>;
    fn seek(&self, time: f64) -> Result<()>;
    fn play(&self) -> Result<()>;
    fn pause(&self) -> Result<()>;
    /// Runs one of the editor's named actions (see [`FigApi::commands`]) as its menu item or shortcut would.
    fn run_command(&self, name: &str) -> Result<()>;

    // output
    /// Renders the frame at `time` (the playhead when none), its longer side at most `max_side` pixels.
    fn render_frame(&self, time: Option<f64>, max_side: u32) -> Result<Image>;
    /// Starts exporting the timeline to an MP4 at `path`, in the background.
    fn export(&self, path: PathBuf, options: ExportOptions) -> Result<()>;
    fn jobs(&self) -> Result<Vec<JobInfo>>;
    /// Cancels running exports: the one writing `path`, or all when none. Returns how many.
    fn cancel_export(&self, path: Option<&str>) -> Result<usize>;
}

/// Something that plugs into Fig: started with the API once the window is up, and offered the command line first.
pub trait Extension: Send + Sync {
    fn name(&self) -> &'static str;

    /// A command line meant for this extension (`fig mcp`) runs here instead of the window; returns the exit code,
    /// or `None` when the arguments aren't its.
    fn run_cli(&self, _args: &[String]) -> Option<i32> {
        None
    }

    /// Called once the window is up, from the UI thread: start your own threads here.
    fn start(&self, _api: Arc<dyn FigApi>) {}
}
