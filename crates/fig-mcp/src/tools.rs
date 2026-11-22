//! The MCP tools: each one turns its arguments into a `fig_api` call and its answer into JSON text.

use std::path::PathBuf;
use std::sync::Arc;

use base64::Engine;
use fig_api::{ExportOptions, FigApi, TrackChange, TrackKind, Value};
use rmcp::handler::server::{router::tool::ToolRouter, wrapper::Parameters};
use rmcp::model::{CallToolResult, ContentBlock, Implementation, ServerCapabilities, ServerConfig};
use rmcp::{ErrorData, ServerHandler, tool, tool_handler, tool_router};
use schemars::JsonSchema;
use serde::{Deserialize, Serialize};

const INSTRUCTIONS: &str = "Fig is a video editor for short vertical reels. These tools drive the Fig window that's \
open on the user's screen: they see what it shows, and every edit goes through its undo history.\n\
Start with `status`; open a project with `list_projects` and `open_project`. `get_timeline` gives the ids every \
other tool takes: tracks are listed top to bottom, and higher video tracks cover lower ones. Times are seconds on \
the timeline, except a clip marker's, which counts from its clip's start. `get_clip_properties` lists what \
`set_property` and `set_keyframe` take; `list_effects` and `list_transitions` give type ids and their settings. \
Use `render_frame` to look at the result.";

type Reply = Result<CallToolResult, ErrorData>;

#[derive(Clone)]
pub struct FigServer {
    api: Arc<dyn FigApi>,
    tool_router: ToolRouter<Self>,
}

/// An answer as text: JSON, or "ok" for none; a failure as a tool error the model can read.
fn reply<T: Serialize>(result: fig_api::Result<T>) -> CallToolResult {
    match result {
        Ok(value) => {
            let json = serde_json::to_value(&value).unwrap_or(serde_json::Value::Null);
            let text = if json.is_null() { "ok".to_string() } else { json.to_string() };
            CallToolResult::success(vec![ContentBlock::text(text)])
        }
        Err(e) => CallToolResult::error(vec![ContentBlock::text(e.0)]),
    }
}

fn value(v: serde_json::Value) -> fig_api::Result<Value> {
    match v {
        serde_json::Value::Number(n) => Ok(Value::Number(n.as_f64().unwrap_or_default())),
        serde_json::Value::Bool(b) => Ok(Value::Bool(b)),
        serde_json::Value::String(s) => Ok(Value::Text(s)),
        other => Err(format!("a value is a number, true/false or text, not {other}").into()),
    }
}

impl FigServer {
    pub fn new(api: Arc<dyn FigApi>) -> FigServer {
        FigServer { api, tool_router: Self::tool_router() }
    }

    /// Runs a blocking API call off the async thread.
    async fn call<T: Serialize + Send + 'static>(
        &self,
        f: impl FnOnce(&dyn FigApi) -> fig_api::Result<T> + Send + 'static,
    ) -> Reply {
        let api = self.api.clone();
        let result = tokio::task::spawn_blocking(move || f(api.as_ref()))
            .await
            .map_err(|e| ErrorData::internal_error(e.to_string(), None))?;
        Ok(reply(result))
    }
}

// ---- arguments

#[derive(Deserialize, JsonSchema)]
pub struct ProjectId {
    /// A project's id, from `list_projects`.
    id: String,
}

#[derive(Deserialize, JsonSchema)]
pub struct NewProject {
    name: String,
}

#[derive(Deserialize, JsonSchema)]
pub struct ClipId {
    clip_id: String,
}

#[derive(Deserialize, JsonSchema)]
pub struct ClipIds {
    clip_ids: Vec<String>,
}

#[derive(Deserialize, JsonSchema)]
pub struct Import {
    /// Files to import (absolute paths): video, audio or pictures.
    paths: Vec<PathBuf>,
}

#[derive(Deserialize, JsonSchema)]
pub struct AddClip {
    /// From `list_media`.
    media_id: String,
    /// Where it starts.
    start: f64,
    /// A track to put it on; else the first one of its kind with room.
    track_id: Option<String>,
}

#[derive(Deserialize, JsonSchema)]
pub struct AddText {
    text: String,
    start: f64,
    /// Seconds it lasts; the preset's when none.
    duration: Option<f64>,
    /// A style from `list_text_presets`; the first when none.
    preset: Option<String>,
    track_id: Option<String>,
}

#[derive(Deserialize, JsonSchema)]
pub struct MoveClip {
    clip_id: String,
    /// Its new start. Clips linked to it (its sound) move by as much.
    start: f64,
    /// Another track to move it onto.
    track_id: Option<String>,
}

#[derive(Deserialize, JsonSchema)]
pub struct TrimClip {
    clip_id: String,
    /// Where its first edge goes on the timeline; unchanged when none.
    start: Option<f64>,
    /// Where its last edge goes on the timeline; unchanged when none.
    end: Option<f64>,
}

#[derive(Deserialize, JsonSchema)]
pub struct SplitClip {
    clip_id: String,
    /// Where to cut, on the timeline.
    at: f64,
}

#[derive(Deserialize, JsonSchema)]
pub struct DeleteClips {
    clip_ids: Vec<String>,
    /// Close the gaps they leave by pulling later clips back.
    #[serde(default)]
    ripple: bool,
}

#[derive(Deserialize, JsonSchema)]
pub struct EnableClips {
    clip_ids: Vec<String>,
    enabled: bool,
}

#[derive(Deserialize, JsonSchema)]
pub struct SetProperty {
    clip_ids: Vec<String>,
    /// A key from `get_clip_properties` (opacity, volume, scale...).
    key: String,
    /// A number, true/false, a colour as "#RRGGBB", or a choice's name or index.
    value: serde_json::Value,
}

#[derive(Deserialize, JsonSchema)]
pub struct SetKeyframe {
    clip_id: String,
    /// An animatable property's key.
    key: String,
    /// When, on the timeline.
    time: f64,
    value: f64,
}

#[derive(Deserialize, JsonSchema)]
pub struct RemoveKeyframes {
    clip_id: String,
    key: String,
    /// The key at this timeline time; all of the property's keys when none.
    time: Option<f64>,
}

#[derive(Deserialize, JsonSchema)]
pub struct SetText {
    clip_ids: Vec<String>,
    text: String,
}

#[derive(Deserialize, JsonSchema)]
#[serde(rename_all = "lowercase")]
pub enum Kind {
    Video,
    Audio,
}

#[derive(Deserialize, JsonSchema)]
pub struct AddTrack {
    kind: Kind,
}

#[derive(Deserialize, JsonSchema)]
pub struct TrackId {
    track_id: String,
}

#[derive(Deserialize, JsonSchema)]
pub struct UpdateTrack {
    track_id: String,
    /// A new name; empty for the automatic one.
    name: Option<String>,
    visible: Option<bool>,
    muted: Option<bool>,
    /// Its new place, 0 at the top.
    index: Option<usize>,
}

#[derive(Deserialize, JsonSchema)]
pub struct AddMarker {
    /// When: on the timeline, or from the clip's start for a clip marker.
    time: f64,
    /// A clip or track to put it on; the timeline when none.
    owner_id: Option<String>,
    /// Seconds it spans (timeline markers only); a point marker when none.
    duration: Option<f64>,
    name: Option<String>,
    /// "#RRGGBB".
    color: Option<String>,
}

#[derive(Deserialize, JsonSchema)]
pub struct MarkerIds {
    marker_ids: Vec<String>,
}

#[derive(Deserialize, JsonSchema)]
pub struct AddEffect {
    /// The clip or track it goes on. On a track it acts on whatever plays there, across cuts.
    owner_id: String,
    /// From `list_effects`.
    type_id: String,
    /// Where it starts on the timeline; with `duration`, else it covers the whole clip or track.
    start: Option<f64>,
    /// How long it lasts.
    duration: Option<f64>,
}

#[derive(Deserialize, JsonSchema)]
pub struct SetEffectRange {
    owner_id: String,
    effect_id: String,
    /// Its new start on the timeline; with `duration`, else it spreads over the whole clip or track.
    start: Option<f64>,
    duration: Option<f64>,
    /// Carry its keyframes along with its start.
    #[serde(default = "yes")]
    move_keyframes: bool,
}

fn yes() -> bool {
    true
}

fn span(start: Option<f64>, duration: Option<f64>) -> Result<Option<(f64, f64)>, String> {
    match (start, duration) {
        (None, None) => Ok(None),
        (Some(s), Some(d)) if d > 0.0 => Ok(Some((s, d))),
        _ => Err("give both start and a positive duration, or neither".into()),
    }
}

#[derive(Deserialize, JsonSchema)]
pub struct EffectRef {
    owner_id: String,
    effect_id: String,
}

#[derive(Deserialize, JsonSchema)]
pub struct SetEffectParam {
    owner_id: String,
    effect_id: String,
    /// One of the effect type's settings (`list_effects`).
    key: String,
    /// A number, true/false, a colour as "#RRGGBB", or a choice's name or index.
    value: serde_json::Value,
}

#[derive(Deserialize, JsonSchema)]
pub struct SetEffectKeyframe {
    /// The clip or track the effect is on.
    owner_id: String,
    effect_id: String,
    /// When, on the timeline.
    time: f64,
    /// Settings to key here and their values, e.g. {"zoom": 2, "centerX": 0.3, "centerY": 0.6} on a Transform.
    values: std::collections::BTreeMap<String, f64>,
    /// How it moves into this key: linear, ease-in, ease-out, ease-in-out (when none) or hold.
    easing: Option<String>,
}

#[derive(Deserialize, JsonSchema)]
pub struct CancelExport {
    /// The export writing this file; every running one when none.
    path: Option<String>,
}

#[derive(Deserialize, JsonSchema)]
pub struct AddTransition {
    /// The clip before the cut.
    left_clip_id: String,
    /// The clip after it, touching it on the same track; none for whichever clip touches it there. To fade a clip
    /// out to black, set its `fadeOut` property instead.
    right_clip_id: Option<String>,
    /// From `list_transitions`.
    type_id: String,
    /// Seconds; the transition's own when none.
    duration: Option<f64>,
}

#[derive(Deserialize, JsonSchema)]
pub struct Cut {
    left_clip_id: String,
    right_clip_id: String,
}

#[derive(Deserialize, JsonSchema)]
pub struct Seek {
    time: f64,
}

#[derive(Deserialize, JsonSchema)]
pub struct RunCommand {
    /// A name from `list_commands` ("Split", "Freeze", "Reverse"...). Most act on the selection or the playhead.
    name: String,
}

#[derive(Deserialize, JsonSchema)]
pub struct RenderFrame {
    /// When; the playhead when none.
    time: Option<f64>,
    /// The picture's longer side at most, in pixels (default 720).
    max_side: Option<u32>,
}

#[derive(Deserialize, JsonSchema)]
pub struct Export {
    /// The MP4 to write (absolute path).
    path: PathBuf,
    /// The shorter side in pixels (1080 for 1080x1920); the timeline's own size when none.
    short_side: Option<u32>,
    /// Quality, 0 (best) to 51; Preferences' when none.
    crf: Option<u32>,
    /// Export only from here...
    start: Option<f64>,
    /// ...to here.
    end: Option<f64>,
}

// ---- tools

#[tool_router]
impl FigServer {
    /// What Fig shows: the open project, the playhead, whether it's playing, unsaved changes, undo, the selection.
    #[tool]
    async fn status(&self) -> Reply {
        self.call(|api| api.status()).await
    }

    /// The user's projects.
    #[tool]
    async fn list_projects(&self) -> Reply {
        self.call(|api| api.list_projects()).await
    }

    /// Opens a project in the editor (saving the one open first) and returns when it shows.
    #[tool]
    async fn open_project(&self, Parameters(p): Parameters<ProjectId>) -> Reply {
        self.call(move |api| api.open_project(&p.id)).await
    }

    /// Makes a new project and opens it; returns its id.
    #[tool]
    async fn create_project(&self, Parameters(p): Parameters<NewProject>) -> Reply {
        self.call(move |api| api.create_project(&p.name)).await
    }

    /// Saves the open project now.
    #[tool]
    async fn save_project(&self) -> Reply {
        self.call(|api| api.save()).await
    }

    /// Saves the open project and goes back to the project list.
    #[tool]
    async fn close_project(&self) -> Reply {
        self.call(|api| api.close_project()).await
    }

    /// The open project's timeline: tracks (top to bottom) with their clips, effects, markers and transitions.
    #[tool]
    async fn get_timeline(&self) -> Reply {
        self.call(|api| api.timeline()).await
    }

    /// The media in the open project.
    #[tool]
    async fn list_media(&self) -> Reply {
        self.call(|api| api.media()).await
    }

    /// A clip's properties (the inspector's settings): keys, ranges, choices, whether they animate, and values now.
    #[tool]
    async fn get_clip_properties(&self, Parameters(p): Parameters<ClipId>) -> Reply {
        self.call(move |api| api.clip_properties(&p.clip_id)).await
    }

    /// The effects there are, with their settings.
    #[tool]
    async fn list_effects(&self) -> Reply {
        self.call(|api| api.effect_catalog()).await
    }

    /// The transitions there are, with their settings and default lengths.
    #[tool]
    async fn list_transitions(&self) -> Reply {
        self.call(|api| api.transition_catalog()).await
    }

    /// The editor's named actions, for `run_command`.
    #[tool]
    async fn list_commands(&self) -> Reply {
        self.call(|api| api.commands()).await
    }

    /// The styles a text clip can start from.
    #[tool]
    async fn list_text_presets(&self) -> Reply {
        self.call(|api| api.text_presets()).await
    }

    /// Imports files into the open project; returns their media ids once they're in.
    #[tool]
    async fn import_media(&self, Parameters(p): Parameters<Import>) -> Reply {
        self.call(move |api| api.import_media(p.paths)).await
    }

    /// Puts media on the timeline; returns the new clip's id (a video with sound gets a linked audio clip too).
    #[tool]
    async fn add_clip(&self, Parameters(p): Parameters<AddClip>) -> Reply {
        self.call(move |api| api.add_clip(&p.media_id, p.track_id.as_deref(), p.start)).await
    }

    /// Adds a text clip; returns its id.
    #[tool]
    async fn add_text(&self, Parameters(p): Parameters<AddText>) -> Reply {
        self.call(move |api| api.add_text(&p.text, p.preset.as_deref(), p.track_id.as_deref(), p.start, p.duration))
            .await
    }

    /// Moves a clip to a new start, and onto another track when given.
    #[tool]
    async fn move_clip(&self, Parameters(p): Parameters<MoveClip>) -> Reply {
        self.call(move |api| api.move_clip(&p.clip_id, p.start, p.track_id.as_deref())).await
    }

    /// Moves a clip's edges, showing more or less of its source.
    #[tool]
    async fn trim_clip(&self, Parameters(p): Parameters<TrimClip>) -> Reply {
        self.call(move |api| api.trim_clip(&p.clip_id, p.start, p.end)).await
    }

    /// Cuts a clip in two; returns both halves' ids.
    #[tool]
    async fn split_clip(&self, Parameters(p): Parameters<SplitClip>) -> Reply {
        self.call(move |api| api.split_clip(&p.clip_id, p.at)).await
    }

    /// Deletes clips, leaving gaps unless `ripple`.
    #[tool]
    async fn delete_clips(&self, Parameters(p): Parameters<DeleteClips>) -> Reply {
        self.call(move |api| api.delete_clips(&p.clip_ids, p.ripple)).await
    }

    /// Turns clips on or off (an off clip stays on the timeline but isn't seen or heard).
    #[tool]
    async fn set_clips_enabled(&self, Parameters(p): Parameters<EnableClips>) -> Reply {
        self.call(move |api| api.set_clip_enabled(&p.clip_ids, p.enabled)).await
    }

    /// Sets a property on clips. On an animated property this sets its key at the playhead.
    #[tool]
    async fn set_property(&self, Parameters(p): Parameters<SetProperty>) -> Reply {
        self.call(move |api| api.set_property(&p.clip_ids, &p.key, value(p.value)?)).await
    }

    /// Sets a property's key at a time, animating it.
    #[tool]
    async fn set_keyframe(&self, Parameters(p): Parameters<SetKeyframe>) -> Reply {
        self.call(move |api| api.set_keyframe(&p.clip_id, &p.key, p.time, p.value)).await
    }

    /// Removes a property's key at a time, or all of them.
    #[tool]
    async fn remove_keyframes(&self, Parameters(p): Parameters<RemoveKeyframes>) -> Reply {
        self.call(move |api| api.remove_keyframes(&p.clip_id, &p.key, p.time)).await
    }

    /// Changes what text clips say.
    #[tool]
    async fn set_text(&self, Parameters(p): Parameters<SetText>) -> Reply {
        self.call(move |api| api.set_text(&p.clip_ids, &p.text)).await
    }

    /// Selects clips (what selection commands act on); an empty list clears the selection.
    #[tool]
    async fn select_clips(&self, Parameters(p): Parameters<ClipIds>) -> Reply {
        self.call(move |api| api.select(&p.clip_ids)).await
    }

    /// Adds a track; returns its id.
    #[tool]
    async fn add_track(&self, Parameters(p): Parameters<AddTrack>) -> Reply {
        let kind = match p.kind {
            Kind::Video => TrackKind::Video,
            Kind::Audio => TrackKind::Audio,
        };
        self.call(move |api| api.add_track(kind)).await
    }

    /// Removes a track and its clips.
    #[tool]
    async fn remove_track(&self, Parameters(p): Parameters<TrackId>) -> Reply {
        self.call(move |api| api.remove_track(&p.track_id)).await
    }

    /// Renames, hides, mutes or reorders a track.
    #[tool]
    async fn update_track(&self, Parameters(p): Parameters<UpdateTrack>) -> Reply {
        let change = TrackChange { name: p.name, visible: p.visible, muted: p.muted, index: p.index };
        self.call(move |api| api.change_track(&p.track_id, change)).await
    }

    /// Adds a marker on the timeline, a track or a clip; returns its id.
    #[tool]
    async fn add_marker(&self, Parameters(p): Parameters<AddMarker>) -> Reply {
        self.call(move |api| {
            api.add_marker(
                p.owner_id.as_deref(),
                p.time,
                p.duration.unwrap_or(0.0),
                p.name.as_deref().unwrap_or(""),
                p.color.as_deref(),
            )
        })
        .await
    }

    /// Deletes markers.
    #[tool]
    async fn delete_markers(&self, Parameters(p): Parameters<MarkerIds>) -> Reply {
        self.call(move |api| api.delete_markers(&p.marker_ids)).await
    }

    /// Adds an effect to a clip, or to a track for a stretch of time whatever clips play there; returns its id.
    #[tool]
    async fn add_effect(&self, Parameters(p): Parameters<AddEffect>) -> Reply {
        self.call(move |api| api.add_effect(&p.owner_id, &p.type_id, span(p.start, p.duration)?)).await
    }

    /// Moves or resizes an effect's stretch of time.
    #[tool]
    async fn set_effect_range(&self, Parameters(p): Parameters<SetEffectRange>) -> Reply {
        self.call(move |api| {
            api.set_effect_range(&p.owner_id, &p.effect_id, span(p.start, p.duration)?, p.move_keyframes)
        })
        .await
    }

    /// Removes an effect.
    #[tool]
    async fn remove_effect(&self, Parameters(p): Parameters<EffectRef>) -> Reply {
        self.call(move |api| api.remove_effect(&p.owner_id, &p.effect_id)).await
    }

    /// Sets one of an effect's settings.
    #[tool]
    async fn set_effect_param(&self, Parameters(p): Parameters<SetEffectParam>) -> Reply {
        self.call(move |api| api.set_effect_param(&p.owner_id, &p.effect_id, &p.key, value(p.value)?)).await
    }

    /// Keys an effect's settings at a time, animating them. Keys at several times make a move: a Transform keyed
    /// at zoom 1 then at zoom 2 centred on a point pushes the camera in there. Settings left out stay as they are.
    #[tool]
    async fn set_effect_keyframe(&self, Parameters(p): Parameters<SetEffectKeyframe>) -> Reply {
        let values: Vec<(String, f64)> = p.values.into_iter().collect();
        self.call(move |api| api.set_effect_keyframe(&p.owner_id, &p.effect_id, p.time, &values, p.easing.as_deref()))
            .await
    }

    /// Puts a transition on the cut between two touching clips.
    #[tool]
    async fn add_transition(&self, Parameters(p): Parameters<AddTransition>) -> Reply {
        self.call(move |api| api.add_transition(&p.left_clip_id, p.right_clip_id.as_deref(), &p.type_id, p.duration))
            .await
    }

    /// Removes the transition on a cut.
    #[tool]
    async fn remove_transition(&self, Parameters(p): Parameters<Cut>) -> Reply {
        self.call(move |api| api.remove_transition(&p.left_clip_id, &p.right_clip_id)).await
    }

    /// Undoes the last edit (the user's or a tool's); says whether there was one.
    #[tool]
    async fn undo(&self) -> Reply {
        self.call(|api| api.undo()).await
    }

    /// Redoes the last undone edit; says whether there was one.
    #[tool]
    async fn redo(&self) -> Reply {
        self.call(|api| api.redo()).await
    }

    /// Moves the playhead.
    #[tool]
    async fn seek(&self, Parameters(p): Parameters<Seek>) -> Reply {
        self.call(move |api| api.seek(p.time)).await
    }

    /// Starts playback from the playhead.
    #[tool]
    async fn play(&self) -> Reply {
        self.call(|api| api.play()).await
    }

    /// Stops playback.
    #[tool]
    async fn pause(&self) -> Reply {
        self.call(|api| api.pause()).await
    }

    /// Runs one of the editor's named actions as its shortcut would.
    #[tool]
    async fn run_command(&self, Parameters(p): Parameters<RunCommand>) -> Reply {
        self.call(move |api| api.run_command(&p.name)).await
    }

    /// Renders a frame of the timeline as a PNG, to see what an edit looks like.
    #[tool]
    async fn render_frame(&self, Parameters(p): Parameters<RenderFrame>) -> Reply {
        let api = self.api.clone();
        let image = tokio::task::spawn_blocking(move || api.render_frame(p.time, p.max_side.unwrap_or(720)))
            .await
            .map_err(|e| ErrorData::internal_error(e.to_string(), None))?;
        Ok(match image {
            Ok(img) => CallToolResult::success(vec![
                ContentBlock::image(base64::engine::general_purpose::STANDARD.encode(&img.png), "image/png"),
                ContentBlock::text(format!("{}x{}", img.width, img.height)),
            ]),
            Err(e) => CallToolResult::error(vec![ContentBlock::text(e.0)]),
        })
    }

    /// Starts exporting the timeline to an MP4 in the background; follow it with `list_jobs`.
    #[tool]
    async fn export(&self, Parameters(p): Parameters<Export>) -> Reply {
        let range = match (p.start, p.end) {
            (None, None) => None,
            (start, end) => Some((start.unwrap_or(0.0), end.unwrap_or(f64::MAX))),
        };
        let options = ExportOptions { short_side: p.short_side, crf: p.crf, range };
        self.call(move |api| api.export(p.path, options)).await
    }

    /// Exports running or finished: progress from 0 to 1, and any error.
    #[tool]
    async fn list_jobs(&self) -> Reply {
        self.call(|api| api.jobs()).await
    }

    /// Stops exports that are running; returns how many.
    #[tool]
    async fn cancel_export(&self, Parameters(p): Parameters<CancelExport>) -> Reply {
        self.call(move |api| api.cancel_export(p.path.as_deref())).await
    }
}

#[tool_handler(router = self.tool_router)]
impl ServerHandler for FigServer {
    fn get_info(&self) -> ServerConfig {
        ServerConfig::new(ServerCapabilities::builder().enable_tools().build())
            .with_server_info(Implementation::new("fig", env!("CARGO_PKG_VERSION")))
            .with_instructions(INSTRUCTIONS)
    }
}
