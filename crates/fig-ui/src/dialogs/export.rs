//! The export dialog (`ExportDialog.axaml`): the whole timeline to one MP4, or each marked range to its own file.
//! Exports run as background jobs through `fig_media::export`, with the same renderer the preview uses.

use std::path::PathBuf;
use std::rc::Rc;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};

use fig_media::encode::EncoderChoice;
use fig_media::export::{ExportSettings, ExportTurn};
use rune::{El, TextEdit, Tx, col, el, row, text, text_input};

use super::Dialog;
use super::jobs::Job;
use crate::app::AppState;
use crate::menu::Item;
use crate::theme::{Tokens, font, hex, mono};
use crate::widgets::{self, Kind};

pub struct ExportState {
    pub ranges: bool,
    /// Short side in pixels; 0 is the project's own size.
    pub short_side: u32,
    pub crf: i32,
    pub encoder: EncoderChoice,
    pub turn: ExportTurn,
    pub output: TextEdit,
    /// Range markers (by id) left out of a batch.
    pub skipped: Vec<String>,
}

const SIZES: [(u32, &str); 5] = [(0, "Match project"), (2160, "4K"), (1080, "1080p"), (720, "720p"), (540, "540p")];
const ENCODERS: [(EncoderChoice, &str); 6] = [
    (EncoderChoice::Automatic, "Automatic (GPU when there is one)"),
    (EncoderChoice::Nvenc, "NVIDIA (NVENC)"),
    (EncoderChoice::QuickSync, "Intel (Quick Sync)"),
    (EncoderChoice::Amf, "AMD (AMF)"),
    (EncoderChoice::Vaapi, "VA-API (Linux GPUs)"),
    (EncoderChoice::Software, "CPU (x264)"),
];

fn default_folder(app: &AppState) -> PathBuf {
    app.preferences
        .export
        .folder
        .clone()
        .map(PathBuf::from)
        .filter(|p| p.is_dir())
        .or_else(|| std::env::var_os("HOME").map(|h| PathBuf::from(h).join("Videos")).filter(|p| p.is_dir()))
        .or_else(|| std::env::var_os("HOME").map(PathBuf::from))
        .unwrap_or_else(|| PathBuf::from("."))
}

fn safe_name(name: &str) -> String {
    let s: String = name.chars().map(|c| if c.is_alphanumeric() || " -_.()".contains(c) { c } else { '_' }).collect();
    if s.trim().is_empty() { "export".into() } else { s.trim().to_string() }
}

pub fn open(app: &mut AppState, ranges: bool) {
    let Some(e) = app.editor.as_ref() else { return };
    let folder = default_folder(app);
    let output = if ranges { folder } else { folder.join(format!("{}.mp4", safe_name(&e.project.name))) };
    let encoder = match app.preferences.export.encoder {
        crate::settings::EncoderChoice::Nvenc => EncoderChoice::Nvenc,
        crate::settings::EncoderChoice::QuickSync => EncoderChoice::QuickSync,
        crate::settings::EncoderChoice::Amf => EncoderChoice::Amf,
        crate::settings::EncoderChoice::Vaapi => EncoderChoice::Vaapi,
        crate::settings::EncoderChoice::Software => EncoderChoice::Software,
        crate::settings::EncoderChoice::Automatic => EncoderChoice::Automatic,
    };
    app.dialog = Some(Dialog::Export(Box::new(ExportState {
        ranges,
        short_side: 0,
        crf: app.preferences.export.quality.clamp(0, 51),
        encoder,
        turn: ExportTurn::None,
        output: TextEdit::new(output.to_string_lossy()),
        skipped: Vec::new(),
    })));
}

fn state(app: &mut AppState) -> Option<&mut ExportState> {
    match app.dialog.as_mut() {
        Some(Dialog::Export(s)) => Some(s),
        _ => None,
    }
}

/// The size a setting gives for the timeline (even numbers).
fn size_for(app: &AppState, short_side: u32) -> (u32, u32) {
    let Some(e) = app.editor.as_ref() else { return (1920, 1080) };
    let (w, h) = crate::playback::render::frame_size(e.doc(), e.media());
    if short_side == 0 {
        return (w & !1, h & !1);
    }
    let s = short_side as f64 / w.min(h) as f64;
    (((w as f64 * s).round() as u32) & !1, ((h as f64 * s).round() as u32) & !1)
}

pub fn view(app: &AppState, s: &ExportState, t: &Tokens) -> El {
    let Some(e) = app.editor.as_ref() else { return super::card(560.0, t) };
    let (w, h) = size_for(app, s.short_side);
    let step = (28 - s.crf).clamp(0, 10);
    let qlabel = if step <= 2 { "Smaller file" } else if step >= 8 { "High quality" } else { "Balanced" };
    let ranges: Vec<_> = e.doc().markers.iter().filter(|m| m.is_range()).cloned().collect();
    let mut body = super::card(560.0, t).child(super::title_row(if s.ranges { "Export marked ranges" } else { "Export" }, t));
    body = body.child(widgets::note(if s.ranges { "Each checked range becomes a separate video file." } else { "The whole timeline, as one video." }, t));
    if s.ranges {
        let mut list = col().w_full().gap(4.0);
        if ranges.is_empty() {
            list = list.child(widgets::note("No ranges yet. Add range markers on the ruler, or create them from bookmarks.", t));
        }
        for m in &ranges {
            let on = !s.skipped.contains(&m.id);
            let id = m.id.clone();
            list = list.child(
                row()
                    .w_full()
                    .gap(8.0)
                    .center_y()
                    .child(widgets::checkbox(on, "", t).on_press(move |a: &mut AppState| {
                        if let Some(s) = state(a) {
                            if let Some(i) = s.skipped.iter().position(|x| *x == id) {
                                s.skipped.remove(i);
                            } else {
                                s.skipped.push(id.clone());
                            }
                        }
                    }))
                    .child(el().w(4.0).h(18.0).radius(2.0).fill(hex(if m.color.is_empty() { "#5f9cf8" } else { &m.color })))
                    .child(text(m.output_name.clone().unwrap_or_else(|| m.name.clone())).font(font(12.5, 500.0)).color(t.text_primary).grow(1.0).truncate())
                    .child(text(format!("{} · {}", e.timecode(m.start_sec), e.timecode(m.dur_sec))).font(mono(11.0, 400.0)).color(t.text_muted)),
            );
        }
        body = body
            .child(el().w_full().max_h(180.0).scroll(rune::ScrollSpec { stick_bottom: false }).child(list))
            .child(widgets::button("Create ranges from bookmarks…", Kind::Ghost, t).on_press(|a: &mut AppState| a.dialog = Some(Dialog::Bookmarks { include_bounds: true })));
    }
    body = body
        .child(widgets::rule(t))
        .child(row().w_full().child(col().grow(1.0).child(text("MP4 video").font(font(13.0, 600.0)).color(t.text_primary)).child(widgets::note(format!("H.264 / AAC · {}", e.doc().rate.label()), t))).child(text(format!("{w} × {h}")).font(mono(12.0, 400.0)).color(t.text_secondary)))
        .child(widgets::field_row(
            "Resolution",
            widgets::dropdown("export.size", SIZES.iter().find(|x| x.0 == s.short_side).map_or("Custom", |x| x.1), t).on_press(|a: &mut AppState| {
                let items = SIZES
                    .iter()
                    .map(|&(side, label)| {
                        let (w, h) = size_for(a, side);
                        Item::new(format!("{label} ({w}×{h})"), move |a, _| {
                            if let Some(s) = state(a) {
                                s.short_side = side;
                            }
                        })
                    })
                    .collect();
                crate::menu::open_below(a, "export.size", items);
            }),
            t,
        ))
        .child(widgets::field_row(
            "Quality",
            row()
                .w_full()
                .gap(8.0)
                .center_y()
                .child(el().grow(1.0).child(widgets::slider(
                    "export.quality",
                    f64::from(step),
                    0.0,
                    10.0,
                    5.0,
                    Rc::new(|a: &mut AppState, v, _| {
                        if let Some(s) = state(a) {
                            s.crf = 28 - v.round() as i32;
                        }
                    }),
                    t,
                )))
                .child(text(format!("{qlabel} · CRF {}", s.crf)).font(font(11.5, 400.0)).color(t.text_muted).w(140.0)),
            t,
        ))
        .child(widgets::field_row(
            "Encoder",
            widgets::dropdown("export.encoder", ENCODERS.iter().find(|x| x.0 == s.encoder).map_or("Automatic", |x| x.1), t).on_press(|a: &mut AppState| {
                let items = ENCODERS
                    .iter()
                    .map(|&(enc, label)| {
                        Item::new(label, move |a, _| {
                            if let Some(s) = state(a) {
                                s.encoder = enc;
                            }
                        })
                    })
                    .collect();
                crate::menu::open_below(a, "export.encoder", items);
            }),
            t,
        ))
        .child(widgets::field_row(
            "Orientation",
            widgets::dropdown(
                "export.turn",
                match s.turn {
                    ExportTurn::None => "As edited",
                    ExportTurn::Right => "Turned right (phone held left)",
                    ExportTurn::Left => "Turned left (phone held right)",
                },
                t,
            )
            .on_press(|a: &mut AppState| {
                let items = [(ExportTurn::None, "As edited"), (ExportTurn::Right, "Turned right (phone held left)"), (ExportTurn::Left, "Turned left (phone held right)")]
                    .into_iter()
                    .map(|(turn, label)| {
                        Item::new(label, move |a, _| {
                            if let Some(s) = state(a) {
                                s.turn = turn;
                            }
                        })
                    })
                    .collect();
                crate::menu::open_below(a, "export.turn", items);
            }),
            t,
        ))
        .child(widgets::rule(t))
        .child(widgets::label(if s.ranges { "Destination folder" } else { "Output file" }, t))
        .child(
            row()
                .w_full()
                .gap(6.0)
                .child(
                    text_input(&s.output, font(12.5, 400.0))
                        .single_line()
                        .grow(1.0)
                        .h(30.0)
                        .px(10.0)
                        .radius(8.0)
                        .fill(hex("#1b1d22"))
                        .border(1.0, t.border)
                        .color(t.text_primary)
                        .on_edit(|a: &mut AppState| match a.dialog.as_mut() {
                            Some(Dialog::Export(s)) => &mut s.output,
                            _ => &mut a.field_edit,
                        }),
                )
                .child(widgets::button("Browse…", Kind::Normal, t).on_press_tx(|a: &mut AppState, tx: &mut Tx| browse(a, tx))),
        );
    let problem = validate(app, s);
    let label = if s.ranges { format!("Export {} files", ranges.iter().filter(|m| !s.skipped.contains(&m.id)).count()) } else { "Export".into() };
    let mut go = widgets::button(label, Kind::Primary, t);
    go = if problem.is_none() { go.on_press(start) } else { go.opacity(0.45) };
    body.child(
        row()
            .w_full()
            .gap(8.0)
            .center_y()
            .child(text(problem.unwrap_or_default()).font(font(11.5, 400.0)).color(t.warn).grow(1.0).wrap())
            .child(widgets::button("Cancel", Kind::Normal, t).on_press(super::close))
            .child(go),
    )
}

fn validate(app: &AppState, s: &ExportState) -> Option<String> {
    let e = app.editor.as_ref()?;
    if fig_media::export::timeline_end_sec(e.doc()) <= 0.0 {
        return Some("The timeline is empty".into());
    }
    let out = PathBuf::from(s.output.text.trim());
    if s.ranges {
        if !out.is_dir() {
            return Some("Pick a folder that exists".into());
        }
        if !e.doc().markers.iter().any(|m| m.is_range() && !s.skipped.contains(&m.id)) {
            return Some("Check at least one range".into());
        }
    } else if out.parent().is_none_or(|p| !p.is_dir()) {
        return Some("The output folder doesn't exist".into());
    }
    None
}

fn browse(app: &mut AppState, tx: &mut Tx) {
    let Some(s) = state(app) else { return };
    let current = PathBuf::from(s.output.text.trim());
    if s.ranges {
        let options = rune::file_dialog::FileDialogOptions { title: "Export to folder".into(), directory: Some(current), ..Default::default() };
        tx.open_folder(options, |a: &mut AppState, r, _| {
            if let (Ok(Some(p)), Some(s)) = (r, state(a)) {
                s.output = TextEdit::new(p.to_string_lossy());
            }
        });
    } else {
        let options = rune::file_dialog::FileDialogOptions {
            title: "Export".into(),
            directory: current.parent().map(PathBuf::from),
            file_name: current.file_name().map(|n| n.to_string_lossy().into_owned()),
            ..Default::default()
        }
        .filter("MP4 video", &["mp4"]);
        tx.save_file(options, |a: &mut AppState, r, _| {
            if let (Ok(Some(mut p)), Some(s)) = (r, state(a)) {
                if p.extension().is_none() {
                    p.set_extension("mp4");
                }
                s.output = TextEdit::new(p.to_string_lossy());
            }
        });
    }
}

/// Starts the export(s) as background jobs and closes the dialog.
fn start(app: &mut AppState) {
    let Some(Dialog::Export(s)) = app.dialog.take() else { return };
    let Some(e) = app.editor.as_mut() else { return };
    e.commit_to_project();
    let (w, h) = size_for(app, s.short_side);
    let e = app.editor.as_ref().unwrap();
    let base = ExportSettings { width: w, height: h, quality: s.crf as u32, encoder: s.encoder, turn: s.turn, range: None };
    let out = PathBuf::from(s.output.text.trim());
    let mut jobs: Vec<(String, PathBuf, ExportSettings)> = Vec::new();
    if s.ranges {
        for m in e.doc().markers.iter().filter(|m| m.is_range() && !s.skipped.contains(&m.id)) {
            let name = safe_name(m.output_name.as_deref().unwrap_or(&m.name));
            let path = out.join(format!("{name}.mp4"));
            jobs.push((name, path, ExportSettings { range: Some((m.start_sec, m.end_sec())), ..base.clone() }));
        }
    } else {
        jobs.push((out.file_name().map_or("export".into(), |n| n.to_string_lossy().into_owned()), out.clone(), base));
    }
    let timeline = e.doc().clone();
    let media = e.project.media.clone();
    app.preferences.export.quality = s.crf;
    if let Some(dir) = if s.ranges { Some(out.clone()) } else { out.parent().map(PathBuf::from) } {
        app.preferences.export.folder = Some(dir.to_string_lossy().into_owned());
    }
    app.save_preferences();
    for (name, path, settings) in jobs {
        let progress = Arc::new(AtomicU64::new(0f64.to_bits()));
        let cancel = Arc::new(AtomicBool::new(false));
        app.jobs.list.push(Job { name, path: path.clone(), progress: progress.clone(), cancel: cancel.clone(), done: false, error: None, encoder: None });
        let index = app.jobs.list.len() - 1;
        let (timeline, media) = (timeline.clone(), media.clone());
        crate::app::spawn(
            move || {
                let layers = Arc::new(fig_render::layers::LayerRasterizer::new());
                fig_media::export::export(
                    &timeline,
                    &media,
                    &path,
                    &settings,
                    layers,
                    &mut |f| progress.store(f.to_bits(), Ordering::Relaxed),
                    &|| cancel.load(Ordering::Relaxed),
                )
                .map_err(|e| e.to_string())
            },
            move |a, result| {
                let Some(job) = a.jobs.list.get_mut(index) else { return };
                job.done = true;
                match result {
                    Ok(encoder) => {
                        job.encoder = Some(encoder);
                        let name = job.name.clone();
                        crate::toasts::show(a, format!("Exported {name}"));
                    }
                    Err(err) => {
                        let cancelled = job.cancel.load(Ordering::Relaxed);
                        job.error = Some(if cancelled { "Cancelled".into() } else { err });
                    }
                }
            },
        );
    }
    app.dialog = Some(Dialog::Jobs);
}
