//! Dialogs: modal cards over the window (export, auto captions, bookmark ranges, export jobs, the shortcut sheet, and
//! the small prompt and confirm boxes), plus the file-picking commands (Save As, packages, captions files).

pub mod captions;
pub mod export;
pub mod jobs;

use std::rc::Rc;

use rune::{Color, El, TextEdit, Tx, col, el, row, text, text_input};

use crate::app::AppState;
use crate::theme::{Tokens, font, hex, mono};
use crate::widgets::{self, Kind};

pub type OnName = Rc<dyn Fn(&mut AppState, &str)>;

pub enum Dialog {
    Prompt { title: String, label: String, edit: TextEdit, confirm: String, on_ok: OnName },
    Confirm {
        title: String,
        message: String,
        confirm: String,
        danger: bool,
        on_ok: Rc<dyn Fn(&mut AppState)>,
        /// A third choice beside Cancel and the confirm button (Discard).
        alt: Option<(String, Rc<dyn Fn(&mut AppState)>)>,
    },
    Shortcuts,
    Jobs,
    Bookmarks { include_bounds: bool },
    Export(Box<export::ExportState>),
    Captions(Box<captions::CaptionsState>),
}

/// Asks for a name (rename, new project…). Blank falls back to `initial`.
pub fn prompt(app: &mut AppState, title: &str, label: &str, initial: &str, confirm: &str, on_ok: impl Fn(&mut AppState, &str) + 'static) {
    let mut edit = TextEdit::new(initial);
    edit.set_selection(0, initial.len());
    edit.focused = true;
    app.dialog = Some(Dialog::Prompt { title: title.into(), label: label.into(), edit, confirm: confirm.into(), on_ok: Rc::new(on_ok) });
}

pub fn confirm(app: &mut AppState, title: &str, message: &str, confirm: &str, danger: bool, on_ok: impl Fn(&mut AppState) + 'static) {
    app.dialog = Some(Dialog::Confirm { title: title.into(), message: message.into(), confirm: confirm.into(), danger, on_ok: Rc::new(on_ok), alt: None });
}

/// [`confirm`] with a third choice, drawn as a danger button between Cancel and the confirm button.
pub fn choose(
    app: &mut AppState,
    title: &str,
    message: &str,
    confirm: &str,
    on_ok: impl Fn(&mut AppState) + 'static,
    alt: &str,
    on_alt: impl Fn(&mut AppState) + 'static,
) {
    app.dialog = Some(Dialog::Confirm {
        title: title.into(),
        message: message.into(),
        confirm: confirm.into(),
        danger: false,
        on_ok: Rc::new(on_ok),
        alt: Some((alt.into(), Rc::new(on_alt))),
    });
}

pub fn close(app: &mut AppState) {
    if let Some(Dialog::Captions(c)) = app.dialog.as_ref() {
        c.cancel.store(true, std::sync::atomic::Ordering::Relaxed);
    }
    app.dialog = None;
}

fn submit_prompt(app: &mut AppState) {
    if let Some(Dialog::Prompt { edit, on_ok, .. }) = app.dialog.take() {
        on_ok(app, &edit.text);
    }
}

pub fn card(width: f32, t: &Tokens) -> El {
    col().w(width).max_h(640.0).padding(24.0).gap(14.0).radius(14.0).fill(hex("#1d1e20")).border(1.0, t.border).shadow(16.0, 48.0, Color::BLACK.alpha(0.5))
}

pub fn title_row(title: impl Into<String>, t: &Tokens) -> El {
    row()
        .w_full()
        .center_y()
        .child(text(title.into()).font(font(15.0, 600.0)).color(t.text_primary).grow(1.0))
        .child(widgets::icon_button(&crate::icons::X, 14.0, 28.0, t).on_press(close))
}

pub fn overlay(app: &AppState, dialog: &Dialog, t: &Tokens) -> El {
    let body = match dialog {
        Dialog::Prompt { title, label, edit, confirm, .. } => card(380.0, t)
            .child(title_row(title.clone(), t))
            .child(widgets::label(label.clone(), t))
            .child(
                text_input(edit, font(13.0, 400.0))
                    .single_line()
                    .w_full()
                    .h(32.0)
                    .px(10.0)
                    .radius(8.0)
                    .fill(hex("#1b1d22"))
                    .border(1.0, t.accent)
                    .color(t.text_primary)
                    .on_edit(|s: &mut AppState| match s.dialog.as_mut() {
                        Some(Dialog::Prompt { edit, .. }) => edit,
                        _ => &mut s.field_edit,
                    })
                    .on_submit(|s: &mut AppState, _tx: &mut Tx| submit_prompt(s)),
            )
            .child(row().w_full().gap(8.0).justify_end().child(widgets::button("Cancel", Kind::Normal, t).on_press(close)).child(widgets::button(confirm.clone(), Kind::Primary, t).on_press(submit_prompt))),
        Dialog::Confirm { title, message, confirm, danger, alt, .. } => card(400.0, t)
            .child(title_row(title.clone(), t))
            .child(text(message.clone()).font(font(13.0, 400.0)).color(t.text_secondary).wrap())
            .child(
                row()
                    .w_full()
                    .gap(8.0)
                    .justify_end()
                    .child(widgets::button("Cancel", Kind::Normal, t).on_press(close))
                    .children(alt.as_ref().map(|(label, _)| {
                        widgets::button(label.clone(), Kind::Danger, t).on_press(|s: &mut AppState| {
                            if let Some(Dialog::Confirm { alt: Some((_, f)), .. }) = s.dialog.take() {
                                f(s);
                            }
                        })
                    }))
                    .child(
                    widgets::button(confirm.clone(), if *danger { Kind::Danger } else { Kind::Primary }, t).on_press(|s: &mut AppState| {
                        if let Some(Dialog::Confirm { on_ok, .. }) = s.dialog.take() {
                            on_ok(s);
                        }
                    }),
                ),
            ),
        Dialog::Shortcuts => shortcuts(app, t),
        Dialog::Jobs => jobs::view(app, t),
        Dialog::Bookmarks { include_bounds } => bookmarks(app, *include_bounds, t),
        Dialog::Export(state) => export::view(app, state, t),
        Dialog::Captions(state) => captions::view(app, state, t),
    };
    el().absolute(0.0, 0.0)
        .w_full()
        .h_full()
        .fill(Color::BLACK.alpha(0.45))
        .center()
        .child(body.modal(true, close))
}

fn shortcuts(app: &AppState, t: &Tokens) -> El {
    let mut list = col().w_full().gap(6.0);
    let mut section = "";
    for e in crate::commands::CATALOG {
        if e.section != section {
            section = e.section;
            list = list.child(text(section).font(font(12.0, 600.0)).color(t.text_muted).pt(8.0));
        }
        let keys = crate::commands::shortcut_label(e.command, &app.preferences.keyboard.shortcuts);
        list = list.child(row().w_full().child(text(if keys.is_empty() { "—".into() } else { keys }).font(mono(12.0, 400.0)).color(hex("#b8bbc4")).w(170.0)).child(text(e.label).font(font(12.5, 400.0)).color(t.text_secondary)));
    }
    list = list.child(text("Timeline").font(font(12.0, 600.0)).color(t.text_muted).pt(8.0));
    for (keys, what) in crate::commands::FIXED {
        list = list.child(row().w_full().child(text(*keys).font(mono(12.0, 400.0)).color(hex("#b8bbc4")).w(170.0)).child(text(*what).font(font(12.5, 400.0)).color(t.text_secondary).wrap().grow(1.0).min_w(0.0)));
    }
    card(520.0, t)
        .child(title_row("Keyboard shortcuts", t))
        .child(col().w_full().h(460.0).scroll(rune::ScrollSpec { stick_bottom: false }).child(list))
        .child(widgets::note("Change them in Preferences ▸ Keyboard.", t))
}

fn bookmarks(app: &AppState, include_bounds: bool, t: &Tokens) -> El {
    let Some(e) = app.editor.as_ref() else { return card(400.0, t) };
    let plan = fig_edit::markers::plan_bookmark_ranges(e.doc(), include_bounds);
    let mut list = col().w_full().gap(4.0);
    for r in &plan {
        list = list.child(
            row()
                .w_full()
                .gap(8.0)
                .child(text(r.name.clone()).font(font(12.5, 500.0)).color(t.text_primary).grow(1.0))
                .child(text(format!("{} – {}", e.timecode(r.start_sec), e.timecode(r.end_sec))).font(mono(11.0, 400.0)).color(t.text_muted)),
        );
    }
    if plan.is_empty() {
        list = list.child(widgets::note("Add point markers (M) where each part should start; each gap between two becomes a range.", t));
    }
    card(460.0, t)
        .child(title_row("Create ranges from bookmarks", t))
        .child(col().w_full().max_h(320.0).scroll(rune::ScrollSpec { stick_bottom: false }).child(list))
        .child(widgets::checkbox(include_bounds, "Include the start and end of the sequence", t).on_press(|s: &mut AppState| {
            if let Some(Dialog::Bookmarks { include_bounds }) = s.dialog.as_mut() {
                *include_bounds = !*include_bounds;
            }
        }))
        .child(
            row().w_full().gap(8.0).justify_end().child(widgets::button("Cancel", Kind::Normal, t).on_press(close)).child(
                widgets::button(format!("Create {} ranges", plan.len()), Kind::Primary, t).on_press(move |s: &mut AppState| {
                    if let Some(e) = s.editor.as_mut() {
                        let n = e.ed.create_ranges_from_bookmarks(include_bounds).len();
                        e.say(format!("{n} ranges created"));
                    }
                    s.dialog = None;
                }),
            ),
        )
}

// ---- file commands ----------------------------------------------------------------------------

/// File ▸ Save as…: the project, under a new name, as a new project in the store.
pub fn save_as(app: &mut AppState, _tx: &mut Tx) {
    let Some(e) = app.editor.as_ref() else { return };
    let name = format!("{} copy", e.project.name);
    prompt(app, "Save as", "New project name", &name, "Save", |s, name| {
        let Some(e) = s.editor.as_mut() else { return };
        e.commit_to_project();
        let name = if name.trim().is_empty() { "Untitled" } else { name.trim() };
        match s.store.create_project(name) {
            Ok(id) => {
                let mut copy = e.project.clone();
                copy.id = id.clone();
                copy.name = name.to_string();
                match s.store.save_project(&mut copy) {
                    Ok(()) => crate::toasts::show(s, format!("Saved as \u{201c}{name}\u{201d}")),
                    Err(err) => crate::toasts::show(s, format!("Couldn't save: {err}")),
                }
            }
            Err(err) => crate::toasts::show(s, format!("Couldn't save: {err}")),
        }
    });
}

/// File ▸ Save as package…: the project and every file it uses, in one .figpck.
pub fn save_package(app: &mut AppState, tx: &mut Tx) {
    let Some(e) = app.editor.as_ref() else { return };
    let options = rune::file_dialog::FileDialogOptions {
        title: "Save as package".into(),
        file_name: Some(format!("{}.figpck", e.project.name)),
        ..Default::default()
    }
    .filter("Fig package", &["figpck"]);
    tx.save_file(options, |s: &mut AppState, result, _tx| {
        let Ok(Some(path)) = result else { return };
        let Some(e) = s.editor.as_mut() else { return };
        e.commit_to_project();
        let project = e.project.clone();
        let root = s.store.root().to_path_buf();
        crate::toasts::show(s, "Packing the project…");
        crate::app::spawn(
            move || fig_project::ProjectStore::new(root).pack(&project, &path, &mut |_| {}).map_err(|e| e.to_string()),
            |s, r| match r {
                Ok(p) if p.missing.is_empty() => crate::toasts::show(s, format!("Package saved ({} files)", p.files)),
                Ok(p) => crate::toasts::show(s, format!("Package saved; {} file(s) were missing", p.missing.len())),
                Err(err) => crate::toasts::show(s, format!("Couldn't save the package: {err}")),
            },
        );
    });
}

/// File ▸ Export captions: SRT (or plain text, by the extension chosen).
pub fn export_captions(app: &mut AppState, tx: &mut Tx) {
    let Some(e) = app.editor.as_ref() else { return };
    if !e.ed.has_captions() {
        crate::toasts::show(app, "There are no captions to export yet");
        return;
    }
    let options = rune::file_dialog::FileDialogOptions {
        title: "Export captions".into(),
        file_name: Some(format!("{}.srt", e.project.name)),
        ..Default::default()
    }
    .filter("Subtitles", &["srt"])
    .filter("Plain text", &["txt"]);
    tx.save_file(options, |s: &mut AppState, result, _tx| {
        let Ok(Some(path)) = result else { return };
        let Some(e) = s.editor.as_ref() else { return };
        let cues = e.ed.caption_cues(0.0, f64::MAX);
        let body = if path.extension().is_some_and(|x| x.eq_ignore_ascii_case("txt")) {
            fig_timeline::captions::to_plain_text(&cues)
        } else {
            fig_timeline::captions::to_srt(&cues)
        };
        match std::fs::write(&path, body) {
            Ok(()) => crate::toasts::show(s, "Captions exported"),
            Err(err) => crate::toasts::show(s, format!("Couldn't write the captions: {err}")),
        }
    });
}

/// Copies every file the project links to into its own folder, so it travels with it.
pub fn collect_sources(app: &mut AppState) {
    let Some(e) = app.editor.as_mut() else { return };
    e.commit_to_project();
    let mut project = e.project.clone();
    let folder = e.folder.clone();
    let lib = fig_media::library::Library::new(app.store.cache_directory(&e.id));
    crate::toasts::show(app, "Collecting sources…");
    crate::app::spawn(
        move || {
            let r = lib.collect_sources(&mut project, &folder, &mut |_| {}).map_err(|e| e.to_string());
            (r, project.media)
        },
        |s, (r, media)| match r {
            Ok(res) => {
                if let Some(e) = s.editor.as_mut() {
                    e.project.media = media;
                    for a in &mut e.project.media {
                        a.in_project = std::path::Path::new(&a.url).starts_with(&e.folder);
                    }
                    e.media_rev += 1;
                }
                crate::toasts::show(s, format!("{} file(s) copied into the project", res.copied));
            }
            Err(err) => crate::toasts::show(s, format!("Couldn't collect the sources: {err}")),
        },
    );
}
