//! The editor's state: the timeline document, the viewport, playback, preview, panels and save
//! status. The Rust counterpart of `EditorViewModel` (split by area, like the C# partials).
//!
//! Mutations go through [`fig_edit::TimelineEditor`] — it owns undo, selection, snapping and live
//! edits — so views and gestures never modify the document behind its back.

use fig_edit::TimelineEditor;
use fig_project::Project;
use std::path::PathBuf;
use fig_timeline::{AudioBody, Clip, Marker, MediaAsset, MediaKind, TextBody, Timeline, Track, TrackKind, VideoBody};

use crate::inspector::InspectorState;
use crate::library::LibraryState;
use crate::playback::TransportState;
use crate::preview::PreviewState;
use crate::timeline::TimelineState;

/// The active timeline tool (C# `EditorViewModel` select/blade).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Tool {
    #[default]
    Select,
    Blade,
}

pub struct EditorState {
    pub editor: TimelineEditor,
    /// The project this editor is a view of (`ProjectManager`'s current project).
    pub project: Project,
    pub project_name: String,
    pub project_path: Option<PathBuf>,
    /// Which of the project's timelines this editor is showing (multi-timeline projects keep the rest).
    pub timeline_index: usize,
    pub playhead: f64,
    pub timeline: TimelineState,
    pub transport: TransportState,
    pub preview: PreviewState,
    pub library: LibraryState,
    pub inspector: InspectorState,
    pub save: SaveState,
    pub graph_open: bool,
    /// `WorkspaceLayout.IsLeftCollapsed` / `IsRightCollapsed`.
    pub left_collapsed: bool,
    pub right_collapsed: bool,
    /// Panel fractions, as `WorkspaceLayout`: sides 0.20/0.22, timeline 0.44.
    pub left_frac: f32,
    pub right_frac: f32,
    pub timeline_frac: f32,
    /// Where a panel splitter drag started (fractions + workspace size).
    pub split_origin: Option<(f32, f32, f32, f32, f32)>,
    pub tool: Tool,
    /// The open timeline context menu, if any.
    pub context_menu: Option<crate::timeline_menu::ContextMenu>,
    /// The graph editor's canvas state.
    pub graph: crate::graph::GraphState,
    /// Bumped when state the panels show has changed outside the editor's own revision (playhead,
    /// transport, preview).
    pub ui_revision: u64,
}

impl EditorState {
    /// The demo sequence: 3 tracks, 4 video clips, 2 titles, 1 audio clip, a marker — the same
    /// shape as the Avalonia reference project, so screenshots compare like for like.
    pub fn demo() -> EditorState {
        let doc = demo_timeline();
        let mut project = Project::default();
        project.name = "Golden hour — cut 03".into();
        project.timelines = vec![doc.clone()];
        EditorState {
            editor: TimelineEditor::new(doc),
            project,
            project_name: "Golden hour — cut 03".into(),
            project_path: None,
            timeline_index: 0,
            playhead: 0.0,
            timeline: TimelineState::default(),
            transport: TransportState::default(),
            preview: PreviewState::new(),
            library: LibraryState::new(),
            inspector: InspectorState::new(),
            save: SaveState::default(),
            graph_open: false,
            left_collapsed: false,
            right_collapsed: false,
            left_frac: 0.20,
            right_frac: 0.22,
            timeline_frac: 0.44,
            split_origin: None,
            tool: Tool::Select,
            context_menu: None,
            graph: crate::graph::GraphState::default(),
            ui_revision: 0,
        }
    }

    /// Opens a project loaded from disk (`ProjectStore::load_project`).
    pub fn open(project: Project, folder: PathBuf) -> EditorState {
        let doc = project.timelines.first().cloned().unwrap_or_default();
        let name = if project.name.is_empty() { "Untitled".to_string() } else { project.name.clone() };
        let mut state = EditorState {
            editor: TimelineEditor::new(doc),
            project,
            project_name: name,
            project_path: None,
            timeline_index: 0,
            playhead: 0.0,
            timeline: TimelineState::default(),
            transport: TransportState::default(),
            preview: PreviewState::new(),
            library: LibraryState::new(),
            inspector: InspectorState::new(),
            save: SaveState::Saved,
            graph_open: false,
            left_collapsed: false,
            right_collapsed: false,
            left_frac: 0.20,
            right_frac: 0.22,
            timeline_frac: 0.44,
            split_origin: None,
            tool: Tool::Select,
            context_menu: None,
            graph: crate::graph::GraphState::default(),
            ui_revision: 0,
        };
        let doc = state.doc().clone();
        state.preview.build_thumbnails(&doc);
        state.preview.load_for_doc(&doc, state.playhead);
        state
    }

    /// A demo project with a media library, for the library and inspector snapshots.
    pub fn demo_with_media() -> EditorState {
        let mut state = EditorState::demo();
        state.project.media = demo_media();
        let media = state.project.media.clone();
        state.preview.build_media_thumbnails(&media);
        let doc = state.doc().clone();
        state.preview.load_for_doc(&doc, state.playhead);
        state
    }

    /// `EditorViewModel.SplitAtPlayhead`: selection wins; with nothing selected it splits the
    /// first track's clip under the playhead (never every track).
    pub fn split_at_playhead(&mut self) -> Vec<String> {
        let at = self.playhead;
        if !self.editor.selection.is_empty() {
            return self.editor.split_at_playhead(at);
        }
        let Some(track_id) = self.doc().tracks.first().map(|track| track.id.clone()) else { return Vec::new() };
        self.editor.split_at_playhead_on(&track_id, at)
    }

    /// Copies the editor's live document back into the project before a save, preserving every
    /// other timeline the project has (multi-timeline projects are not clobbered by the editor).
    pub fn commit_to_project(&mut self) {
        self.project.name = self.project_name.clone();
        while self.project.timelines.len() <= self.timeline_index {
            self.project.timelines.push(Timeline::default());
        }
        self.project.timelines[self.timeline_index] = self.doc().clone();
    }

    /// Applies the user's `preferences.json` and `workspace.json` to a freshly opened editor:
    /// undo depth, snapping, playback defaults and the panel layout they last used.
    pub fn apply_preferences(&mut self, prefs: &crate::services::Preferences, workspace: &crate::services::WorkspaceLayout) {
        let doc = self.doc().clone();
        self.editor = TimelineEditor::with_undo_steps(doc, prefs.editing.undo_steps.max(10) as usize);
        self.editor.snap.magnetic = prefs.editing.snap;
        self.editor.snap.window_sec = f64::from(prefs.editing.snap_reach_px) / f64::from(self.timeline.pps.max(1.0));
        self.transport.loop_playback = prefs.playback.loop_playback;
        self.transport.audio_scrub = prefs.playback.audio_scrub;
        self.left_frac = workspace.left as f32;
        self.right_frac = workspace.right as f32;
        self.timeline_frac = workspace.timeline as f32;
        self.left_collapsed = workspace.left_collapsed;
        self.right_collapsed = workspace.right_collapsed;

        let folder = self.project_path.clone();
        let media = self.project.media.clone();
        let doc = self.doc().clone();
        self.preview.build_thumbnails_for_media(&doc, &media, folder.as_deref());
        self.preview.load_for_doc_with_media(&doc, &media, folder.as_deref(), self.playhead);
    }

    pub fn doc(&self) -> &Timeline {
        self.editor.doc()
    }

    pub fn duration_sec(&self) -> f64 {
        self.doc().duration_sec()
    }

    pub fn timecode(&self, sec: f64) -> String {
        format_timecode(sec)
    }

    pub fn playhead_timecode(&self) -> String {
        self.timecode(self.playhead)
    }

    /// `00:00:00:00` — hours:minutes:seconds:frames at the project rate (30 fps for the demo).
    pub fn project_rate(&self) -> f64 {
        let rate = self.doc().rate;
        rate.num() as f64 / rate.den() as f64
    }
}

pub fn format_timecode(sec: f64) -> String {
    let rate = 30.0;
    let total = (sec.max(0.0) * rate).round() as i64;
    let frames = total % 30;
    let secs = total / 30;
    format!("{:02}:{:02}:{:02}:{:02}", secs / 3600, (secs / 60) % 60, secs % 60, frames)
}

/// What the save indicator says (`EditorViewModel.SaveStatusText`).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum SaveState {
    #[default]
    Saved,
    Dirty,
    Saving,
    Failed,
}

impl SaveState {
    pub fn status_text(self) -> &'static str {
        match self {
            SaveState::Saved => "All changes saved",
            SaveState::Dirty => "Saving changes…",
            SaveState::Saving => "Saving changes…",
            SaveState::Failed => "Couldn't save · will retry on your next change",
        }
    }
}

fn video_clip(start: f64, dur: f64, source: &str) -> Clip {
    Clip::new(VideoBody { source_id: source.into(), ..VideoBody::default() }).at(start, dur)
}

fn text_clip(text: &str, start: f64, dur: f64) -> Clip {
    Clip::new(TextBody { text: text.into(), ..TextBody::default() }).at(start, dur)
}

fn audio_clip(start: f64, dur: f64, source: &str) -> Clip {
    Clip::new(AudioBody { source_id: source.into(), ..AudioBody::default() }).at(start, dur)
}

pub fn demo_timeline() -> Timeline {
    let mut doc = Timeline::default();

    let mut v1 = Track::new(TrackKind::Video);
    v1.name = Some("V1".into());
    v1.clips = vec![
        video_clip(0.0, 3.6, "media-01"),
        video_clip(3.6, 4.6, "media-02"),
        video_clip(8.2, 4.9, "media-03"),
        video_clip(13.1, 1.8, "media-04"),
    ];

    let mut v2 = Track::new(TrackKind::Video);
    v2.name = Some("V2".into());
    v2.clips = vec![text_clip("Sweet Apple Pie", 0.95, 3.55), text_clip("a montage", 9.4, 3.6)];

    let mut a1 = Track::new(TrackKind::Audio);
    a1.name = Some("A1".into());
    a1.clips = vec![audio_clip(0.0, 14.9, "media-bed")];

    doc.tracks = vec![v1, v2, a1];
    doc.markers = vec![Marker { name: "beat".into(), start_sec: 6.0, ..Marker::default() }];
    doc
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn demo_tracks_get_the_csharp_auto_labels() {
        let editor = EditorState::demo();
        let labels: Vec<_> = editor.doc().tracks.iter().map(|t| editor.editor.track_label(&t.id)).collect();
        assert_eq!(labels, vec!["V1", "V2", "A1"]);
    }
}

/// The library of the demo project: the generated fixture clips, so the media grid and "add to
/// timeline" are usable without importing files.
pub fn demo_media() -> Vec<MediaAsset> {
    let fixtures = crate::preview::PreviewState::fixture_dir();
    let mut out = Vec::new();
    for (id, name, duration, w, h) in [
        ("media-01", "media-01.mp4", 8.0, 1920, 1080),
        ("media-02", "media-02.mp4", 6.0, 1920, 1080),
        ("media-03", "media-03.mp4", 6.0, 1920, 1080),
        ("media-04", "media-04.mp4", 3.0, 1920, 1080),
    ] {
        out.push(MediaAsset {
            id: id.into(),
            kind: MediaKind::Video,
            url: fixtures.join(name).to_string_lossy().into_owned(),
            duration_sec: duration,
            width: w,
            height: h,
            has_audio: id == "media-01",
            ..MediaAsset::default()
        });
    }
    out
}
