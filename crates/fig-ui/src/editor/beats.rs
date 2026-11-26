//! Beat markers as a short session (`BeatMarkersViewModel`): analyze the selected clip's sound once, tune the
//! sensitivity while candidate markers follow the slider on the timeline, then apply (one undo step) or cancel.

use std::rc::Rc;

use fig_media::beats::BeatAnalysis;
use fig_timeline::{Clip, clip_time};
use rune::{El, Phase, col, row, text};

use crate::app::AppState;
use crate::editor::Editor;
use crate::ui::theme::{Tokens, font};
use crate::ui::widgets::{self, Kind};

#[derive(Default)]
pub struct BeatSession {
    /// The flyout under the Beats button is showing.
    pub open: bool,
    pub sensitivity: f64,
    pub kicks_only: bool,
    pub analyzing: bool,
    pub status: String,
    /// The clip as analyzed (if it changes the markers no longer match its sound), and what was found.
    analyzed: Option<(Clip, BeatAnalysis)>,
    /// Bumped by every start and cancel, so an analysis that was superseded is dropped.
    generation: u64,
}

impl BeatSession {
    pub fn new() -> BeatSession {
        BeatSession { sensitivity: 50.0, ..Default::default() }
    }

    pub fn has_preview(&self) -> bool {
        self.analyzed.is_some()
    }
}

/// The Beats button's label: with candidates showing, how many.
pub fn button_label(e: &Editor) -> String {
    match e.ed.beat_preview() {
        Some(p) if e.beats.has_preview() => format!("Beats · {}", p.local_times.len()),
        _ => "Beats".into(),
    }
}

fn fail(e: &mut Editor, message: &str) {
    e.beats.status = message.into();
    e.say(message.to_string());
}

/// Decodes and analyzes the selected clip with sound, then shows candidate markers on it.
pub fn analyze(app: &mut AppState) {
    let Some(e) = app.editor.as_mut() else { return };
    let selected = e.ed.selection.selected_clip_ids();
    // an audio clip, or a video clip playing its own sound
    let clip = e
        .doc()
        .tracks
        .iter()
        .flat_map(|t| &t.clips)
        .find(|c| selected.contains(&c.id) && (c.as_audio().is_some() || c.as_video().is_some_and(|v| v.plays_audio)))
        .cloned();
    let Some(clip) = clip else { return fail(e, "Select a clip with sound first.") };
    let Some(asset) = clip.media_id().and_then(|id| e.find_media(id)).cloned().filter(|a| !a.offline) else {
        return fail(e, "The source audio is unavailable.");
    };
    let duration = clip_time::source_offset(&clip, clip.dur_sec).min(clip.source_out().min(asset.duration_sec) - clip.source_in());
    if duration.is_nan() || duration <= 0.0 {
        return fail(e, "The clip has no audio range to analyze.");
    }
    discard(e);
    e.beats.generation += 1;
    let generation = e.beats.generation;
    e.beats.analyzing = true;
    e.beats.status = "Analyzing…".into();
    let start = clip.source_in();
    crate::app::spawn(
        move || fig_media::beats::analyze(std::path::Path::new(&asset.url), start, Some(duration)).map_err(|e| e.to_string()),
        move |app, result| {
            let Some(e) = app.editor.as_mut() else { return };
            if e.beats.generation != generation {
                return;
            }
            e.beats.analyzing = false;
            match result {
                Ok(analysis) if is_current(e, &clip) => {
                    e.beats.status.clear();
                    e.beats.analyzed = Some((clip, analysis));
                    refresh(e);
                }
                Ok(_) => fail(e, "The clip changed during analysis. Analyze again."),
                Err(err) => fail(e, &format!("Beat analysis failed: {err}")),
            }
        },
    );
}

/// The clip is still the one analyzed, unchanged in what decides where its sound falls.
fn is_current(e: &Editor, was: &Clip) -> bool {
    e.ed.find_clip(&was.id).is_some_and(|c| {
        c.media_id() == was.media_id()
            && c.source_in() == was.source_in()
            && c.source_out() == was.source_out()
            && c.dur_sec == was.dur_sec
            && c.speed == was.speed
            && clip_time::curve(c) == clip_time::curve(was)
    })
}

/// Picks markers from the analysis for the slider and the kicks box; cheap enough for every slider step.
fn refresh(e: &mut Editor) {
    let Some((clip, analysis)) = e.beats.analyzed.as_ref() else { return };
    let local: Vec<f64> = analysis
        .select(e.beats.sensitivity / 100.0, e.beats.kicks_only)
        .into_iter()
        .map(|t| clip_time::local_at(clip, t))
        .filter(|t| *t >= 0.0 && *t < clip.dur_sec)
        .collect();
    let id = clip.id.clone();
    e.ed.set_beat_preview(&id, &local);
    let n = e.ed.beat_preview().map_or(0, |p| p.local_times.len());
    e.beats.status = if n == 0 { "No hits at this sensitivity. Move the slider toward More hits.".into() } else { String::new() };
}

/// Drops the candidates and any analysis in flight; nothing in the timeline changes.
pub fn discard(e: &mut Editor) {
    e.beats.generation += 1;
    if e.beats.analyzed.take().is_some() {
        e.ed.clear_beat_preview();
    }
    e.beats.analyzing = false;
}

/// Called when the timeline changes: a session whose clip changed ends.
pub fn check(e: &mut Editor) {
    if let Some((clip, _)) = e.beats.analyzed.as_ref()
        && !is_current(e, &clip.clone())
    {
        discard(e);
        fail(e, "The clip changed, so the beat preview was cleared.");
    }
}

fn apply(app: &mut AppState) {
    let Some(e) = app.editor.as_mut() else { return };
    if e.beats.analyzed.take().is_none() {
        return;
    }
    let kind = if e.beats.kicks_only { "kick" } else { "beat" };
    let n = e.ed.commit_beat_preview().len();
    e.beats.open = false;
    e.say(if n == 0 { "No beat markers added".to_string() } else { format!("Added {n} {kind} markers") });
}

/// The flyout under the Beats button.
pub fn flyout(app: &AppState, e: &Editor, t: &Tokens) -> Option<El> {
    if !e.beats.open {
        return None;
    }
    let anchor = rune::memo::window_layout("tl.beats")?;
    let b = &e.beats;
    let mut body = col().w(280.0).padding(14.0).gap(10.0).radius(10.0).fill(t.card_bg).border(1.0, t.border).shadow(8.0, 24.0, rune::Color::BLACK.alpha(0.4));
    body = body.child(text("Beat markers").font(font(13.0, 600.0)).color(t.text_primary));
    if !b.has_preview() {
        body = body
            .child(widgets::note("Analyze the selected audio clip, then tune which hits become markers. Nothing is added until you apply.", t))
            .child(widgets::button(if b.analyzing { "Analyzing…" } else { "Analyze selected clip" }, Kind::Primary, t).key("beats.analyze").on_press(|s: &mut AppState| {
                if !s.editor.as_ref().is_some_and(|e| e.beats.analyzing) {
                    analyze(s);
                }
            }));
    } else {
        let set: widgets::OnValue = Rc::new(|s: &mut AppState, v: f64, _: Phase| {
            if let Some(e) = s.editor.as_mut() {
                e.beats.sensitivity = v;
                refresh(e);
            }
        });
        let n = e.ed.beat_preview().map_or(0, |p| p.local_times.len());
        body = body
            .child(text("Sensitivity").font(font(12.0, 500.0)).color(t.text_secondary))
            .child(widgets::slider("beats.sens", b.sensitivity, 0.0, 100.0, 50.0, set, t))
            .child(
                row()
                    .w_full()
                    .child(text("Strong hits").font(font(11.0, 400.0)).color(t.text_muted).grow(1.0))
                    .child(text("More hits").font(font(11.0, 400.0)).color(t.text_muted)),
            )
            .child(widgets::checkbox(b.kicks_only, "Focus on kicks", t).on_press(|s: &mut AppState| {
                if let Some(e) = s.editor.as_mut() {
                    e.beats.kicks_only = !e.beats.kicks_only;
                    refresh(e);
                }
            }))
            .child(widgets::note("Favors sudden bass attacks. Bass notes may also be detected.", t))
            .child(text(format!("{n} markers shown on the clip")).font(font(11.5, 400.0)).color(t.text_secondary))
            .child(
                row()
                    .w_full()
                    .gap(8.0)
                    .justify_end()
                    .child(widgets::button("Cancel", Kind::Normal, t).on_press(|s: &mut AppState| {
                        if let Some(e) = s.editor.as_mut() {
                            discard(e);
                            e.beats.status.clear();
                        }
                    }))
                    .child(widgets::button("Apply", Kind::Primary, t).on_press(apply)),
            );
    }
    if !b.status.is_empty() {
        body = body.child(widgets::note(b.status.clone(), t));
    }
    let x = (anchor[0] + anchor[2] - 280.0).max(8.0);
    // above the button (it sits low in the window), by how tall the flyout is
    let tall = if b.has_preview() { 330.0 } else { 170.0 } + if b.status.is_empty() { 0.0 } else { 30.0 };
    let _ = app;
    Some(rune::el().absolute(0.0, 0.0).w_full().h_full().on_press(|s: &mut AppState| {
        if let Some(e) = s.editor.as_mut() {
            e.beats.open = false;
        }
    }).child(body.absolute(x, (anchor[1] - 8.0 - tall).max(8.0)).on_press(|_: &mut AppState| {})))
}
