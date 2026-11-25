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
                .gap(4.0)
                .child(text("Preview").font(font(14.0, 600.0)).color(t.text_primary).grow(1.0))
                .child(crop_button(e, t))
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
        match crop_box(e, rect, t) {
            Some(crop) => w = w.child(crop),
            None => {
                w = w.children(gizmo(app, e, rect, frame, t));
                w = w.children(crate::editor::overlays::view(e, rect, t));
            }
        }
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
    for (i, p) in pts.iter().copied().enumerate() {
        let id = id.clone();
        layer = layer.child(
            el().key(format!("gizmo.corner{i}"))
                .absolute(p[0] - 5.0, p[1] - 5.0)
                .w(10.0)
                .h(10.0)
                .radius(2.0)
                .fill(Color::WHITE)
                .border(1.0, t.accent)
                .cursor(Cursor::Crosshair)
                .on_drag(move |s: &mut AppState, d: &mut Drag, tx: &mut Tx| drag_handle(s, &id, Handle::Corner, d, tx, rect, [p[0] - 5.0, p[1] - 5.0])),
        );
    }
    let rid = id.clone();
    layer = layer
        .child(el().key("gizmo.knob").absolute(knob[0] - 6.0, knob[1] - 6.0).w(12.0).h(12.0).radius(6.0).fill(t.accent).cursor(Cursor::Grab).help("Drag to turn (Shift: 15° steps)").on_drag(
            move |s: &mut AppState, d: &mut Drag, tx: &mut Tx| drag_handle(s, &rid, Handle::Rotate, d, tx, rect, [knob[0] - 6.0, knob[1] - 6.0]),
        ));
    // the body moves it (drawn under the handles)
    let (minx, maxx) = pts.iter().fold((f32::MAX, f32::MIN), |a, p| (a.0.min(p[0]), a.1.max(p[0])));
    let (miny, maxy) = pts.iter().fold((f32::MAX, f32::MIN), |a, p| (a.0.min(p[1]), a.1.max(p[1])));
    let body = el()
        .key("gizmo.body")
        .absolute(minx, miny)
        .w((maxx - minx).max(4.0))
        .h((maxy - miny).max(4.0))
        .cursor(Cursor::Move)
        .on_drag(move |s: &mut AppState, d: &mut Drag, tx: &mut Tx| drag_handle(s, &id, Handle::Move, d, tx, rect, [minx, miny]));
    Some(el().absolute(0.0, 0.0).w_full().h_full().child(body).child(layer))
}

// ---- cropping on the picture ---------------------------------------------------------------

/// The one selected clip that can be cropped.
fn croppable(e: &Editor) -> Option<&Clip> {
    let ids = e.ed.selection.selected_clip_ids();
    let [id] = ids.as_slice() else { return None };
    e.ed.find_clip(id).filter(|c| c.is_croppable())
}

/// Starts or ends cropping the selected clip on the preview; there's nothing to press without one.
fn crop_button(e: &Editor, t: &Tokens) -> El {
    let Some(clip) = croppable(e) else { return el() };
    let on = e.crop_edit.as_deref() == Some(clip.id.as_str());
    widgets::toggle_button(&icons::CROP, on, 26.0, t)
        .help(if on { "Done cropping" } else { "Crop on the preview: drag its edges, corners or inside" })
        .on_press(|s: &mut AppState| toggle_crop(s))
}

fn toggle_crop(s: &mut AppState) {
    let Some(e) = s.editor.as_mut() else { return };
    let id = croppable(e).map(|c| c.id.clone());
    e.crop_edit = if e.crop_edit.is_some() { None } else { id };
    e.show_overlay = true;
}

/// Which sides a crop handle moves; none slides the whole box.
#[derive(Clone, Copy, PartialEq)]
struct Sides {
    left: bool,
    top: bool,
    right: bool,
    bottom: bool,
}

const SLIDE: Sides = Sides { left: false, top: false, right: false, bottom: false };
/// The narrowest a crop may leave the picture, as a share of the frame.
const MIN_KEPT: f64 = 0.1;

/// The clip being cropped, shown whole: what the crop cuts is dimmed, and its box's edges and corners move the sides.
fn crop_box(e: &Editor, rect: [f32; 4], t: &Tokens) -> Option<El> {
    let id = e.crop_edit.as_ref()?;
    let clip = e.ed.find_clip(id)?;
    let local = (e.playhead - clip.start_sec).clamp(0.0, clip.dur_sec);
    let (l, tp, r, b) = fig_timeline::automation::sample_crop(clip, local);
    let x0 = rect[0] + l as f32 * rect[2];
    let y0 = rect[1] + tp as f32 * rect[3];
    let x1 = rect[0] + (1.0 - r) as f32 * rect[2];
    let y1 = rect[1] + (1.0 - b) as f32 * rect[3];
    let mut layer = el().absolute(0.0, 0.0).w_full().h_full();
    let dim = Color::BLACK.alpha(0.55);
    for d in [
        [rect[0], rect[1], rect[2], y0 - rect[1]],
        [rect[0], y1, rect[2], rect[1] + rect[3] - y1],
        [rect[0], y0, x0 - rect[0], y1 - y0],
        [x1, y0, rect[0] + rect[2] - x1, y1 - y0],
    ] {
        if d[2] > 0.5 && d[3] > 0.5 {
            layer = layer.child(el().absolute(d[0], d[1]).w(d[2]).h(d[3]).fill(dim).inert());
        }
    }
    // the box: its inside slides the crop over the picture
    let grab = |sides: Sides, origin: [f32; 2]| move |s: &mut AppState, d: &mut Drag, tx: &mut Tx| drag_crop(s, sides, d, tx, rect, origin);
    // keyed: the dims come and go as the crop changes, and a drag must stay with the handle it started on
    layer = layer.child(
        el().key("crop.box")
            .absolute(x0, y0)
            .w((x1 - x0).max(1.0))
            .h((y1 - y0).max(1.0))
            .border(1.5, Color::WHITE)
            .cursor(Cursor::Move)
            .help("Drag to move the crop · drag an edge or corner to resize it")
            .on_drag(grab(SLIDE, [x0, y0])),
    );
    // thirds, to frame by
    for i in 1..3 {
        let fx = x0 + (x1 - x0) * i as f32 / 3.0;
        let fy = y0 + (y1 - y0) * i as f32 / 3.0;
        layer = layer
            .child(el().absolute(fx, y0).w(1.0).h(y1 - y0).fill(Color::WHITE.alpha(0.3)).inert())
            .child(el().absolute(x0, fy).w(x1 - x0).h(1.0).fill(Color::WHITE.alpha(0.3)).inert());
    }
    let (mx, my) = ((x0 + x1) / 2.0, (y0 + y1) / 2.0);
    let handles = [
        (x0, y0, Sides { left: true, top: true, ..SLIDE }, Cursor::ResizeNwse),
        (x1, y0, Sides { right: true, top: true, ..SLIDE }, Cursor::ResizeNesw),
        (x0, y1, Sides { left: true, bottom: true, ..SLIDE }, Cursor::ResizeNesw),
        (x1, y1, Sides { right: true, bottom: true, ..SLIDE }, Cursor::ResizeNwse),
        (mx, y0, Sides { top: true, ..SLIDE }, Cursor::ResizeRow),
        (mx, y1, Sides { bottom: true, ..SLIDE }, Cursor::ResizeRow),
        (x0, my, Sides { left: true, ..SLIDE }, Cursor::ResizeColumn),
        (x1, my, Sides { right: true, ..SLIDE }, Cursor::ResizeColumn),
    ];
    for (i, (hx, hy, sides, cursor)) in handles.into_iter().enumerate() {
        let corner = (sides.left || sides.right) && (sides.top || sides.bottom);
        let (hw, hh) = if corner { (12.0, 12.0) } else if sides.top || sides.bottom { (22.0, 6.0) } else { (6.0, 22.0) };
        layer = layer.child(
            el().key(format!("crop.h{i}"))
                .absolute(hx - hw / 2.0, hy - hh / 2.0)
                .w(hw)
                .h(hh)
                .radius(2.0)
                .fill(Color::WHITE)
                .border(1.0, t.accent)
                .cursor(cursor)
                .on_drag(grab(sides, [hx - hw / 2.0, hy - hh / 2.0])),
        );
    }
    // what it cuts from each side, and Done, in a row above the box that stays on the picture
    let label = format!("Crop · L {:.0}%  T {:.0}%  R {:.0}%  B {:.0}%", l * 100.0, tp * 100.0, r * 100.0, b * 100.0);
    let row_w = label.chars().count() as f32 * 6.2 + 18.0 + 6.0 + 52.0;
    let rx = x0.clamp(rect[0] + 4.0, (rect[0] + rect[2] - row_w - 4.0).max(rect[0] + 4.0));
    let ry = if y0 - 30.0 >= rect[1] + 4.0 { y0 - 30.0 } else { (y1 + 8.0).min(rect[1] + rect[3] - 26.0) };
    Some(
        layer.child(
            row()
                .key("crop.bar")
                .absolute(rx, ry)
                .gap(6.0)
                .center_y()
                .child(el().px(9.0).py(4.0).radius(3.0).fill(hex("#151618")).inert().child(text(label).font(font(11.0, 400.0)).color(Color::WHITE)))
                .child(
                    el().w(52.0)
                        .h(22.0)
                        .radius(3.0)
                        .fill(t.accent)
                        .center()
                        .cursor(Cursor::Pointer)
                        .help("Done cropping")
                        .child(text("Done").font(font(11.0, 600.0)).color(Color::WHITE))
                        .on_press(|s: &mut AppState| {
                            if let Some(e) = s.editor.as_mut() {
                                e.crop_edit = None;
                            }
                        }),
                ),
        ),
    )
}

thread_local! {
    static CROP0: std::cell::Cell<Option<(f64, f64, f64, f64)>> = const { std::cell::Cell::new(None) };
}

/// Drags a crop handle: the sides it holds follow the pointer, or the whole crop slides. Each side keeps to the most a
/// side may take, and the crop always leaves some of the picture.
fn drag_crop(s: &mut AppState, sides: Sides, d: &mut Drag, tx: &mut Tx, rect: [f32; 4], origin: [f32; 2]) {
    let at = crate::editor::overlays::pointer(d, origin);
    let Some(e) = s.editor.as_mut() else { return };
    let Some(id) = e.crop_edit.clone() else { return };
    let Some(clip) = e.ed.find_clip(&id) else { return };
    match d.phase {
        Phase::Down => {
            let local = (e.playhead - clip.start_sec).clamp(0.0, clip.dur_sec);
            CROP0.set(Some(fig_timeline::automation::sample_crop(clip, local)));
            // the whole drag is one undo step
            e.ed.begin_live();
            return;
        }
        Phase::Up => {
            CROP0.set(None);
            if d.cancelled() {
                e.ed.cancel_live();
            } else {
                e.ed.commit_live();
            }
            return;
        }
        Phase::Move => {}
    }
    let Some((l0, t0, r0, b0)) = CROP0.get() else { return };
    let max = properties::crop(fig_timeline::CropSide::Left).def.max;
    let (w, h) = (f64::from(rect[2].max(1.0)), f64::from(rect[3].max(1.0)));
    let (px, py) = (f64::from(at[0] - rect[0]) / w, f64::from(at[1] - rect[1]) / h);
    let (mut l, mut tp, mut r, mut b) = (l0, t0, r0, b0);
    if sides == SLIDE {
        let dx = (f64::from(d.dx) / w).clamp((-l0).max(r0 - max), (max - l0).min(r0));
        let dy = (f64::from(d.dy) / h).clamp((-t0).max(b0 - max), (max - t0).min(b0));
        (l, r, tp, b) = (l0 + dx, r0 - dx, t0 + dy, b0 - dy);
    } else {
        if sides.left {
            l = px.clamp(0.0, max.min(1.0 - r0 - MIN_KEPT));
        }
        if sides.right {
            r = (1.0 - px).clamp(0.0, max.min(1.0 - l0 - MIN_KEPT));
        }
        if sides.top {
            tp = py.clamp(0.0, max.min(1.0 - b0 - MIN_KEPT));
        }
        if sides.bottom {
            b = (1.0 - py).clamp(0.0, max.min(1.0 - t0 - MIN_KEPT));
        }
    }
    let playhead = e.playhead;
    let ids = [id];
    for (side, v, was) in [
        (fig_timeline::CropSide::Left, l, l0),
        (fig_timeline::CropSide::Top, tp, t0),
        (fig_timeline::CropSide::Right, r, r0),
        (fig_timeline::CropSide::Bottom, b, b0),
    ] {
        if (v - was).abs() > 1e-9 || sides == SLIDE {
            e.ed.set_clip_property(&ids, properties::crop(side), fig_timeline::ParamValue::Double(v), playhead);
        }
    }
    // edits inside a live drag don't count themselves; this marks the timeline changed for what draws it
    let _ = e.ed.live_doc();
    tx.instant();
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

fn drag_handle(s: &mut AppState, id: &str, h: Handle, d: &mut Drag, tx: &mut Tx, rect: [f32; 4], origin: [f32; 2]) {
    let p1 = crate::editor::overlays::pointer(d, origin);
    let p0 = [p1[0] - d.dx, p1[1] - d.dy];
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
            let r0 = ((p0[0] - c[0]).powi(2) + (p0[1] - c[1]).powi(2)).sqrt().max(1.0);
            let r1 = ((p1[0] - c[0]).powi(2) + (p1[1] - c[1]).powi(2)).sqrt();
            l.scale = (start.scale * f64::from(r1 / r0)).clamp(0.1, 4.0);
        }
        Handle::Rotate => {
            let c = [rect[0] + start.center_x as f32 * rect[2], rect[1] + start.center_y as f32 * rect[3]];
            let a0 = (p0[1] - c[1]).atan2(p0[0] - c[0]);
            let a1 = (p1[1] - c[1]).atan2(p1[0] - c[0]);
            let mut r = start.rotation + f64::from((a1 - a0).to_degrees());
            r = ((r + 180.0).rem_euclid(360.0)) - 180.0;
            if shift {
                r = (r / 15.0).round() * 15.0;
            }
            l.rotation = r;
        }
    }
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
