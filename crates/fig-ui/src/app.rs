//! The app: Home or an open project, the overlays above them (menus, dialogs, Preferences, toasts), and the
//! background work that reports back to the UI thread.

use std::sync::OnceLock;

use rune::{AppHandle, Cx, El, Mods, NativeSurface, TextEdit, Tx, col, el, text};

use crate::editor::Editor;
use crate::home::HomeState;
use crate::ui::menu::Popup;
use crate::playback::Playback;
use crate::settings::{GestureMap, Preferences, WorkspaceLayout};
use crate::ui::theme::Tokens;

static HANDLE: OnceLock<AppHandle<AppState>> = OnceLock::new();

pub fn set_handle(handle: AppHandle<AppState>) {
    let _ = HANDLE.set(handle);
}

/// Runs `work` on a worker thread, then `done` with its result on the UI thread.
pub fn spawn<T: Send + 'static>(
    work: impl FnOnce() -> T + Send + 'static,
    done: impl FnOnce(&mut AppState, T) + Send + 'static,
) {
    std::thread::spawn(move || {
        let result = work();
        if let Some(handle) = HANDLE.get() {
            handle.update(move |app, _tx| done(app, result));
        }
    });
}

/// Runs `f` on the UI thread from any thread.
/// [`spawn`], with `done` given the window too (to retitle or maximize it).
pub fn spawn_tx<T: Send + 'static>(
    work: impl FnOnce() -> T + Send + 'static,
    done: impl FnOnce(&mut AppState, T, &mut Tx) + Send + 'static,
) {
    std::thread::spawn(move || {
        let result = work();
        if let Some(handle) = HANDLE.get() {
            handle.update(move |app, tx| done(app, result, tx));
        }
    });
}

/// A project being opened: its files are checked (and missing thumbnails made again) before the editor shows.
pub struct Loading {
    pub name: String,
    pub status: std::sync::Arc<std::sync::Mutex<String>>,
}

pub fn later(f: impl FnOnce(&mut AppState) + Send + 'static) {
    if let Some(handle) = HANDLE.get() {
        handle.update(move |app, _tx| f(app));
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Route {
    Home,
    Editor,
}

pub struct AppState {
    pub route: Route,
    /// A choice made in the unsaved-changes prompt: (save first, then quit), carried out on the next poll.
    pub pending_leave: Option<(bool, bool)>,
    pub loading: Option<Loading>,
    pub home: HomeState,
    pub editor: Option<Editor>,
    pub store: fig_project::ProjectStore,
    pub preferences: Preferences,
    pub workspace: WorkspaceLayout,
    pub gestures: GestureMap,
    pub playback: Playback,
    pub surface: NativeSurface,
    pub media: crate::editor::media::MediaCache,
    /// Measures titles and stills for the preview's handles (the preview thread has its own).
    pub layers: std::sync::Arc<fig_render::layers::LayerRasterizer>,
    pub jobs: crate::dialogs::jobs::Jobs,
    pub popup: Option<Popup>,
    pub dialog: Option<crate::dialogs::Dialog>,
    pub preferences_open: Option<crate::preferences::PreferencesState>,
    pub toasts: crate::ui::toasts::Toasts,
    pub window_size: (f32, f32),
    pub mods: Mods,
    /// The inline field being typed into (a number field's value, a rename), one at a time.
    pub field_key: Option<String>,
    pub field_edit: TextEdit,
    pub field_apply: Option<crate::ui::widgets::OnText>,
    /// Files dragged over the window right now (for the drop highlight).
    pub drop_hover: bool,
    /// Something being dragged from the library (onto the timeline).
    pub lib_drag: Option<crate::editor::library::LibDrag>,
    /// A project to create and open on the next poll (asked for from a dialog that has no `Tx`).
    pub pending_new_project: Option<String>,
    /// The window's size when an edge grip drag began.
    pub resize_origin: Option<(f32, f32)>,
}

impl AppState {
    pub fn new() -> AppState {
        crate::settings::ensure_directories();
        let preferences = Preferences::load();
        let store = crate::settings::project_store();
        let surface = NativeSurface::new();
        AppState {
            route: Route::Home,
            pending_leave: None,
            loading: None,
            home: HomeState::from_store(&store),
            editor: None,
            store,
            workspace: WorkspaceLayout::load(),
            gestures: GestureMap::load(),
            playback: Playback::new(surface.clone()),
            surface,
            media: crate::editor::media::MediaCache::default(),
            layers: std::sync::Arc::new(fig_render::layers::LayerRasterizer::new()),
            jobs: Default::default(),
            popup: None,
            dialog: None,
            preferences_open: None,
            toasts: Default::default(),
            window_size: (1200.0, 700.0),
            mods: Mods::default(),
            field_key: None,
            field_edit: TextEdit::new(""),
            field_apply: None,
            drop_hover: false,
            resize_origin: None,
            lib_drag: None,
            pending_new_project: None,
            preferences,
        }
    }

    pub fn refresh_home(&mut self) {
        self.home.reload(&self.store);
    }

    /// Opens a project from the store (and maximizes the window, as entering the editor does in the C# build).
    pub fn open_project(&mut self, id: &str, _tx: &mut Tx) {
        if self.loading.is_some() {
            return;
        }
        match self.store.load_project(id) {
            Ok(Some(mut project)) => {
                let status = std::sync::Arc::new(std::sync::Mutex::new("Opening project...".to_string()));
                self.loading = Some(Loading { name: project.name.clone(), status: status.clone() });
                let cache = self.store.cache_directory(id);
                let id = id.to_string();
                spawn_tx(
                    move || {
                        let lib = fig_media::library::Library::new(cache);
                        let report = lib.validate_and_repair(&mut project, &mut |s| *status.lock().unwrap() = s.to_string());
                        (project, report)
                    },
                    move |app, (project, report), tx| {
                        app.loading = None;
                        app.show_project(&id, project, tx);
                        if report.had_issues() {
                            if let Some(e) = app.editor.as_mut() {
                                e.media_rev += 1;
                            }
                            let mut parts = Vec::new();
                            if report.artifacts_repaired > 0 {
                                parts.push(format!("{} preview(s) regenerated", report.artifacts_repaired));
                            }
                            if report.offline_assets > 0 {
                                parts.push(format!("{} source(s) offline", report.offline_assets));
                            }
                            if report.failed_artifacts > 0 {
                                parts.push(format!("{} preview(s) failed", report.failed_artifacts));
                            }
                            crate::ui::toasts::show(app, parts.join(", "));
                        }
                    },
                );
            }
            Ok(None) => crate::ui::toasts::show(self, "That project is gone"),
            Err(error) => crate::ui::toasts::show(self, format!("Couldn't open the project: {error}")),
        }
    }

    /// Shows a checked project in the editor.
    fn show_project(&mut self, id: &str, project: fig_project::Project, tx: &mut Tx) {
        let folder = self.store.project_directory(id);
        {
            {
                self.close_project_quietly();
                let editor = Editor::open(id.to_string(), folder, project, &self.preferences, &self.workspace);
                self.editor = Some(editor);
                self.route = Route::Editor;
                self.playback.reset_sources();
                crate::editor::sync_playback(self);
                crate::editor::seek(self, 0.0, false);
                crate::editor::media::prepare_project(self);
                // a new project starts with a video and an audio track
                if let Some(e) = self.editor.as_mut()
                    && e.doc().tracks.is_empty()
                {
                    e.ed.edit(|ed| {
                        ed.add_track(fig_timeline::TrackKind::Video);
                        ed.add_track(fig_timeline::TrackKind::Audio);
                    });
                }
                tx.set_title(format!("{} — fig", self.editor.as_ref().map_or("", |e| e.project.name.as_str())));
                if self.window_size.0 < 1400.0 {
                    tx.toggle_maximized();
                }
            }
        }
    }

    pub fn new_project(&mut self, name: &str, tx: &mut Tx) {
        let name = if name.trim().is_empty() { "Untitled" } else { name.trim() };
        match self.store.create_project(name) {
            Ok(id) => {
                self.open_project(&id, tx);
            }
            Err(error) => crate::ui::toasts::show(self, format!("Couldn't create the project: {error}")),
        }
    }

    pub fn close_project_quietly(&mut self) {
        self.drop_project(true);
    }

    /// Takes the editor down, saving its changes first or not.
    fn drop_project(&mut self, save: bool) {
        if self.playback.is_playing() {
            self.playback.pause();
        }
        if let Some(mut e) = self.editor.take()
            && save && e.is_dirty() {
                e.commit_to_project();
                let _ = self.store.save_project(&mut e.project);
            }
        self.dialog = None;
        self.popup = None;
    }

    /// Goes back to Home. With unsaved changes it asks first (unless Preferences say not to: then it saves).
    pub fn close_project(&mut self, tx: &mut Tx) {
        self.leave_project(false, tx);
    }

    /// Closes the project (and, `quit`, the app), asking about unsaved changes as the C# `CloseProjectAsync` does.
    pub fn leave_project(&mut self, quit: bool, tx: &mut Tx) {
        let dirty = self.editor.as_ref().is_some_and(|e| e.is_dirty());
        if !dirty || !self.preferences.general.ask_before_closing_unsaved {
            return self.finish_leaving(true, quit, tx);
        }
        let name = self.editor.as_ref().map_or(String::new(), |e| e.project.name.clone());
        crate::dialogs::choose(
            self,
            "Unsaved changes",
            &format!("\"{name}\" has unsaved changes. Save before closing?"),
            "Save",
            move |s| s.pending_leave = Some((true, quit)),
            "Discard",
            move |s| s.pending_leave = Some((false, quit)),
        );
    }

    fn finish_leaving(&mut self, save: bool, quit: bool, tx: &mut Tx) {
        self.drop_project(save);
        self.route = Route::Home;
        self.refresh_home();
        tx.set_title("fig");
        if quit {
            tx.quit();
        }
    }

    pub fn save_workspace(&mut self) {
        if let Some(e) = self.editor.as_ref() {
            self.workspace.left = f64::from(e.panels.left);
            self.workspace.right = f64::from(e.panels.right);
            self.workspace.timeline = f64::from(e.panels.timeline);
            self.workspace.left_collapsed = e.panels.left_collapsed;
            self.workspace.right_collapsed = e.panels.right_collapsed;
        }
        let _ = self.workspace.save();
    }

    pub fn save_preferences(&mut self) {
        self.preferences.normalize();
        let _ = self.preferences.save();
        if let Some(e) = self.editor.as_mut() {
            e.ed.snap.magnetic = self.preferences.editing.snap;
        }
    }

    /// Before every frame: playback, autosave, expiring toasts and finished jobs. True when the view must redraw.
    pub fn poll(&mut self, tx: &mut Tx) -> bool {
        if let Some(name) = self.pending_new_project.take() {
            self.new_project(&name, tx);
            return true;
        }
        if let Some((save, quit)) = self.pending_leave.take() {
            self.finish_leaving(save, quit, tx);
            return true;
        }
        let mut changed = crate::editor::tick(self);
        if self.loading.is_some() {
            tx.wake_after(std::time::Duration::from_millis(100));
            changed = true;
        }
        if self.jobs.active() > 0 {
            tx.wake_after(std::time::Duration::from_millis(250));
            changed = true;
        }
        if matches!(self.dialog, Some(crate::dialogs::Dialog::Captions(_))) {
            tx.wake_after(std::time::Duration::from_millis(200));
            changed = true;
        }
        crate::editor::inspector::sync(self);
        if let Some(e) = self.editor.as_mut() {
            crate::editor::beats::check(e);
        }
        if self.playback.is_playing() {
            tx.wake_after(std::time::Duration::from_millis(16));
        }
        crate::editor::sync_playback(self);
        crate::editor::autosave(self);
        if let Some(e) = self.editor.as_mut() {
            if e.save.changed_at.is_some() || e.save.saving {
                tx.wake_after(std::time::Duration::from_millis(250));
            }
            if e.hint.as_ref().is_some_and(|(_, at)| at.elapsed().as_secs() >= 4) {
                e.hint = None;
                changed = true;
            }
            if e.hint.is_some() {
                tx.wake_after(std::time::Duration::from_millis(500));
            }
            changed |= crate::timeline::input::auto_scroll(e, tx);
            if e.tl.tick_slides() {
                tx.wake_after(std::time::Duration::from_millis(16));
                changed = true;
            }
        }
        if self.toasts.any() {
            changed |= self.toasts.expire();
            tx.wake_after(std::time::Duration::from_millis(300));
        }
        changed
    }
}

pub fn view(app: &AppState, cx: &mut Cx<AppState>) -> El {
    let t = Tokens::dark();
    let mut root = col()
        .key("app.root")
        .w_full()
        .h_full()
        .fill(t.surface_bg)
        .on_layout(|s: &mut AppState, r: [f32; 4]| s.window_size = (r[2], r[3]))
        .child(crate::ui::title_bar::title_bar(app, &t));
    root = match (app.route, app.editor.as_ref()) {
        _ if app.loading.is_some() => root.child(loading_view(app.loading.as_ref().unwrap(), &t)),
        (Route::Editor, Some(editor)) => root.child(crate::editor::view::view(app, editor, &t, cx)),
        _ => root.child(crate::home::view(app, &t)),
    };
    if let Some(prefs) = app.preferences_open.as_ref() {
        root = root.child(crate::preferences::overlay(app, prefs, &t));
    }
    if let Some(dialog) = app.dialog.as_ref() {
        root = root.child(crate::dialogs::overlay(app, dialog, &t));
    }
    if let Some(f) = app.editor.as_ref().and_then(|e| crate::editor::beats::flyout(app, e, &t)) {
        root = root.child(f);
    }
    if let Some(menu) = crate::ui::menu::overlay(app, &t) {
        root = root.child(menu);
    }
    if let Some(d) = app.lib_drag.as_ref() {
        root = root.child(crate::editor::library::drag_ghost(d, &t));
    }
    if app.drop_hover {
        root = root.child(
            el().absolute(0.0, 0.0)
                .w_full()
                .h_full()
                .border(3.0, t.accent)
                .fill(t.accent.alpha(0.06))
                .inert(),
        );
    }
    root.child(crate::ui::toasts::overlay(app, &t))
        .children(crate::ui::window_edges::resize_handles())
        .on_drop_files(|s: &mut AppState, files: &[std::path::PathBuf], tx: &mut Tx| {
            s.drop_hover = false;
            crate::editor::media::drop_files(s, files, None, tx);
        })
        .on_drop_hover(|s: &mut AppState, over: bool, _tx: &mut Tx| s.drop_hover = over)
}

/// The screen while a project opens (`LoadingView`).
fn loading_view(l: &Loading, t: &Tokens) -> El {
    let status = l.status.lock().map(|s| s.clone()).unwrap_or_default();
    col()
        .w_full()
        .grow(1.0)
        .center()
        .gap(10.0)
        .child(text("fig").font(crate::ui::theme::font(34.0, 700.0)).color(t.text_primary))
        .child(text(status).font(crate::ui::theme::font(13.0, 500.0)).color(t.text_secondary))
        .child(text(l.name.clone()).font(crate::ui::theme::font(12.0, 400.0)).color(t.text_muted))
}
