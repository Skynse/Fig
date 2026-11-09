//! Dialog stack: Export, Export jobs, Auto captions, Bookmark ranges and Keyboard shortcuts.
//!
//! One modal at a time over the editor, as the C# `DialogState`; the pages themselves live in
//! their own modules. Every dialog has a scrim, a card, a title row and a footer.

use rune::{el, icon, row, text, El};

use crate::app::AppState;
use crate::icons;
use crate::theme::{font, Tokens, RADIUS_CONTROL, RADIUS_PANEL};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DialogKind {
    Export,
    ExportJobs,
    Captions,
    Bookmarks,
    Shortcuts,
}

#[derive(Default)]
pub struct DialogState {
    pub kind: Option<DialogKind>,
}

impl DialogState {
    pub fn new() -> DialogState {
        DialogState { kind: None }
    }

    pub fn is_open(&self) -> bool {
        self.kind.is_some()
    }

    pub fn close(&mut self) {
        self.kind = None;
    }

    pub fn open_export(&mut self) {
        self.kind = Some(DialogKind::Export);
    }

    pub fn open_export_jobs(&mut self) {
        self.kind = Some(DialogKind::ExportJobs);
    }

    pub fn open_captions(&mut self) {
        self.kind = Some(DialogKind::Captions);
    }

    pub fn open_bookmarks(&mut self) {
        self.kind = Some(DialogKind::Bookmarks);
    }

    pub fn open_shortcuts(&mut self) {
        self.kind = Some(DialogKind::Shortcuts);
    }
}

pub fn overlay(state: &AppState, t: &Tokens) -> El {
    let Some(kind) = state.dialogs.kind else { return el().w(0.0).h(0.0) };
    let (title, w, h, body): (&str, f32, f32, El) = match kind {
        DialogKind::Export => ("Export", 560.0, 696.0, export_body(state, t)),
        DialogKind::ExportJobs => ("Export jobs", 520.0, 420.0, jobs_body(t)),
        DialogKind::Captions => ("Auto captions", 620.0, 560.0, crate::captions::dialog(state.editor.as_ref(), t)),
        DialogKind::Bookmarks => ("Bookmark ranges", 560.0, 500.0, bookmarks_body(t)),
        DialogKind::Shortcuts => ("Keyboard shortcuts", 720.0, 560.0, crate::shortcuts::dialog(&state.shortcuts, &state.preferences, t)),
    };

    let card = rune::col()
        .w(w)
        .h(h)
        .fill(t.panel_bg)
        .radius(RADIUS_PANEL)
        .clip_rounded(RADIUS_PANEL)
        .border(1.0, t.border)
        .padding(16.0)
        .gap(12.0)
        .child(
            row()
                .w_full()
                .h(28.0)
                .center_y()
                .child(text(title).font(font(16.0, 600.0)).color(t.text_primary))
                .child(el().grow(1.0))
                .child(
                    icon(&icons::X, 15.0)
                        .color(t.text_muted)
                        .on_press(|s: &mut AppState| s.dialogs.close()),
                ),
        )
        .child(crate::widgets::rule(t))
        .child(body)
        .child(el().grow(1.0))
        .child(footer(t));

    el()
        .absolute(0.0, 0.0)
        .w_full()
        .h_full()
        .fill(crate::theme::hex("#000000b4"))
        .center()
        .child(card)
}

fn footer(t: &Tokens) -> El {
    row()
        .w_full()
        .h(34.0)
        .center_y()
        .child(el().grow(1.0))
        .child(crate::widgets::button("Cancel", t, false).on_press(|s: &mut AppState| s.dialogs.close()))
        .child(el().w(8.0))
        .child(crate::widgets::button("Export", t, true).on_press(|s: &mut AppState| {
            s.dialogs.open_export_jobs();
            s.toasts.show("Export started");
        }))
}

fn export_body(_state: &AppState, t: &Tokens) -> El {
    rune::col()
        .w_full()
        .gap(10.0)
        .child(row().w_full().h(34.0).center_y().child(label("File name", t)).child(el().grow(1.0)).child(text("Golden hour.mp4").font(font(12.0, 400.0)).color(t.text_secondary)))
        .child(row().w_full().h(34.0).center_y().child(label("Location", t)).child(el().grow(1.0)).child(text("~/.fig/exports").font(font(12.0, 400.0)).color(t.text_secondary)))
        .child(crate::widgets::rule(t))
        .child(row().w_full().h(34.0).center_y().child(label("Format", t)).child(el().grow(1.0)).child(crate::widgets::chip("H.264 · AAC", t)))
        .child(row().w_full().h(34.0).center_y().child(label("Resolution", t)).child(el().grow(1.0)).child(crate::widgets::chip("1920×1080", t)))
        .child(row().w_full().h(34.0).center_y().child(label("Frame rate", t)).child(el().grow(1.0)).child(crate::widgets::chip("30 fps", t)))
        .child(row().w_full().h(34.0).center_y().child(label("Quality", t)).child(el().grow(1.0)).child(crate::widgets::chip("Balanced", t)))
        .child(crate::widgets::rule(t))
        .child(text("Advanced").font(font(12.0, 600.0)).color(t.text_secondary))
        .child(row().w_full().h(30.0).center_y().child(label("Hardware encoding", t)).child(el().grow(1.0)).child(text("When available").font(font(11.0, 400.0)).color(t.text_muted)))
        .child(row().w_full().h(30.0).center_y().child(label("Burn in captions", t)).child(el().grow(1.0)).child(text("Off").font(font(11.0, 400.0)).color(t.text_muted)))
        .child(row().w_full().h(30.0).center_y().child(label("Export marked ranges", t)).child(el().grow(1.0)).child(text("Off").font(font(11.0, 400.0)).color(t.text_muted)))
        .child(crate::widgets::rule(t))
        .child(text("H.264 in an MP4, AAC audio at 48 kHz. Hardware encoding is used when the encoder supports it.").font(font(11.0, 400.0)).color(t.text_faint).wrap())
}

fn jobs_body(t: &Tokens) -> El {
    rune::col()
        .w_full()
        .gap(8.0)
        .child(text("No export jobs yet").font(font(12.0, 400.0)).color(t.text_muted))
        .child(
            row().w_full().h(40.0).radius(RADIUS_CONTROL).fill(t.card_bg).padding(10.0).center_y()
                .child(text("Golden hour.mp4").font(font(12.0, 500.0)).color(t.text_secondary))
                .child(el().grow(1.0))
                .child(text("Queued").font(font(11.0, 400.0)).color(t.text_muted)),
        )
}

fn bookmarks_body(t: &Tokens) -> El {
    rune::col().w_full().gap(8.0).child(text("Ranges from bookmarks").font(font(12.0, 400.0)).color(t.text_muted)).child(
        row().w_full().h(36.0).radius(RADIUS_CONTROL).fill(t.card_bg).padding(10.0).center_y()
            .child(text("No bookmarks in this project").font(font(12.0, 400.0)).color(t.text_muted)),
    )
}

fn label(text_str: &str, t: &Tokens) -> El {
    text(text_str).font(font(12.0, 400.0)).color(t.text_secondary)
}
