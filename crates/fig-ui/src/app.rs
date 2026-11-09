//! The app: Home or an open project, the overlays above them (menus, dialogs, Preferences, toasts), and the
//! background work that reports back to the UI thread.

use std::rc::Rc;
use std::sync::OnceLock;

use rune::{AppHandle, Cx, El, Mods, NativeSurface, TextEdit, Tx, col, el};

use crate::editor::Editor;
use crate::home::HomeState;
use crate::menu::Popup;
use crate::playback::Playback;
use crate::settings::{GestureMap, Preferences, WorkspaceLayout};
use crate::theme::Tokens;

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
    pub home: HomeState,
    pub editor: Option<Editor>,
    pub store: fig_project::ProjectStore,
    pub preferences: Preferences,
    pub workspace: WorkspaceLayout,
    pub gestures: GestureMap,
    pub playback: Playback,
    pub surface: NativeSurface,
    pub media: crate::media::MediaCache,
    /// Measures titles and stills for the preview's handles (the preview thread has its own).
    pub layers: std::sync::Arc<fig_render::layers::LayerRasterizer>,
    pub jobs: crate::dialogs::jobs::Jobs,
    pub popup: Option<Popup>,
    pub dialog: Option<crate::dialogs::Dialog>,
    pub preferences_open: Option<crate::preferences::PreferencesState>,
    pub toasts: crate::toasts::Toasts,
    pub window_size: (f32, f32),
    pub mods: Mods,
    /// The inline field being typed into (a number field's value, a rename), one at a time.
    pub field_key: Option<String>,
    pub field_edit: TextEdit,
    pub field_apply: Option<crate::widgets::OnText>,
    /// Files dragged over the window right now (for the drop highlight).
    pub drop_hover: bool,
    /// Something being dragged from the library (onto the timeline).
    pub lib_drag: Option<crate::library::LibDrag>,
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
            home: HomeState::from_store(&store),
            editor: None,
            store,
            workspace: WorkspaceLayout::load(),
            gestures: GestureMap::load(),
            playback: Playback::new(surface.clone()),
            surface,
            media: crate::media::MediaCache::default(),
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

    pub fn editor(&self) -> Option<&Editor> {
        self.editor.as_ref()
    }

    pub fn refresh_home(&mut self) {
        self.home.reload(&self.store);
    }

    /// Opens a project from the store (and maximizes the window, as entering the editor does in the C# build).
    pub fn open_project(&mut self, id: &str, tx: &mut Tx) {
        let folder = self.store.project_directory(id);
        match self.store.load_project(id) {
            Ok(Some(project)) => {
                self.close_project_quietly();
                let editor = Editor::open(id.to_string(), folder, project, &self.preferences, &self.workspace);
                self.editor = Some(editor);
                self.route = Route::Editor;
                self.playback.reset_sources();
                crate::editor::sync_playback(self);
                crate::editor::seek(self, 0.0, false);
                crate::media::prepare_project(self);
                tx.set_title(format!("{} — fig", self.editor.as_ref().map_or("", |e| e.project.name.as_str())));
                if self.window_size.0 < 1400.0 {
                    tx.toggle_maximized();
                }
            }
            Ok(None) => crate::toasts::show(self, "That project is gone"),
            Err(error) => crate::toasts::show(self, format!("Couldn't open the project: {error}")),
        }
    }

    pub fn new_project(&mut self, name: &str, tx: &mut Tx) {
        let name = if name.trim().is_empty() { "Untitled" } else { name.trim() };
        match self.store.create_project(name) {
            Ok(id) => {
                self.open_project(&id, tx);
                if let Some(e) = self.editor.as_mut()
                    && e.doc().tracks.is_empty()
                {
                    e.ed.edit(|ed| {
                        ed.add_track(fig_timeline::TrackKind::Video);
                        ed.add_track(fig_timeline::TrackKind::Audio);
                    });
                }
            }
            Err(error) => crate::toasts::show(self, format!("Couldn't create the project: {error}")),
        }
    }

    fn close_project_quietly(&mut self) {
        if self.playback.is_playing() {
            self.playback.pause();
        }
        if let Some(mut e) = self.editor.take() {
            if e.is_dirty() {
                e.commit_to_project();
                let _ = self.store.save_project(&mut e.project);
            }
        }
        self.dialog = None;
        self.popup = None;
    }

    /// Saves and goes back to Home.
    pub fn close_project(&mut self, tx: &mut Tx) {
        self.close_project_quietly();
        self.route = Route::Home;
        self.refresh_home();
        tx.set_title("fig");
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
        let mut changed = crate::editor::tick(self);
        if self.jobs.active() > 0 {
            tx.wake_after(std::time::Duration::from_millis(250));
            changed = true;
        }
        if matches!(self.dialog, Some(crate::dialogs::Dialog::Captions(_))) {
            tx.wake_after(std::time::Duration::from_millis(200));
            changed = true;
        }
        crate::inspector::sync(self);
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
        .child(crate::shell::title_bar(app, &t));
    root = match (app.route, app.editor.as_ref()) {
        (Route::Editor, Some(editor)) => root.child(crate::editor::view::view(app, editor, &t, cx)),
        _ => root.child(crate::home::view(app, &t)),
    };
    if let Some(prefs) = app.preferences_open.as_ref() {
        root = root.child(crate::preferences::overlay(app, prefs, &t));
    }
    if let Some(dialog) = app.dialog.as_ref() {
        root = root.child(crate::dialogs::overlay(app, dialog, &t));
    }
    if let Some(menu) = crate::menu::overlay(app, &t) {
        root = root.child(menu);
    }
    if let Some(d) = app.lib_drag.as_ref() {
        root = root.child(crate::library::drag_ghost(d, &t));
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
    root.child(crate::toasts::overlay(app, &t))
        .children(crate::chrome::resize_handles())
        .on_drop_files(|s: &mut AppState, files: &[std::path::PathBuf], tx: &mut Tx| {
            s.drop_hover = false;
            crate::media::drop_files(s, files, None, tx);
        })
        .on_drop_hover(|s: &mut AppState, over: bool, _tx: &mut Tx| s.drop_hover = over)
}

/// A shared, cheap-to-clone callback used by views that store handlers.
pub type Handler = Rc<dyn Fn(&mut AppState, &mut Tx)>;
