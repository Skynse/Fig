//! Home (`HomeView.axaml`): the projects, as a grid or a list, searched and sorted, with New Project and Import
//! Project (a .figpck package or an OpenTimelineIO file).

use std::path::PathBuf;

use fig_project::{ProjectStore, ProjectSummary};
use rune::{Color, Cursor, El, KeyEvent, Pixels, TextEdit, Tx, col, el, icon, row, text};

use crate::app::AppState;
use crate::icons;
use crate::menu::Item;
use crate::theme::{Tokens, font, hex};
use crate::widgets::{self, Kind};

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum Sort {
    Edited,
    Name,
    Duration,
}

pub struct HomeState {
    pub projects: Vec<ProjectSummary>,
    pub thumbs: Vec<Option<Pixels>>,
    pub search: TextEdit,
    pub sort: Sort,
    pub list: bool,
}

impl HomeState {
    pub fn from_store(store: &ProjectStore) -> HomeState {
        let mut h = HomeState { projects: Vec::new(), thumbs: Vec::new(), search: TextEdit::new(""), sort: Sort::Edited, list: false };
        h.reload(store);
        h
    }

    pub fn reload(&mut self, store: &ProjectStore) {
        self.projects = store.list_projects();
        self.thumbs = self
            .projects
            .iter()
            .map(|p| {
                let path = p.thumbnail.as_ref().map(|t| {
                    let t = PathBuf::from(t);
                    if t.is_absolute() { t } else { p.folder.join(t) }
                })?;
                crate::settings::thumbnail_pixels(&path, 228, 128)
            })
            .collect();
    }

    fn visible(&self) -> Vec<usize> {
        let q = self.search.text.trim().to_lowercase();
        let mut idx: Vec<usize> = (0..self.projects.len()).filter(|&i| q.is_empty() || self.projects[i].name.to_lowercase().contains(&q)).collect();
        match self.sort {
            Sort::Edited => idx.sort_by(|a, b| self.projects[*b].updated_at.cmp(&self.projects[*a].updated_at)),
            Sort::Name => idx.sort_by_key(|i| self.projects[*i].name.to_lowercase()),
            Sort::Duration => idx.sort_by(|a, b| self.projects[*b].duration_sec.total_cmp(&self.projects[*a].duration_sec)),
        }
        idx
    }
}

fn ago(p: &ProjectSummary) -> String {
    let now = chrono::Utc::now();
    let secs = (now - p.updated_at.with_timezone(&chrono::Utc)).num_seconds().max(0);
    match secs {
        s if s < 60 => "just now".into(),
        s if s < 3600 => format!("{} min ago", s / 60),
        s if s < 86_400 => format!("{} h ago", s / 3600),
        s if s < 7 * 86_400 => format!("{} days ago", s / 86_400),
        _ => p.updated_at.format("%b %-d, %Y").to_string(),
    }
}

fn duration(sec: f64) -> String {
    let s = sec.round() as i64;
    format!("{}:{:02}", s / 60, s % 60)
}

pub fn view(app: &AppState, t: &Tokens) -> El {
    let h = &app.home;
    let header = row()
        .w_full()
        .px(24.0)
        .pt(18.0)
        .pb(16.0)
        .gap(16.0)
        .center_y()
        .child(text("Projects").font(font(16.0, 600.0)).color(t.text_primary).grow(1.0))
        .child(widgets::button("Import Project…", Kind::Normal, t).w(148.0).on_press_tx(|s: &mut AppState, tx: &mut Tx| import_dialog(s, tx)))
        .child(widgets::button("+ New Project", Kind::Primary, t).w(148.0).on_press_tx(|s: &mut AppState, _tx: &mut Tx| new_project(s)));
    let mut root = col().w_full().grow(1.0).min_h(0.0).child(header);
    if h.projects.is_empty() {
        return root.child(
            col()
                .w_full()
                .grow(1.0)
                .center()
                .gap(10.0)
                .child(icon(&icons::CLAPPERBOARD, 40.0).color(t.text_muted))
                .child(text("Create your first project").font(font(14.0, 600.0)).color(t.text_primary))
                .child(text("Start a new edit or import an existing timeline.").font(font(12.5, 400.0)).color(t.text_muted))
                .child(widgets::button("+ New Project", Kind::Primary, t).on_press(new_project)),
        );
    }
    let visible = h.visible();
    let sort_label = match h.sort {
        Sort::Edited => "Last edited",
        Sort::Name => "Name",
        Sort::Duration => "Duration",
    };
    let toolbar = row()
        .w_full()
        .px(24.0)
        .pb(16.0)
        .gap(10.0)
        .center_y()
        .child(text("Recent projects").font(font(13.0, 600.0)).color(t.text_primary))
        .child(text(h.projects.len().to_string()).font(font(12.0, 400.0)).color(t.text_muted).grow(1.0))
        .child(widgets::text_box(&h.search, "Search projects", |s| &mut s.home.search, t).w(260.0))
        .child(widgets::dropdown("home.sort", sort_label, t).w(140.0).on_press(|s: &mut AppState| {
            let items = [(Sort::Edited, "Last edited"), (Sort::Name, "Name"), (Sort::Duration, "Duration")]
                .into_iter()
                .map(|(v, l)| Item::new(l, move |s, _| s.home.sort = v))
                .collect();
            crate::menu::open_below(s, "home.sort", items);
        }))
        .child(widgets::toggle_button(&icons::LAYOUT_GRID, !h.list, 30.0, t).on_press(|s: &mut AppState| s.home.list = false))
        .child(widgets::toggle_button(&icons::LIST, h.list, 30.0, t).on_press(|s: &mut AppState| s.home.list = true));
    root = root.child(toolbar);
    if visible.is_empty() {
        return root.child(
            col()
                .w_full()
                .grow(1.0)
                .center()
                .gap(8.0)
                .child(text("No projects match your search").font(font(14.0, 600.0)).color(t.text_primary))
                .child(text("Check the spelling or clear the search.").font(font(12.5, 400.0)).color(t.text_muted))
                .child(widgets::button("Clear search", Kind::Normal, t).on_press(|s: &mut AppState| s.home.search = TextEdit::new(""))),
        );
    }
    let mut items = if h.list { col().w_full().px(24.0).gap(6.0) } else { row().w_full().px(24.0).flex_wrap().gap(16.0) };
    for i in visible {
        let p = &h.projects[i];
        let id = p.id.clone();
        let menu_id = p.id.clone();
        let card = if h.list {
            row()
                .w_full()
                .h(60.0)
                .px(14.0)
                .gap(12.0)
                .radius(6.0)
                .fill(hex("#212328"))
                .hover(hex("#272a30"))
                .center_y()
                .child(icon(&icons::CLAPPERBOARD, 20.0).color(t.text_muted))
                .child(text(p.name.clone()).font(font(13.0, 600.0)).color(t.text_primary).grow(1.0).truncate())
                .child(text(ago(p)).font(font(12.0, 400.0)).color(t.text_muted).w(200.0))
                .child(text(duration(p.duration_sec)).font(font(12.0, 400.0)).color(t.text_muted).w(120.0))
        } else {
            let mut thumb = el().w(228.0).h(128.0).radius(14.0).clip_rounded(14.0).fill(t.card_bg).border(2.0, Color::TRANSPARENT).hover(hex("#6ea5ff"));
            thumb = match h.thumbs.get(i).cloned().flatten() {
                Some(px) => thumb.child(el().w_full().h_full().pixels_fit(&px, rune::SurfaceFit::Fill)),
                None => thumb.center().child(icon(&icons::CLAPPERBOARD, 28.0).color(t.text_faint)),
            };
            col()
                .w(228.0)
                .gap(6.0)
                .child(thumb)
                .child(text(p.name.clone()).font(font(13.0, 600.0)).color(t.text_primary).truncate())
                .child(text(format!("Last edited · {}", ago(p))).font(font(12.0, 400.0)).color(t.text_muted))
                .child(text(format!("Duration · {}", duration(p.duration_sec))).font(font(12.0, 400.0)).color(t.text_muted))
        };
        items = items.child(
            card.key(format!("home.card.{}", p.id))
                .cursor(Cursor::Pointer)
                .on_press_tx(move |s: &mut AppState, tx: &mut Tx| s.open_project(&id, tx))
                .on_secondary_press(move |s: &mut AppState, at: rune::PointerPress, _tx: &mut Tx| project_menu(s, &menu_id, at.x, at.y)),
        );
    }
    root.child(el().w_full().grow(1.0).min_h(0.0).scroll(rune::ScrollSpec { stick_bottom: false }).child(items.pb(24.0))).child(
        row()
            .w_full()
            .px(24.0)
            .py(10.0)
            .gap(18.0)
            .child(widgets::note("Click to open", t))
            .child(widgets::note("Ctrl+N New project", t)),
    )
}

fn project_menu(app: &mut AppState, id: &str, x: f32, y: f32) {
    let (a, b, c, d) = (id.to_string(), id.to_string(), id.to_string(), id.to_string());
    let items = vec![
        Item::new("Open project", move |s, tx| s.open_project(&a, tx)),
        Item::new("Rename", move |s, _| {
            let current = s.home.projects.iter().find(|p| p.id == b).map(|p| p.name.clone()).unwrap_or_default();
            let id = b.clone();
            crate::dialogs::prompt(s, "Rename project", "Project name", &current, "Rename", move |s, name| {
                if let Err(err) = s.store.rename_project(&id, name.trim()) {
                    crate::toasts::show(s, format!("Couldn't rename: {err}"));
                }
                s.refresh_home();
            });
        }),
        Item::new("Show in folder", move |s, _| {
            let folder = s.store.project_directory(&c);
            if let Err(err) = crate::media::reveal(&folder.join("project.json").to_string_lossy()) {
                crate::toasts::show(s, err);
            }
        }),
        Item::Separator,
        Item::new("Delete project…", move |s, _| {
            let name = s.home.projects.iter().find(|p| p.id == d).map(|p| p.name.clone()).unwrap_or_default();
            let id = d.clone();
            crate::dialogs::confirm(s, "Delete project", &format!("Delete \u{201c}{name}\u{201d}? Its folder and everything Fig made for it go; your original media files stay."), "Delete", true, move |s| {
                let _ = s.store.delete_project(&id);
                s.refresh_home();
            });
        })
        .danger(),
    ];
    crate::menu::open(app, crate::menu::Popup::at(x, y, items));
}

fn new_project(app: &mut AppState) {
    crate::dialogs::prompt(app, "New project", "Project name", "Untitled", "Create", |s, name| {
        // the dialog's handler has no Tx to open a window state with: the next poll opens it
        s.pending_new_project = Some(name.to_string());
    });
}

fn import_dialog(app: &mut AppState, tx: &mut Tx) {
    let _ = app;
    let options = rune::file_dialog::FileDialogOptions { title: "Import Project".into(), ..Default::default() }
        .filter("Fig package or OpenTimelineIO", &["figpck", "otio"]);
    tx.open_file(options, |s: &mut AppState, r, tx| {
        let Ok(Some(path)) = r else { return };
        if path.extension().is_some_and(|x| x.eq_ignore_ascii_case("otio")) {
            import_otio(s, &path, tx);
        } else {
            import_package(s, path);
        }
    });
}

/// Unpacks a .figpck into a new project, in the background, then refreshes Home.
pub fn import_package(app: &mut AppState, path: PathBuf) {
    let root = app.store.root().to_path_buf();
    crate::toasts::show(app, "Unpacking the project…");
    crate::app::spawn(
        move || ProjectStore::new(root).unpack(&path, &mut |_| {}).map_err(|e| e.to_string()),
        |s, r| match r {
            Ok(_) => {
                s.refresh_home();
                crate::toasts::show(s, "Project imported");
            }
            Err(err) => crate::toasts::show(s, format!("Couldn't import the package: {err}")),
        },
    );
}

fn import_otio(app: &mut AppState, path: &std::path::Path, tx: &mut Tx) {
    match fig_project::otio::import_file(path) {
        Ok(import) => {
            let name = path.file_stem().map_or("Imported".into(), |s| s.to_string_lossy().into_owned());
            match app.store.create_project(&name) {
                Ok(id) => {
                    let mut project = import.project;
                    project.id = id.clone();
                    project.name = name;
                    if let Err(err) = app.store.save_project(&mut project) {
                        crate::toasts::show(app, format!("Couldn't save the imported project: {err}"));
                        return;
                    }
                    if !import.warnings.is_empty() {
                        crate::toasts::show(app, format!("Imported with {} warning(s)", import.warnings.len()));
                    }
                    app.open_project(&id, tx);
                }
                Err(err) => crate::toasts::show(app, format!("Couldn't create the project: {err}")),
            }
        }
        Err(err) => crate::toasts::show(app, format!("Couldn't read the timeline: {err}")),
    }
}

/// Home's keys: Ctrl+N new project.
pub fn key(app: &mut AppState, ev: &KeyEvent, _tx: &mut Tx) -> bool {
    if ev.key == rune::Key::Command('n') {
        new_project(app);
        return true;
    }
    false
}
