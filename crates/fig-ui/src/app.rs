//! The app: routing between Home and the Editor, global overlays (menus, preferences, dialogs,
//! toasts), and the scene registry the snapshot harness drives.
//!
//! This is the Rust counterpart of `AppViewModel` + `MainWindow`: the window has no system
//! decorations, the title bar is drawn by [`crate::shell`], and the current screen fills the rest.

use rune::{Cx, El, Waker, col, el};

use crate::editor_state::EditorState;
use crate::services::{GestureMap, KeyCombo, Preferences, WorkspaceLayout};
use crate::home::HomeState;
use crate::theme::Tokens;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Route {
    Home,
    Editor,
}

/// Which title-bar menu is open (one at a time, as the C# menus).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Menu {
    File,
    Edit,
    View,
    Help,
}

pub struct AppState {
    pub route: Route,
    pub title: String,
    pub home: HomeState,
    pub editor: Option<EditorState>,
    pub menu: Option<Menu>,
    pub preferences_open: bool,
    pub preferences_page: usize,
    pub dialogs: crate::dialogs::DialogState,
    pub shortcuts: crate::shortcuts::Shortcuts,
    pub toasts: crate::toasts::Toasts,
    /// `~/.fig/preferences.json`, loaded and saved in the C# format.
    pub preferences: Preferences,
    /// `~/.fig/config/workspace.json`.
    pub workspace: WorkspaceLayout,
    /// `~/.fig/config/gestures.json`.
    pub gestures: GestureMap,
    /// The user's shortcut overrides from preferences (`Keyboard.Shortcuts`), pre-parsed.
    pub shortcut_overrides: Vec<(String, KeyCombo)>,
    /// The project store the app opens/saves through.
    pub store: fig_project::ProjectStore,
    /// Seconds since start from the last poll.
    pub now: f64,
    /// Where a window-edge grip started dragging (width, height).
    pub resize_origin: Option<(f32, f32)>,
    /// Other threads wake the UI through this (decode/export/save workers).
    pub waker: Option<Waker>,
    /// Modifiers currently held, for drags that copy (Alt) or constrain (Shift).
    pub mods: rune::Mods,
}

impl AppState {
    pub fn new() -> AppState {
        crate::services::ensure_directories();
        let preferences = Preferences::load();
        let workspace = WorkspaceLayout::load();
        let gestures = GestureMap::load();
        let shortcut_overrides = shortcut_overrides(&preferences.keyboard.shortcuts);
        let store = crate::services::project_store();
        let mut home = HomeState::from_store(&store);
        if home.projects.is_empty() {
            // first run: the Home empty state is correct; the editor still shows the demo
            home.projects.clear();
        }
        AppState {
            // the C# app opens on Home; a project is opened by the user (or from the store list)
            route: Route::Home,
            title: "fig".into(),
            home,
            editor: None,
            menu: None,
            preferences_open: false,
            preferences_page: 0,
            dialogs: crate::dialogs::DialogState::new(),
            shortcuts: crate::shortcuts::Shortcuts::new(),
            toasts: crate::toasts::Toasts::new(),
            preferences,
            workspace,
            gestures,
            shortcut_overrides,
            store: crate::services::project_store(),
            now: 0.0,
            resize_origin: None,
            waker: None,
            mods: rune::Mods::default(),
        }
    }

    /// The editor, if a project is open. Snapshot scenes call [`editor`](Self::editor), which
    /// creates the demo sequence they render; product paths check `editor.is_some()` first.
    pub fn editor(&mut self) -> &mut EditorState {
        if self.editor.is_none() {
            let mut demo = EditorState::demo_with_media();
            demo.project.media.clear();
            demo.preview = crate::preview::PreviewState::new();
            let doc = demo.doc().clone();
            demo.preview.build_thumbnails(&doc);
            self.editor = Some(demo);
        }
        self.editor.as_mut().expect("editor was just created")
    }

    pub fn has_editor(&self) -> bool {
        self.editor.is_some()
    }

    /// Re-reads the shortcut overrides after Preferences changed.
    pub fn reload_shortcuts(&mut self) {
        self.shortcut_overrides = shortcut_overrides(&self.preferences.keyboard.shortcuts);
    }

    /// Mirrors the editor's panel layout into `workspace.json` (as `WorkspaceLayout` does on change).
    pub fn save_workspace(&mut self) {
        if let Some(editor) = self.editor.as_ref() {
            self.workspace.left = f64::from(editor.left_frac);
            self.workspace.right = f64::from(editor.right_frac);
            self.workspace.timeline = f64::from(editor.timeline_frac);
            self.workspace.left_collapsed = editor.left_collapsed;
            self.workspace.right_collapsed = editor.right_collapsed;
        }
        let _ = self.workspace.save();
    }

    /// Re-reads the project list for Home (after create/save/close, as `HomeViewModel.Refresh`).
    pub fn refresh_home(&mut self) {
        self.home = HomeState::from_store(&self.store);
    }

    /// Closes the open project and returns Home (`ProjectManager.Close`).
    pub fn close_project(&mut self) {
        self.editor = None;
        self.route = Route::Home;
        self.refresh_home();
    }

    /// Saves `preferences.json` (after a settings change).
    pub fn save_preferences(&mut self) {
        self.preferences.normalize();
        let _ = self.preferences.save();
        self.reload_shortcuts();
    }

    pub fn save_status(&self) -> &'static str {
        match &self.editor {
            Some(e) => e.save.status_text(),
            None => "All changes saved",
        }
    }

    /// Opens a project from the store into the editor (`ProjectManager.Open`).
    pub fn open_project(&mut self, id: &str) -> bool {
        let folder = self.store.project_directory(id);
        match self.store.load_project(id) {
            Ok(Some(project)) => {
                self.route = Route::Editor;
                let mut editor = EditorState::open(project, folder);
                editor.apply_preferences(&self.preferences, &self.workspace);
                self.editor = Some(editor);
                true
            }
            Ok(None) => {
                self.toasts.show("That project is gone");
                false
            }
            Err(error) => {
                self.toasts.show(format!("Couldn't open the project: {error}"));
                false
            }
        }
    }

    /// Creates a new project and opens it (`ProjectManager.Create`).
    pub fn new_project(&mut self) {
        match self.store.create_project("Untitled") {
            Ok(id) => {
                if self.open_project(&id) {
                    // a new project starts with a video and an audio track, not a blank canvas
                    let editor = self.editor();
                    if editor.doc().tracks.is_empty() {
                        editor.editor.add_track(fig_timeline::TrackKind::Video);
                        editor.editor.add_track(fig_timeline::TrackKind::Audio);
                    }
                    self.route = Route::Editor;
                    self.home.projects = HomeState::from_store(&self.store).projects;
                }
            }
            Err(error) => self.toasts.show(format!("Couldn't create the project: {error}")),
        }
    }

    /// Saves the current project; the header's save status reports the result.
    pub fn save_project(&mut self) {
        let Some(editor) = self.editor.as_mut() else { return };
        editor.commit_to_project();
        match self.store.save_project(&mut editor.project) {
            Ok(()) => {
                editor.save = crate::editor_state::SaveState::Saved;
                self.toasts.show("All changes saved");
            }
            Err(error) => {
                editor.save = crate::editor_state::SaveState::Failed;
                self.toasts.show(format!("Couldn't save: {error}"));
            }
        }
    }

    pub fn set_waker(&mut self, waker: Waker) {
        self.waker = Some(waker);
    }

    pub fn is_playing(&self) -> bool {
        self.editor.as_ref().is_some_and(|e| e.transport.playing)
    }

    /// One frame of app time: advances playback and ages the toasts. True when the visible frame
    /// changed (the poll asks for the next one).
    pub fn tick(&mut self, dt: f64) -> bool {
        let mut changed = false;
        let playing = self.is_playing();
        if let Some(editor) = self.editor.as_mut() {
            crate::playback::tick(editor, dt);
            if playing {
                // keep the monitor following the clock (synchronous decode for now; the
                // two-pass worker in P3 moves this off the UI thread)
                let at = editor.playhead;
                editor.preview.show_at(at);
                changed = true;
            }
        }
        changed | self.toasts.tick(dt)
    }

    pub fn window_title(&self) -> String {
        match &self.editor {
            Some(e) if self.route == Route::Editor => format!("{} — {}", e.project_name, self.title),
            _ => self.title.clone(),
        }
    }
}

// ---------------- scenes ----------------

/// Every screen/state the snapshot harness renders. One scene per file the 1:1 gate compares.
pub const SCENES: &[(&str, fn(&mut AppState))] = &[
    ("editor", scene_editor),
    ("editor-selected", scene_editor_selected),
    ("home-empty", scene_home_empty),
    ("home-projects", scene_home_projects),
    ("preferences", scene_preferences),
    ("export", scene_export),
    ("captions", scene_captions),
    ("bookmarks", scene_bookmarks),
    ("shortcuts", scene_shortcuts),
    ("graph", scene_graph),
    ("preview-media", crate::preview::scene_media),
    ("library-effects", crate::library::scene_effects),
    ("library-text", crate::library::scene_text),
    ("export-jobs", scene_export_jobs),
    ("toast", scene_toast),
    ("library-media", scene_library_media),
    ("editor-context", scene_editor_context),
];

fn scene_editor(s: &mut AppState) {
    s.route = Route::Editor;
    if s.editor.is_none() {
        s.editor = Some(EditorState::demo_with_media());
    }
    if let Some(editor) = s.editor.as_mut() {
        editor.apply_preferences(&s.preferences, &s.workspace);
    }
    s.menu = None;
    s.preferences_open = false;
    let e = s.editor();
    e.playhead = 0.0;
    e.timeline.scroll = 0.0;
    e.timeline.pps = 100.0;
    // the reference render has no media library: keep that panel empty for the comparison.
    // The monitor keeps its decoded frame (the preview region is masked in the pixel diff), so a
    // windowed run of this scene drives playback against real frames.
    e.project.media.clear();
    let doc = e.doc().clone();
    e.preview.build_thumbnails(&doc);
}

fn scene_editor_selected(s: &mut AppState) {
    scene_editor(s);
    let e = s.editor();
    if let Some(track) = e.doc().tracks.first().cloned() {
        if let Some(clip) = track.clips.first() {
            e.editor.selection.select_only(&clip.id);
        }
    }
}

fn scene_home_empty(s: &mut AppState) {
    s.route = Route::Home;
    s.home.projects.clear();
}

fn scene_home_projects(s: &mut AppState) {
    s.route = Route::Home;
    s.home = HomeState::with_samples(24);
}

fn scene_preferences(s: &mut AppState) {
    scene_editor(s);
    s.preferences_page = 0;
    s.preferences_open = true;
}

fn scene_export(s: &mut AppState) {
    scene_editor(s);
    s.dialogs.open_export();
}

fn scene_captions(s: &mut AppState) {
    scene_editor(s);
    s.dialogs.open_captions();
}

fn scene_bookmarks(s: &mut AppState) {
    scene_editor(s);
    s.dialogs.open_bookmarks();
}

fn scene_shortcuts(s: &mut AppState) {
    scene_editor(s);
    s.dialogs.open_shortcuts();
}

fn scene_graph(s: &mut AppState) {
    scene_editor(s);
    s.editor().graph_open = true;
}

fn scene_export_jobs(s: &mut AppState) {
    scene_editor(s);
    s.dialogs.open_export_jobs();
}

fn scene_editor_context(s: &mut AppState) {
    scene_editor(s);
    let editor = s.editor();
    if let Some(clip) = editor.doc().tracks[0].clips.first().cloned() {
        editor.editor.selection.select_only(&clip.id);
    }
    crate::timeline_menu::open(s, 260.0, 70.0, 300.0, 500.0);
}

fn scene_library_media(s: &mut AppState) {
    s.route = Route::Editor;
    s.editor = Some(crate::editor_state::EditorState::demo_with_media());
}

fn scene_toast(s: &mut AppState) {
    scene_editor(s);
    s.toasts.show("Clip copied");
    s.toasts.show("All changes saved");
}

// ---------------- view ----------------

pub fn view(state: &AppState, cx: &mut Cx<AppState>) -> El {
    let t = Tokens::dark();
    let mut root = col().key("app.root").w_full().h_full().fill(t.surface_bg).child(crate::shell::title_bar(state, &t));

    root = match state.route {
        Route::Home => root.child(crate::home::view(&state.home, &t)),
        Route::Editor => match &state.editor {
            Some(editor) => root.child(crate::editor::view(state, editor, &t, cx)),
            None => root,
        },
    };

    // overlays, in the C# z-order: menus, preferences card, dialogs, toasts
    if let Some(context) = &state.editor.as_ref().and_then(|e| e.context_menu.clone()) {
        root = root
            .child(crate::shell::menu_scrim(&t))
            .child(crate::timeline_menu::overlay(state, context.clone(), &t));
    }
    if let Some(menu) = state.menu {
        root = root
            .child(crate::shell::menu_scrim(&t))
            .child(crate::shell::menu_dropdown(state, menu, &t));
    }
    if state.preferences_open {
        root = root.child(crate::preferences::overlay(state, &t));
    }
    if state.dialogs.is_open() {
        root = root.child(crate::dialogs::overlay(state, &t));
    }
    root.child(crate::toasts::overlay(&state.toasts, &t)).children(crate::chrome::resize_handles())
}

/// Parses the user's changed shortcuts; only those override the built-in handling.
fn shortcut_overrides(map: &std::collections::BTreeMap<String, String>) -> Vec<(String, KeyCombo)> {
    map.iter()
        .filter_map(|(action, gesture)| crate::services::parse_gesture(gesture).map(|combo| (action.clone(), combo)))
        .collect()
}
