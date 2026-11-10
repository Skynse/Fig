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
    /// What a batch export makes, one file each.
    pub chapters: Vec<Chapter>,
}

/// One file of a batch (`ExportChapterItem`): a range marker, or with no ranges the stretch from one point marker to
/// the next.
pub struct Chapter {
    pub name: String,
    pub color: String,
    pub start: f64,
    pub end: f64,
    pub file: TextEdit,
    pub included: bool,
}

fn slug(name: &str) -> String {
    let s: String = name.trim().to_lowercase().chars().map(|c| if c.is_alphanumeric() { c } else { '-' }).collect();
    let s = s.split('-').filter(|p| !p.is_empty()).collect::<Vec<_>>().join("-");
    if s.is_empty() { "range".into() } else { s }
}

/// The batch's files: every range marker, or (none) the sections between point markers, frame-exact.
fn chapters(doc: &fig_timeline::Timeline) -> Vec<Chapter> {
    let end = fig_media::export::timeline_end_sec(doc);
    let rate = doc.rate;
    let frame = |t: f64| rate.to_seconds(rate.to_frame(t));
    let mut markers: Vec<&fig_timeline::Marker> = doc.markers.iter().collect();
    markers.sort_by(|a, b| a.start_sec.total_cmp(&b.start_sec));
    let has_ranges = markers.iter().any(|m| m.is_range());
    let mut out = Vec::new();
    for (i, m) in markers.iter().enumerate() {
        if has_ranges && !m.is_range() {
            continue;
        }
        let stop = if m.is_range() {
            m.end_sec()
        } else {
            markers[i + 1..].iter().map(|n| n.start_sec).find(|s| *s > m.start_sec + 1e-6).unwrap_or(end)
        };
        let (start, stop) = (frame(m.start_sec), frame(stop.min(end)));
        if stop - start < 1e-6 {
            continue;
        }
        let index = out.len() + 1;
        let name = if m.name.trim().is_empty() { format!("Range {index}") } else { m.name.trim().to_string() };
        let file = match m.output_name.as_deref().map(str::trim).filter(|n| !n.is_empty()) {
            Some(n) if n.to_lowercase().ends_with(".mp4") => n.to_string(),
            Some(n) => format!("{n}.mp4"),
            None => format!("{index:02}-{}.mp4", slug(&name)),
        };
        out.push(Chapter { name, color: m.color.clone(), start, end: stop, file: TextEdit::new(file), included: m.include_in_export });
    }
    out
}

/// What is wrong with each included chapter's file name, in order.
fn problems(s: &ExportState) -> Vec<Option<&'static str>> {
    let folder = PathBuf::from(s.output.text.trim());
    s.chapters
        .iter()
        .map(|c| {
            if !c.included {
                return None;
            }
            let file = c.file.text.trim();
            if file.is_empty() || file.contains(['/', '\\', '\0']) {
                Some("Enter a valid file name")
            } else if s.chapters.iter().filter(|o| o.included && o.file.text.trim().eq_ignore_ascii_case(file)).count() > 1 {
                Some("Another range uses this file name")
            } else if folder.is_dir() && folder.join(file).exists() {
                Some("A file with this name already exists")
            } else {
                None
            }
        })
        .collect()
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
        chapters: if ranges { chapters(e.doc()) } else { Vec::new() },
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
    let mut body = super::card(560.0, t).child(super::title_row(if s.ranges { "Export marked ranges" } else { "Export" }, t));
    body = body.child(widgets::note(if s.ranges { "Each checked range becomes a separate video file." } else { "The whole timeline, as one video." }, t));
    if s.ranges {
        let issues = problems(s);
        let mut list = col().w_full().gap(4.0);
        if s.chapters.is_empty() {
            list = list.child(widgets::note("This timeline has no marked ranges yet. Add range markers in the Markers panel, or create ranges from your bookmarks below.", t));
        }
        for (i, c) in s.chapters.iter().enumerate() {
            let mut r = row()
                .w_full()
                .gap(8.0)
                .center_y()
                .child(widgets::checkbox(c.included, "", t).on_press(move |a: &mut AppState| {
                    if let Some(c) = state(a).and_then(|s| s.chapters.get_mut(i)) {
                        c.included = !c.included;
                    }
                }))
                .child(el().w(4.0).h(18.0).radius(2.0).fill(hex(if c.color.is_empty() { "#5f9cf8" } else { &c.color })))
                .child(text(c.name.clone()).font(font(12.5, 600.0)).color(t.text_primary).w(110.0).truncate())
                .child(
                    text_input(&c.file, font(11.0, 400.0))
                        .grow(1.0)
                        .min_w(0.0)
                        .px(6.0)
                        .py(3.0)
                        .radius(4.0)
                        .fill(hex("#1b1d22"))
                        .border(1.0, if issues[i].is_some() { hex("#ff6b6b") } else { t.border })
                        .color(t.text_primary)
                        .help("File name for this range")
                        .on_edit(move |a: &mut AppState| match a.dialog.as_mut() {
                            Some(Dialog::Export(s)) if i < s.chapters.len() => &mut s.chapters[i].file,
                            _ => &mut a.field_edit,
                        }),
                )
                .child(text(format!("{} → {}", e.timecode(c.start), e.timecode(c.end))).font(mono(10.5, 400.0)).color(t.text_muted));
            if !c.included {
                r = r.opacity(0.55);
            }
            list = list.child(r);
            if let Some(problem) = issues[i] {
                list = list.child(text(problem).font(font(11.0, 400.0)).color(hex("#ff6b6b")).pl(30.0));
            }
        }
        // tick all, the colours, invert
        let all = s.chapters.iter().all(|c| c.included);
        let mut colours: Vec<String> = Vec::new();
        for c in &s.chapters {
            if !colours.iter().any(|x| x.eq_ignore_ascii_case(&c.color)) {
                colours.push(c.color.clone());
            }
        }
        let n = s.chapters.iter().filter(|c| c.included).count();
        let mut tools = row()
            .w_full()
            .gap(6.0)
            .center_y()
            .child(widgets::checkbox(all, "", t).help("Tick or untick every range").on_press(move |a: &mut AppState| {
                if let Some(s) = state(a) {
                    for c in &mut s.chapters {
                        c.included = !all;
                    }
                }
            }))
            .child(text(format!("{n} of {} ranges", s.chapters.len())).font(font(11.5, 400.0)).color(t.text_secondary).grow(1.0));
        if colours.len() > 1 {
            for colour in colours {
                let pick = colour.clone();
                tools = tools.child(
                    el().w(16.0)
                        .h(16.0)
                        .radius(8.0)
                        .fill(hex(if colour.is_empty() { "#5f9cf8" } else { &colour }))
                        .border(1.0, t.border)
                        .help("Pick the ranges of this colour · Shift-click adds them")
                        .on_press_tx(move |a: &mut AppState, tx: &mut Tx| {
                            let add = tx.mods().shift;
                            if let Some(s) = state(a) {
                                for c in &mut s.chapters {
                                    let matched = c.color.eq_ignore_ascii_case(&pick);
                                    if matched || !add {
                                        c.included = matched;
                                    }
                                }
                            }
                        }),
                );
            }
        }
        tools = tools.child(widgets::button("Invert", Kind::Ghost, t).h(22.0).help("Tick the unticked ranges and untick the rest").on_press(|a: &mut AppState| {
            if let Some(s) = state(a) {
                for c in &mut s.chapters {
                    c.included = !c.included;
                }
            }
        }));
        body = body
            .children((!s.chapters.is_empty()).then_some(tools))
            .child(col().w_full().max_h(220.0).scroll(rune::ScrollSpec { stick_bottom: false }).child(list))
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
    let n = s.chapters.iter().filter(|c| c.included).count();
    let label = if !s.ranges { "Export".into() } else if n == 1 { "Export 1 video".to_string() } else { format!("Export {n} videos") };
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
        if !s.chapters.iter().any(|c| c.included) {
            return Some("Check at least one range".into());
        }
        if problems(s).iter().any(Option::is_some) {
            return Some("Fix the file names marked in red".into());
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
        for c in s.chapters.iter().filter(|c| c.included) {
            let file = c.file.text.trim().to_string();
            jobs.push((file.clone(), out.join(&file), ExportSettings { range: Some((c.start, c.end)), ..base.clone() }));
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
