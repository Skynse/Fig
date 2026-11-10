//! An open project: the timeline being edited, where the playhead is, and the state of the panels around it.
//!
//! Every change to the timeline goes through `fig_edit::TimelineEditor` (`ed`), which owns undo, selection and
//! snapping. The project is saved a moment after each change, as the C# build does.

pub mod beats;
pub mod graph;
pub mod inspector;
pub mod library;
pub mod media;
pub mod overlays;
pub mod preview;
pub mod view;

use std::path::PathBuf;
use std::time::Instant;

use fig_edit::TimelineEditor;
use fig_project::Project;
use fig_timeline::{MediaAsset, Timeline, time};

use crate::app::AppState;
use crate::settings::{Preferences, WorkspaceLayout};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Tool {
    #[default]
    Select,
    Blade,
}

/// The panel layout (`WorkspaceLayout`): side fractions, timeline height, collapsed sides.
#[derive(Clone, Copy, Debug)]
pub struct Panels {
    pub left: f32,
    pub right: f32,
    pub timeline: f32,
    pub left_collapsed: bool,
    pub right_collapsed: bool,
    /// The fractions when a splitter drag began.
    pub drag0: Option<(f32, f32, f32)>,
}

impl Panels {
    pub fn from_workspace(w: &WorkspaceLayout) -> Panels {
        Panels {
            left: (w.left as f32).clamp(0.12, 0.40),
            right: (w.right as f32).clamp(0.12, 0.40),
            timeline: (w.timeline as f32).clamp(0.20, 0.75),
            left_collapsed: w.left_collapsed,
            right_collapsed: w.right_collapsed,
            drag0: None,
        }
    }

    pub fn defaults() -> Panels {
        Panels { left: 0.20, right: 0.22, timeline: 0.44, left_collapsed: false, right_collapsed: false, drag0: None }
    }
}

pub struct Editor {
    /// The lanes as last laid out, and the (revision, media revision, scale, folds) they were laid out for.
    lanes: std::cell::RefCell<Option<((u64, u64, u32, u64), std::rc::Rc<Vec<crate::timeline::geometry::Lane>>)>>,
    /// The project's id in the store, and its folder.
    pub id: String,
    pub folder: PathBuf,
    pub project: Project,
    /// The timeline being edited. The project's copy of it is refreshed on save.
    pub ed: TimelineEditor,
    pub timeline_index: usize,
    pub playhead: f64,
    /// J/K/L: 0 when not shuttling, else ±1, ±2, ±4, ±8.
    pub shuttle: i32,
    pub loop_playback: bool,
    /// Playing a stretch (Preview movement): stop here.
    pub play_until: Option<f64>,
    pub audio_scrub: bool,
    pub volume: f32,
    pub muted: bool,
    pub tool: Tool,
    /// The beat-marker session under the Beats button.
    pub beats: crate::editor::beats::BeatSession,
    pub tl: crate::timeline::TimelineView,
    pub panels: Panels,
    pub library: crate::editor::library::LibraryState,
    pub inspector: crate::editor::inspector::InspectorState,
    pub graph: Option<crate::editor::graph::GraphEditor>,
    /// The project's own graph effects, registered in the effect catalog while it is open.
    pub graphs: fig_fx::graph::GraphEffectLibrary,
    /// Bumped when the media library changes (imports, filmstrips, proxies), so playback re-reads it.
    pub media_rev: u64,
    pub save: SaveState,
    /// What the status bar says on the right, briefly.
    pub hint: Option<(String, Instant)>,
    /// The preview's transform handles are shown.
    pub show_overlay: bool,
    /// Where each timeline's view was, when switching between them.
    pub views: Vec<(String, f32, f64)>,
    /// The playhead's x on the timeline and the time readout, moved every frame while playing without rebuilding
    /// the view.
    pub live_playhead: rune::Live<[f32; 2]>,
    pub live_time: rune::LiveText,
}

pub struct SaveState {
    pub saved: (u64, u64),
    pub changed_at: Option<Instant>,
    pub saving: bool,
    pub failed: bool,
}

impl SaveState {
    pub fn text(&self, dirty: bool) -> &'static str {
        if self.failed {
            "Couldn't save · will retry on your next change"
        } else if self.saving || dirty {
            "Saving changes…"
        } else {
            "All changes saved"
        }
    }
}

impl Editor {
    pub fn open(id: String, folder: PathBuf, mut project: Project, prefs: &Preferences, workspace: &WorkspaceLayout) -> Editor {
        if project.timelines.is_empty() {
            project.timelines.push(Timeline::default());
        }
        for asset in &mut project.media {
            asset.in_project = std::path::Path::new(&asset.url).starts_with(&folder);
            asset.offline = !asset.url.is_empty() && !std::path::Path::new(&asset.url).exists();
        }
        let doc = project.timelines[0].clone();
        let graphs = fig_fx::graph::GraphEffectLibrary::attach(project.effect_graphs.clone());
        let mut ed = TimelineEditor::with_undo_steps(doc, prefs.editing.undo_steps.max(10) as usize);
        ed.snap.magnetic = prefs.editing.snap;
        Editor {
            id,
            folder,
            project,
            ed,
            timeline_index: 0,
            playhead: 0.0,
            shuttle: 0,
            loop_playback: prefs.playback.loop_playback,
            play_until: None,
            audio_scrub: prefs.playback.audio_scrub,
            volume: 1.0,
            muted: false,
            tool: Tool::Select,
            beats: crate::editor::beats::BeatSession::new(),
            tl: crate::timeline::TimelineView::new(prefs),
            panels: Panels::from_workspace(workspace),
            library: crate::editor::library::LibraryState::new(),
            inspector: crate::editor::inspector::InspectorState::default(),
            graph: None,
            graphs,
            media_rev: 1,
            save: SaveState { saved: (0, 1), changed_at: None, saving: false, failed: false },
            hint: None,
            lanes: std::cell::RefCell::new(None),
            show_overlay: true,
            views: Vec::new(),
            live_playhead: rune::Live::new([0.0, 0.0]),
            live_time: rune::LiveText::new("00:00:00:00"),
        }
    }

    pub fn doc(&self) -> &Timeline {
        self.ed.doc()
    }

    pub fn media(&self) -> &[MediaAsset] {
        &self.project.media
    }

    /// Every lane, laid out once per change (a long timeline has thousands of clips to measure racks for).
    pub fn lanes(&self) -> std::rc::Rc<Vec<crate::timeline::geometry::Lane>> {
        let key = (self.ed.revision(), self.media_rev, self.tl.scale.to_bits(), self.tl.folds.generation);
        if let Some((k, lanes)) = self.lanes.borrow().as_ref()
            && *k == key
        {
            return lanes.clone();
        }
        let lanes = std::rc::Rc::new(crate::timeline::geometry::lanes(self.doc(), self.media(), self.tl.scale, &self.tl.folds));
        *self.lanes.borrow_mut() = Some((key, lanes.clone()));
        lanes
    }

    pub fn find_media(&self, id: &str) -> Option<&MediaAsset> {
        self.project.media.iter().find(|m| m.id == id)
    }

    /// What playback must re-read when it changes.
    pub fn revision(&self) -> u64 {
        self.ed.revision().wrapping_mul(1_000_003).wrapping_add(self.media_rev)
    }

    pub fn is_dirty(&self) -> bool {
        self.save.saved != (self.ed.revision(), self.media_rev)
    }

    pub fn fps(&self) -> f64 {
        self.doc().rate.fps().max(1.0)
    }

    pub fn duration(&self) -> f64 {
        self.doc().duration_sec()
    }

    pub fn timecode(&self, sec: f64) -> String {
        time::format_timecode(sec.max(0.0), self.doc().rate)
    }

    pub fn snap_frame(&self, sec: f64) -> f64 {
        time::snap_to_frame(sec.max(0.0), self.doc().rate)
    }

    /// Moves the drawn playhead and the time readout to the playhead.
    pub fn publish_playhead(&self) {
        self.live_playhead.set([self.tl.x_of(self.playhead), 0.0]);
        self.live_time.set(self.timecode(self.playhead));
    }

    /// A short message for the status bar's right side.
    pub fn say(&mut self, text: impl Into<String>) {
        self.hint = Some((text.into(), Instant::now()));
    }

    /// Writes the editor's timeline back into the project, keeping its other timelines.
    pub fn commit_to_project(&mut self) {
        while self.project.timelines.len() <= self.timeline_index {
            self.project.timelines.push(Timeline::default());
        }
        self.project.timelines[self.timeline_index] = self.doc().clone();
        self.project.effect_graphs = self.graphs.graphs().to_vec();
    }

    /// Switches to another of the project's timelines (keeping each one's zoom and scroll).
    pub fn switch_timeline(&mut self, index: usize, prefs: &Preferences) {
        if index == self.timeline_index || index >= self.project.timelines.len() {
            return;
        }
        self.commit_to_project();
        let here = self.doc().id.clone();
        self.views.retain(|(id, _, _)| *id != here);
        self.views.push((here, self.tl.pps, self.tl.scroll));
        self.timeline_index = index;
        let doc = self.project.timelines[index].clone();
        let mut ed = TimelineEditor::with_undo_steps(doc, prefs.editing.undo_steps.max(10) as usize);
        ed.snap.magnetic = self.ed.snap.magnetic;
        self.ed = ed;
        self.media_rev += 1;
        self.playhead = 0.0;
        if let Some((_, pps, scroll)) = self.views.iter().find(|(id, _, _)| *id == self.ed.doc().id) {
            self.tl.pps = *pps;
            self.tl.scroll = *scroll;
        } else {
            self.tl.scroll = 0.0;
        }
    }

    /// Replaces a media item after background work (a filmstrip, peaks, a proxy) finished with it.
    pub fn update_media(&mut self, asset: MediaAsset) {
        if let Some(slot) = self.project.media.iter_mut().find(|m| m.id == asset.id) {
            *slot = asset;
            self.media_rev += 1;
            self.ed.notify_media_changed();
        }
    }
}

/// Moves the playhead (stopped, or restarting playback from there while playing).
pub fn seek(app: &mut AppState, sec: f64, scrubbing: bool) {
    let Some(e) = app.editor.as_mut() else { return };
    let end = e.duration().max(0.0);
    let t = sec.clamp(0.0, end.max(0.0));
    e.playhead = t;
    e.publish_playhead();
    sync_playback(app);
    let e = app.editor.as_ref().unwrap();
    app.playback.seek(t, scrubbing, e.audio_scrub);
}

/// Gives playback the current timeline, when it changed.
pub fn sync_playback(app: &mut AppState) {
    if let Some(e) = app.editor.as_ref() {
        app.playback.update(e.doc(), &e.project.media, e.revision());
        app.playback.set_volume(e.volume, e.muted);
    }
}

pub fn play(app: &mut AppState, rate: f64) {
    let Some(e) = app.editor.as_mut() else { return };
    let end = e.duration();
    if end <= 0.0 {
        return;
    }
    // playing from the very end starts over
    if rate > 0.0 && e.playhead >= end - 1.0 / e.fps() {
        e.playhead = 0.0;
    }
    let from = e.playhead;
    e.tl.panned = false;
    sync_playback(app);
    app.playback.play(from, rate);
    // say once when there is no sound to play through
    static TOLD: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);
    if !app.playback.has_sound() && !TOLD.swap(true, std::sync::atomic::Ordering::Relaxed) {
        crate::ui::toasts::show(app, "No audio output device is available. Playing without sound.");
    }
}

pub fn pause(app: &mut AppState) {
    let at = app.playback.pause();
    if let Some(e) = app.editor.as_mut() {
        e.play_until = None;
        e.shuttle = 0;
        e.playhead = e.snap_frame(at.clamp(0.0, e.duration()));
        e.publish_playhead();
        let t = e.playhead;
        app.playback.seek(t, false, false);
    }
}

pub fn toggle_play(app: &mut AppState) {
    if app.playback.is_playing() {
        pause(app);
    } else {
        if let Some(e) = app.editor.as_mut() {
            e.shuttle = 0;
        }
        play(app, 1.0);
    }
}

/// Plays a stretch of the timeline once, then stops at its end.
pub fn play_range(app: &mut AppState, from: f64, to: f64) {
    if app.playback.is_playing() {
        pause(app);
    }
    let Some(e) = app.editor.as_mut() else { return };
    e.playhead = from.max(0.0);
    e.shuttle = 0;
    play(app, 1.0);
    if let Some(e) = app.editor.as_mut() {
        e.play_until = Some(to);
    }
}

/// L / J: play forward or back, faster with each press (1×, 2×, 4×, 8×); the other way slows down first.
pub fn shuttle(app: &mut AppState, forward: bool) {
    let Some(e) = app.editor.as_mut() else { return };
    let s = e.shuttle;
    let next = if forward {
        match s {
            s if s >= 8 => 8,
            s if s >= 1 => s * 2,
            0 => 1,
            -1 => 0,
            s => s / 2,
        }
    } else {
        match s {
            s if s <= -8 => -8,
            s if s <= -1 => s * 2,
            0 => -1,
            1 => 0,
            s => s / 2,
        }
    };
    e.shuttle = next;
    if next == 0 {
        pause(app);
    } else {
        let from = app.playback.position();
        if let Some(e) = app.editor.as_mut() {
            e.playhead = if app.playback.is_playing() { from } else { e.playhead };
        }
        play(app, f64::from(next));
    }
}

/// Once a frame while playing: follows the clock, stops at the end (or loops), keeps the playhead on screen.
pub fn tick(app: &mut AppState) -> bool {
    let playing = app.playback.is_playing();
    let follow = app.preferences.playback.follow_playhead;
    let Some(e) = app.editor.as_mut() else { return false };
    if !playing {
        return false;
    }
    let end = e.play_until.map_or(e.duration(), |u| u.min(e.duration()));
    let mut t = app.playback.position();
    let forward = app.playback.rate() > 0.0;
    if (forward && t >= end) || (!forward && t <= 0.0) {
        if e.loop_playback && forward && end > 0.0 && e.play_until.is_none() {
            e.playhead = 0.0;
            app.playback.seek(0.0, false, false);
            return true;
        }
        t = t.clamp(0.0, end);
        e.playhead = t;
        pause(app);
        return true;
    }
    e.playhead = t.clamp(0.0, end.max(0.0));
    let scroll = e.tl.scroll;
    if follow && !e.tl.panned {
        e.tl.keep_in_view(e.playhead, forward);
    }
    e.publish_playhead();
    // only a page turn needs the view rebuilt; the playhead itself moves through live values
    e.tl.scroll != scroll
}

/// Saves when there are changes and the autosave delay has passed since the last one.
pub fn autosave(app: &mut AppState) {
    let delay = app.preferences.general.autosave_seconds.max(0.2);
    let Some(e) = app.editor.as_mut() else { return };
    let now = (e.ed.revision(), e.media_rev);
    if e.ed.is_live() || e.save.saving || now == e.save.saved {
        e.save.changed_at = None;
        return;
    }
    let since = *e.save.changed_at.get_or_insert_with(Instant::now);
    if since.elapsed().as_secs_f64() < delay {
        return;
    }
    save_now(app);
}

/// Saves on a background thread; the title bar shows the result.
pub fn save_now(app: &mut AppState) {
    let store_root = app.store.root().to_path_buf();
    let Some(e) = app.editor.as_mut() else { return };
    e.commit_to_project();
    let mut project = e.project.clone();
    let stamp = (e.ed.revision(), e.media_rev);
    e.save.saving = true;
    e.save.changed_at = None;
    crate::app::spawn(
        move || {
            let store = fig_project::ProjectStore::new(store_root);
            let result = store.save_project(&mut project).map_err(|err| err.to_string());
            (result, project.updated_at)
        },
        move |app, (result, updated)| {
            let Some(e) = app.editor.as_mut() else { return };
            e.save.saving = false;
            match result {
                Ok(()) => {
                    e.save.saved = stamp;
                    e.save.failed = false;
                    e.project.updated_at = updated;
                }
                Err(error) => {
                    e.save.failed = true;
                    crate::ui::toasts::show(app, format!("Couldn't save: {error}"));
                }
            }
        },
    );
}
