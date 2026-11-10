//! The preview panel (`PreviewView`): the composited picture, handles to move, scale and turn the selected title or
//! still on it, and the transport bar under it.

use std::rc::Rc;

use fig_render::layers::{LayerSize, geometry};
use fig_timeline::{Clip, LayerTransform, properties};
use indexmap::IndexMap;
use rune::{Color, Cursor, Cx, Drag, El, Phase, Shape, SurfaceFit, Tx, canvas, col, el, icon, row, text};

use crate::app::AppState;
use crate::commands::Command;
use crate::editor::Editor;
use crate::ui::icons;
use crate::ui::menu::Item;
use crate::playback::Quality;
use crate::ui::theme::{Tokens, font, hex, mono};
use crate::ui::widgets;

pub fn view(app: &AppState, e: &Editor, t: &Tokens, cx: &mut Cx<AppState>) -> El {
    let _ = cx;
    col()
        .w_full()
        .h_full()
        .padding(10.0)
        .gap(8.0)
        .child(
            row()
                .w_full()
                .h(26.0)
                .center_y()
                .child(text("Preview").font(font(14.0, 600.0)).color(t.text_primary).grow(1.0))
                .child(
                    widgets::toggle_button(&icons::SCAN, e.show_overlay, 26.0, t)
                        .help("Show or hide the handles on the preview")
                        .on_press(|s: &mut AppState| {
                            if let Some(e) = s.editor.as_mut() {
                                e.show_overlay = !e.show_overlay;
                            }
                        }),
                ),
        )
        .child(
            col()
                .w_full()
                .grow(1.0)
                .min_h(0.0)
                .radius(12.0)
                .clip_rounded(12.0)
                .fill(hex("#0e0f11"))
                .child(well(app, e, t))
                .child(transport(app, e, t)),
        )
}

/// Where the picture sits inside the well: (x, y, w, h), letterboxed.
fn picture_rect(well: [f32; 2], frame: (u32, u32)) -> [f32; 4] {
    let (fw, fh) = (frame.0.max(1) as f32, frame.1.max(1) as f32);
    let s = (well[0] / fw).min(well[1] / fh);
    let (w, h) = (fw * s, fh * s);
    [(well[0] - w) / 2.0, (well[1] - h) / 2.0, w, h]
}

fn well(app: &AppState, e: &Editor, t: &Tokens) -> El {
    let mut w = el()
        .key("preview.well")
        .w_full()
        .grow(1.0)
        .min_h(0.0)
        .fill(t.preview_well)
        .child(el().w_full().h_full().native_surface(&app.surface, SurfaceFit::Fit))
        .on_secondary_press(|s: &mut AppState, p: rune::PointerPress, _tx: &mut Tx| {
            let playing = s.playback.is_playing();
            let overlay = s.editor.as_ref().is_some_and(|e| e.show_overlay);
            let items = vec![
                Item::command(s, if playing { "Pause" } else { "Play" }, Command::PlayPause),
                Item::command(s, "Go to start", Command::GoStart),
                Item::command(s, "Go to end", Command::GoEnd),
                Item::Separator,
                Item::new(if overlay { "Hide controls on the preview" } else { "Show controls on the preview" }, |s, _| {
                    if let Some(e) = s.editor.as_mut() {
                        e.show_overlay = !e.show_overlay;
                    }
                }),
            ];
            crate::ui::menu::open(s, crate::ui::menu::Popup::at(p.x, p.y, items));
        });
    if e.show_overlay
        && !app.playback.is_playing()
        && let Some(size) = rune::memo::layout_size("preview.well")
    {
        let frame = crate::playback::render::frame_size(e.doc(), e.media());
        let rect = picture_rect(size, frame);
        w = w.children(gizmo(app, e, rect, frame, t));
        w = w.children(crate::editor::overlays::view(e, rect, t));
    }
    w
}

/// The selected title's or still's box on the picture, with a corner to scale and a knob to turn it.
fn gizmo(app: &AppState, e: &Editor, rect: [f32; 4], frame: (u32, u32), t: &Tokens) -> Option<El> {
    let ids = e.ed.selection.selected_clip_ids();
    let clip = ids.iter().filter_map(|id| e.ed.find_clip(id)).find(|c| c.layout().is_some() && e.playhead >= c.start_sec && e.playhead < c.end_sec())?;
    let local = e.playhead - clip.start_sec;
    let layout = properties::layout_at(clip, local);
    let size = measure(app, e, clip, frame, local)?;
    let aspect = frame.0 as f64 / frame.1.max(1) as f64;
    let corners = geometry::corners(&layout, size, aspect);
    let to_px = |(x, y): (f64, f64)| [rect[0] + x as f32 * rect[2], rect[1] + y as f32 * rect[3]];
    let pts: Vec<[f32; 2]> = corners.iter().map(|c| to_px(*c)).collect();
    let mut outline = pts.clone();
    outline.push(pts[0]);
    let knob = to_px(geometry::to_frame(&layout, 0.0, -size.height * layout.scale / 2.0 - 0.06, aspect));
    let id = clip.id.clone();
    let mut layer = el()
        .absolute(0.0, 0.0)
        .w_full()
        .h_full()
        .child(canvas(Shape::new().stroke(outline, 1.5, t.accent)).absolute(0.0, 0.0).w_full().h_full().inert());
    for p in &pts {
        let id = id.clone();
        layer = layer.child(
            el().absolute(p[0] - 5.0, p[1] - 5.0)
                .w(10.0)
                .h(10.0)
                .radius(2.0)
                .fill(Color::WHITE)
                .border(1.0, t.accent)
                .cursor(Cursor::Crosshair)
                .on_drag(move |s: &mut AppState, d: &mut Drag, tx: &mut Tx| drag_handle(s, &id, Handle::Corner, d, tx, rect, aspect)),
        );
    }
    let rid = id.clone();
    layer = layer
        .child(el().absolute(knob[0] - 6.0, knob[1] - 6.0).w(12.0).h(12.0).radius(6.0).fill(t.accent).cursor(Cursor::Grab).help("Drag to turn (Shift: 15° steps)").on_drag(
            move |s: &mut AppState, d: &mut Drag, tx: &mut Tx| drag_handle(s, &rid, Handle::Rotate, d, tx, rect, aspect),
        ));
    // the body moves it (drawn under the handles)
    let (minx, maxx) = pts.iter().fold((f32::MAX, f32::MIN), |a, p| (a.0.min(p[0]), a.1.max(p[0])));
    let (miny, maxy) = pts.iter().fold((f32::MAX, f32::MIN), |a, p| (a.0.min(p[1]), a.1.max(p[1])));
    let body = el()
        .absolute(minx, miny)
        .w((maxx - minx).max(4.0))
        .h((maxy - miny).max(4.0))
        .cursor(Cursor::Move)
        .on_drag(move |s: &mut AppState, d: &mut Drag, tx: &mut Tx| drag_handle(s, &id, Handle::Move, d, tx, rect, aspect));
    Some(el().absolute(0.0, 0.0).w_full().h_full().child(body).child(layer))
}

fn measure(app: &AppState, e: &Editor, clip: &Clip, frame: (u32, u32), local: f64) -> Option<LayerSize> {
    let path = clip.media_id().and_then(|id| e.find_media(id)).map(|m| m.url.clone());
    app.layers.measure(clip, path.as_deref(), frame.0 as usize, frame.1 as usize, local)
}

#[derive(Clone, Copy)]
enum Handle {
    Move,
    Corner,
    Rotate,
}

thread_local! {
    static GRAB: std::cell::Cell<Option<LayerTransform>> = const { std::cell::Cell::new(None) };
}

fn drag_handle(s: &mut AppState, id: &str, h: Handle, d: &mut Drag, tx: &mut Tx, rect: [f32; 4], aspect: f64) {
    let Some(e) = s.editor.as_mut() else { return };
    let Some(clip) = e.ed.find_clip(id).cloned() else { return };
    let local = (e.playhead - clip.start_sec).clamp(0.0, clip.dur_sec);
    match d.phase {
        Phase::Down => {
            GRAB.set(Some(properties::layout_at(&clip, local)));
            e.ed.begin_live();
            return;
        }
        Phase::Up => {
            GRAB.set(None);
            if d.cancelled() {
                e.ed.cancel_live();
            } else {
                e.ed.commit_live();
            }
            return;
        }
        Phase::Move => {}
    }
    let Some(start) = GRAB.get() else { return };
    let mut l = start;
    let (fx, fy) = (f64::from(d.dx / rect[2].max(1.0)), f64::from(d.dy / rect[3].max(1.0)));
    let shift = tx.mods().shift;
    match h {
        Handle::Move => {
            l.center_x = start.center_x + fx;
            l.center_y = start.center_y + fy;
            // snap to the middle
            if (l.center_x - 0.5).abs() < 0.012 {
                l.center_x = 0.5;
            }
            if (l.center_y - 0.5).abs() < 0.012 {
                l.center_y = 0.5;
            }
        }
        Handle::Corner => {
            // scale by how far the pointer is from the centre now, against where the press was
            let c = [rect[0] + start.center_x as f32 * rect[2], rect[1] + start.center_y as f32 * rect[3]];
            let p0 = [d.start_x, d.start_y];
            let p1 = [d.start_x + d.dx, d.start_y + d.dy];
            let r0 = ((p0[0] - c[0]).powi(2) + (p0[1] - c[1]).powi(2)).sqrt().max(1.0);
            let r1 = ((p1[0] - c[0]).powi(2) + (p1[1] - c[1]).powi(2)).sqrt();
            l.scale = (start.scale * f64::from(r1 / r0)).clamp(0.1, 4.0);
        }
        Handle::Rotate => {
            let c = [rect[0] + start.center_x as f32 * rect[2], rect[1] + start.center_y as f32 * rect[3]];
            let a0 = (d.start_y - c[1]).atan2(d.start_x - c[0]);
            let a1 = (d.start_y + d.dy - c[1]).atan2(d.start_x + d.dx - c[0]);
            let mut r = start.rotation + f64::from((a1 - a0).to_degrees());
            r = ((r + 180.0).rem_euclid(360.0)) - 180.0;
            if shift {
                r = (r / 15.0).round() * 15.0;
            }
            l.rotation = r;
        }
    }
    let _ = aspect;
    let mut layouts = IndexMap::new();
    layouts.insert(id.to_string(), l);
    let playhead = e.playhead;
    e.ed.set_layouts(&layouts, playhead);
    tx.instant();
}

fn small(i: &'static rune::Icon, tip: &str, t: &Tokens) -> El {
    el().w(28.0).h(28.0).radius(3.0).hover(t.chip_bg).center().help(tip.to_string()).child(icon(i, 14.0).color(t.text_secondary))
}

fn transport(app: &AppState, e: &Editor, t: &Tokens) -> El {
    let playing = app.playback.is_playing();
    let total = e.timecode(e.duration());
    let left = row()
        .gap(6.0)
        .center_y()
        .child(text("").live_text(&e.live_time).font(mono(11.0, 400.0)).color(hex("#b8bbc4")).w(90.0))
        .child(text(format!("/ {total}")).font(mono(11.0, 400.0)).color(t.text_muted));
    let center = row()
        .gap(2.0)
        .center_y()
        .child(small(&icons::SKIP_BACK, "Go to the start (Home)", t).on_press_tx(|s: &mut AppState, tx: &mut Tx| crate::commands::actions::run(s, Command::GoStart, tx)))
        .child(small(&icons::STEP_BACK, "Previous frame (Left)", t).on_press_tx(|s: &mut AppState, tx: &mut Tx| crate::commands::actions::run(s, Command::FrameBack, tx)))
        .child(
            el().w(30.0)
                .h(30.0)
                .radius(3.0)
                .fill(hex("#30343b"))
                .hover(hex("#383c44"))
                .center()
                .help("Play / pause (Space)")
                .child(icon(if playing { &icons::PAUSE } else { &icons::PLAY }, 15.0).color(t.text_primary))
                .on_press(crate::editor::toggle_play),
        )
        .child(small(&icons::STEP_FORWARD, "Next frame (Right)", t).on_press_tx(|s: &mut AppState, tx: &mut Tx| crate::commands::actions::run(s, Command::FrameForward, tx)))
        .child(small(&icons::SKIP_FORWARD, "Go to the end (End)", t).on_press_tx(|s: &mut AppState, tx: &mut Tx| crate::commands::actions::run(s, Command::GoEnd, tx)))
        .child(
            el().w(28.0)
                .h(28.0)
                .radius(3.0)
                .hover(t.chip_bg)
                .center()
                .help("Loop playback (Ctrl+L)")
                .child(icon(&icons::REPEAT, 14.0).color(if e.loop_playback { t.accent } else { t.text_secondary }))
                .on_press_tx(|s: &mut AppState, tx: &mut Tx| crate::commands::actions::run(s, Command::Loop, tx)),
        );
    let quality = app.playback.quality();
    let right = row()
        .gap(6.0)
        .center_y()
        .child(
            el().w(24.0)
                .h(24.0)
                .radius(3.0)
                .hover(t.chip_bg)
                .center()
                .help(if app.playback.has_sound() { "Mute" } else { "No sound device (install alsa-lib-devel and build with --features audio)" })
                .child(icon(if e.muted || !app.playback.has_sound() { &icons::VOLUME_X } else { &icons::VOLUME_2 }, 14.0).color(t.text_secondary))
                .on_press(|s: &mut AppState| {
                    if let Some(e) = s.editor.as_mut() {
                        e.muted = !e.muted;
                    }
                }),
        )
        .child(el().w(72.0).child(widgets::slider(
            "preview.volume",
            f64::from(e.volume),
            0.0,
            1.0,
            1.0,
            Rc::new(|s: &mut AppState, v, _| {
                if let Some(e) = s.editor.as_mut() {
                    e.volume = v as f32;
                    e.muted = false;
                }
            }),
            t,
        )))
        .child(widgets::dropdown("preview.quality", quality.label(), t).w(76.0).h(24.0).on_press(|s: &mut AppState| {
            let current = s.playback.quality();
            let items = Quality::ALL
                .iter()
                .map(|&q| Item::new(q.label(), move |s, _| s.playback.set_quality(q)).checked(q == current))
                .collect();
            crate::ui::menu::open_below(s, "preview.quality", items);
        }));
    row()
        .w_full()
        .h(40.0)
        .px(12.0)
        .fill(t.card_bg)
        .center_y()
        .child(el().grow(1.0).min_w(0.0).child(left))
        .child(center)
        .child(el().grow(1.0).min_w(0.0).child(row().w_full().justify_end().child(right)))
}
