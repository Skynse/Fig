//! Auto captions (`CaptionsDialog.axaml`): pick a speech model (downloaded once), a language and how words are
//! grouped; Fig listens to the timeline's sound, writes what is said, and puts the captions on a new track.

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};

use fig_captions::{MODELS, SpeechModels, TranscribeOptions};
use fig_timeline::captions::{CaptionGrouping, group};
use rune::{Color, El, col, el, row, text};

use super::Dialog;
use crate::app::AppState;
use crate::ui::menu::Item;
use crate::ui::theme::{Tokens, font, hex};
use crate::ui::widgets::{self, Kind};

pub struct CaptionsState {
    pub model: &'static str,
    pub language: String,
    pub grouping: usize,
    pub translate: bool,
    pub only_selected: bool,
    /// 0..1 as f64 bits, while working.
    pub progress: Arc<AtomicU64>,
    pub cancel: Arc<AtomicBool>,
    pub working: Option<String>,
    pub error: Option<String>,
}

const LANGUAGES: [(&str, &str); 12] = [
    ("auto", "Detect"),
    ("en", "English"),
    ("es", "Spanish"),
    ("fr", "French"),
    ("de", "German"),
    ("it", "Italian"),
    ("pt", "Portuguese"),
    ("ja", "Japanese"),
    ("ko", "Korean"),
    ("zh", "Chinese"),
    ("ru", "Russian"),
    ("ar", "Arabic"),
];

fn models() -> SpeechModels {
    SpeechModels::new(crate::settings::speech_models_dir())
}

pub fn open(app: &mut AppState) {
    let p = &app.preferences.captions;
    let model = fig_captions::find_model(&p.model).unwrap_or(fig_captions::default_model()).id;
    app.dialog = Some(Dialog::Captions(Box::new(CaptionsState {
        model,
        language: p.language.clone(),
        grouping: (p.grouping.max(0) as usize).min(3),
        translate: p.translate,
        only_selected: false,
        progress: Arc::new(AtomicU64::new(0)),
        cancel: Arc::new(AtomicBool::new(false)),
        working: None,
        error: None,
    })));
}

fn state(app: &mut AppState) -> Option<&mut CaptionsState> {
    match app.dialog.as_mut() {
        Some(Dialog::Captions(s)) => Some(s),
        _ => None,
    }
}

pub fn view(app: &AppState, s: &CaptionsState, t: &Tokens) -> El {
    let store = models();
    let mut list = col().w_full().gap(6.0);
    for m in MODELS.iter() {
        let installed = store.is_installed(m);
        let on = m.id == s.model;
        let id = m.id;
        let mut r = row()
            .w_full()
            .padding(10.0)
            .gap(10.0)
            .radius(8.0)
            .fill(hex("#232427"))
            .border(1.0, if on { t.accent } else { Color::TRANSPARENT })
            .hover(t.card_bg_alt)
            .center_y()
            .child(col().grow(1.0).min_w(0.0).child(text(format!("{} · {}", m.name, m.size_label())).font(font(12.5, 600.0)).color(t.text_primary)).child(text(m.description).font(font(11.0, 400.0)).color(t.text_muted)))
            .child(text(if installed { "Installed" } else { "Downloads when used" }).font(font(11.0, 400.0)).color(if installed { hex("#7fc8a0") } else { t.text_faint }))
            .on_press(move |a: &mut AppState| {
                if let Some(s) = state(a) {
                    s.model = id;
                }
            });
        if installed {
            r = r.child(widgets::button("Remove", Kind::Ghost, t).h(24.0).on_press(move |_: &mut AppState| {
                if let Some(m) = fig_captions::find_model(id) {
                    models().delete(m);
                }
            }));
        }
        list = list.child(r);
    }
    let lang = LANGUAGES.iter().find(|l| l.0 == s.language).map_or(s.language.as_str(), |l| l.1);
    let groupings = CaptionGrouping::choices();
    let busy = s.working.is_some();
    let frac = f64::from_bits(s.progress.load(Ordering::Relaxed));
    let mut body = super::card(540.0, t)
        .child(super::title_row("Auto captions", t))
        .child(widgets::note("Speech is recognised on this computer: nothing is uploaded. Models download once, to ~/.fig/models/whisper.", t))
        .child(list)
        .child(widgets::field_row(
            "Language",
            widgets::dropdown("cap.lang", lang, t).on_press(|a: &mut AppState| {
                let items = LANGUAGES
                    .iter()
                    .map(|&(code, name)| {
                        Item::new(name, move |a, _| {
                            if let Some(s) = state(a) {
                                s.language = code.into();
                            }
                        })
                    })
                    .collect();
                crate::ui::menu::open_below(a, "cap.lang", items);
            }),
            t,
        ))
        .child(widgets::field_row(
            "Each caption",
            widgets::dropdown("cap.group", groupings[s.grouping.min(3)].0, t).on_press(|a: &mut AppState| {
                let items = CaptionGrouping::choices()
                    .iter()
                    .enumerate()
                    .map(|(i, (name, _))| {
                        Item::new(*name, move |a, _| {
                            if let Some(s) = state(a) {
                                s.grouping = i;
                            }
                        })
                    })
                    .collect();
                crate::ui::menu::open_below(a, "cap.group", items);
            }),
            t,
        ))
        .child(widgets::checkbox(s.translate, "Write the captions in English", t).on_press(|a: &mut AppState| {
            if let Some(s) = state(a) {
                s.translate = !s.translate;
            }
        }))
        .child(widgets::checkbox(s.only_selected, "Only where the selected clips are", t).on_press(|a: &mut AppState| {
            if let Some(s) = state(a) {
                s.only_selected = !s.only_selected;
            }
        }));
    if let Some(w) = &s.working {
        body = body.child(col().w_full().gap(6.0).child(el().w_full().h(4.0).radius(2.0).fill(hex("#0e0f11")).child(el().w_frac(frac as f32).h(4.0).radius(2.0).fill(t.accent))).child(widgets::note(w.clone(), t)));
    }
    if let Some(err) = &s.error {
        body = body.child(text(err.clone()).font(font(12.0, 400.0)).color(t.danger).wrap());
    }
    let _ = app;
    body.child(
        row()
            .w_full()
            .gap(8.0)
            .justify_end()
            .child(widgets::button(if busy { "Stop" } else { "Cancel" }, Kind::Normal, t).on_press(super::close))
            .child(if busy { widgets::button("Working…", Kind::Primary, t).opacity(0.5) } else { widgets::button("Generate captions", Kind::Primary, t).on_press(generate) }),
    )
}

fn generate(app: &mut AppState) {
    let (timeline, media, range) = {
        let Some(e) = app.editor.as_ref() else { return };
        let Some(Dialog::Captions(s)) = app.dialog.as_ref() else { return };
        let range = if s.only_selected {
            let clips: Vec<_> = e.ed.selection.selected_clip_ids().into_iter().filter_map(|id| e.ed.find_clip(&id).cloned()).collect();
            if clips.is_empty() {
                None
            } else {
                Some((clips.iter().map(|c| c.start_sec).fold(f64::MAX, f64::min), clips.iter().map(|c| c.end_sec()).fold(0.0, f64::max)))
            }
        } else {
            None
        };
        (e.doc().clone(), e.project.media.clone(), range)
    };
    let Some(s) = state(app) else { return };
    let model = fig_captions::find_model(s.model).unwrap_or(fig_captions::default_model());
    let options = TranscribeOptions { language: s.language.clone(), translate_to_english: s.translate };
    let grouping = CaptionGrouping::choices()[s.grouping.min(3)].1;
    s.working = Some("Starting…".into());
    s.error = None;
    s.cancel.store(false, Ordering::Relaxed);
    let (progress, cancel) = (s.progress.clone(), s.cancel.clone());
    let (lang, gi, tr) = (s.language.clone(), s.grouping, s.translate);
    app.preferences.captions.model = model.id.into();
    app.preferences.captions.language = lang;
    app.preferences.captions.grouping = gi as i32;
    app.preferences.captions.translate = tr;
    app.save_preferences();
    let say = |text: &'static str| crate::app::later(move |a| {
        if let Some(s) = state(a) {
            s.working = Some(text.into());
        }
    });
    crate::app::spawn(
        move || -> Result<Vec<fig_timeline::CaptionCue>, String> {
            let store = models();
            if !store.is_installed(model) {
                say("Downloading the speech model…");
                let p = progress.clone();
                store.download(model, &mut |f| p.store(f.to_bits(), Ordering::Relaxed), &cancel).map_err(|e| e.to_string())?;
            }
            say("Listening to the timeline…");
            let (start, end) = range.unwrap_or((0.0, fig_media::export::timeline_end_sec(&timeline)));
            if end <= start {
                return Err("There's nothing to listen to".into());
            }
            let p = progress.clone();
            let samples = fig_captions::mix_for_speech(&timeline, &media, start, end - start, &mut |f| p.store((f * 0.2).to_bits(), Ordering::Relaxed), &cancel)
                .map_err(|e| e.to_string())?;
            say("Writing down what is said…");
            let p = progress.clone();
            let words = fig_captions::transcribe(&samples, &store.path_of(model), &options, start, move |f| p.store((0.2 + f * 0.8).to_bits(), Ordering::Relaxed), cancel.clone())
                .map_err(|e| e.to_string())?;
            Ok(group(&words, &grouping))
        },
        |a, result| match result {
            Ok(cues) if cues.is_empty() => {
                if let Some(s) = state(a) {
                    s.working = None;
                    s.error = Some("No speech was heard".into());
                }
            }
            Ok(cues) => {
                a.dialog = None;
                if let Some(e) = a.editor.as_mut() {
                    e.ed.add_captions(&cues, None, None);
                    e.library.tab = crate::editor::library::Tab::Captions;
                }
                crate::ui::toasts::show(a, format!("{} captions added", cues.len()));
            }
            Err(err) => {
                if let Some(s) = state(a) {
                    s.working = None;
                    s.error = Some(if err == "cancelled" { "Stopped".into() } else { err });
                }
            }
        },
    );
}
