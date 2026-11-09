//! Controls drawn over the preview for what is selected (`TransformOverlay.cs`, `SwirlOverlay.cs`,
//! `TransformControlsViewModel.cs`): the Transform effect's camera frame, with the path it takes through its
//! keyframes, and the Swirl transition's center, radius and turn.

use fig_timeline::{EffectInstance, ParamValue};
use indexmap::IndexMap;
use rune::{Color, Cursor, Drag, El, Phase, Shape, Tx, canvas, el, text};

use crate::app::AppState;
use crate::editor::Editor;
use crate::theme::{Tokens, font, hex};

/// The selected Transform effect: its owner, the effect, and the owner's start on the timeline.
pub fn selected_transform(e: &Editor) -> Option<(String, EffectInstance, f64)> {
    let (owner, id) = e.ed.selection.selected_effect()?;
    let fx = e.ed.find_effect(&owner, &id)?;
    if fx.type_id != fig_fx::effects::ids::TRANSFORM {
        return None;
    }
    let origin = e.ed.find_clip(&owner).map_or(0.0, |c| c.start_sec);
    Some((owner, fx.clone(), origin))
}

pub fn keyframe_times(fx: &EffectInstance) -> Vec<f64> {
    let mut times: Vec<f64> = fx.keyframes.values().flat_map(|k| k.iter().map(|p| (p.time_sec * 1e6).round() / 1e6)).collect();
    times.sort_by(f64::total_cmp);
    times.dedup();
    times
}

/// The owner-relative time being edited: the playhead, onto a keyframe when it is within half a frame of one.
pub fn edit_time(e: &Editor, fx: &EffectInstance, origin: f64) -> f64 {
    let limit = e.ed.find_clip(&e.ed.selection.selected_effect().map(|s| s.0).unwrap_or_default()).map_or(f64::INFINITY, |c| c.dur_sec);
    let t = (e.playhead - origin).clamp(0.0, limit);
    let tolerance = 0.5 / e.fps().max(1.0);
    keyframe_times(fx).into_iter().find(|k| (k - t).abs() <= tolerance).unwrap_or(t)
}

/// Sets the camera: a keyframe at the playhead while animated, otherwise the one look for the whole effect.
pub fn set_camera(e: &mut Editor, cx: f64, cy: f64, zoom: f64, rotation: Option<f64>) {
    let Some((owner, fx, origin)) = selected_transform(e) else { return };
    let current = fig_fx::effects::transform_view_at(&fx, edit_time(e, &fx, origin));
    let zoom = zoom.clamp(1.0, 8.0);
    let half = 0.5 / zoom;
    let (cx, cy) = (cx.clamp(half, 1.0 - half), cy.clamp(half, 1.0 - half));
    let turn = rotation.unwrap_or(current.rotation_degrees).clamp(-180.0, 180.0);
    let values: IndexMap<String, f64> =
        [("centerX", cx), ("centerY", cy), ("zoom", zoom), ("rotation", turn)].into_iter().map(|(k, v)| (k.to_string(), v)).collect();
    if fx.keyframes.is_empty() {
        for (k, v) in &values {
            e.ed.set_effect_param(&owner, &fx.id, k, ParamValue::Double(*v));
        }
    } else {
        let t = edit_time(e, &fx, origin);
        e.ed.set_effect_keyframe(&owner, &fx.id, t, &values, None);
    }
}

/// Everything drawn over the picture `rect` (well coordinates) for the selection.
pub fn view(e: &Editor, rect: [f32; 4], t: &Tokens) -> Vec<El> {
    let mut out = Vec::new();
    if let Some(camera) = camera(e, rect, t) {
        out.push(camera);
    }
    if let Some(swirl) = swirl(e, rect, t) {
        out.push(swirl);
    }
    out
}

fn chip(label: String, x: f32, y: f32, rect: [f32; 4]) -> El {
    let w = label.chars().count() as f32 * 6.4 + 18.0;
    let x = x.clamp(rect[0] + 2.0, (rect[0] + rect[2] - w - 2.0).max(rect[0] + 2.0));
    let y = y.clamp(rect[1] + 2.0, (rect[1] + rect[3] - 24.0).max(rect[1] + 2.0));
    el().absolute(x, y).px(9.0).py(4.0).radius(3.0).fill(hex("#151618")).inert().child(text(label).font(font(11.0, 400.0)).color(Color::WHITE))
}

// ---- the Transform camera ------------------------------------------------------------------

fn camera(e: &Editor, rect: [f32; 4], t: &Tokens) -> Option<El> {
    let (_, fx, origin) = selected_transform(e)?;
    let time = edit_time(e, &fx, origin);
    let view = fig_fx::effects::transform_view_at(&fx, time);
    let size = 1.0 / view.zoom.max(1.0);
    let left = (view.center_x - size / 2.0).clamp(0.0, 1.0 - size);
    let top = (view.center_y - size / 2.0).clamp(0.0, 1.0 - size);
    let f = [rect[0] + left as f32 * rect[2], rect[1] + top as f32 * rect[3], size as f32 * rect[2], size as f32 * rect[3]];
    let mut layer = el().absolute(0.0, 0.0).w_full().h_full();
    // dim what falls outside the frame: it reads as "what you will see"
    let dim = Color::BLACK.alpha(0.27);
    for r in [
        [rect[0], rect[1], rect[2], f[1] - rect[1]],
        [rect[0], f[1] + f[3], rect[2], rect[1] + rect[3] - f[1] - f[3]],
        [rect[0], f[1], f[0] - rect[0], f[3]],
        [f[0] + f[2], f[1], rect[0] + rect[2] - f[0] - f[2], f[3]],
    ] {
        if r[2] > 0.5 && r[3] > 0.5 {
            layer = layer.child(el().absolute(r[0], r[1]).w(r[2]).h(r[3]).fill(dim).inert());
        }
    }
    // the route the camera takes through its keyframes
    let times = keyframe_times(&fx);
    if times.len() > 1 {
        let pts: Vec<[f32; 2]> = times
            .iter()
            .map(|&k| fig_fx::effects::transform_view_at(&fx, k))
            .map(|v| [rect[0] + v.center_x as f32 * rect[2], rect[1] + v.center_y as f32 * rect[3]])
            .collect();
        layer = layer.child(canvas(Shape::new().stroke(pts.clone(), 1.5, hex("#c85f9cf8"))).absolute(0.0, 0.0).w_full().h_full().inert());
        for p in pts {
            layer = layer.child(el().absolute(p[0] - 3.5, p[1] - 3.5).w(7.0).h(7.0).radius(3.5).fill(t.accent).inert());
        }
    }
    let on_key = times.iter().any(|k| (k - time).abs() < 1e-6);
    // the frame moves it; a corner zooms (the picture's shape stays)
    layer = layer.child(
        el().absolute(f[0], f[1])
            .w(f[2])
            .h(f[3])
            .border(1.5, t.accent)
            .cursor(Cursor::Move)
            .help("Drag to move the camera · drag a corner to zoom")
            .on_drag(move |s: &mut AppState, d: &mut Drag, tx: &mut Tx| drag_camera(s, d, tx, rect, None)),
    );
    for (i, (hx, hy)) in [(f[0], f[1]), (f[0] + f[2], f[1]), (f[0], f[1] + f[3]), (f[0] + f[2], f[1] + f[3])].into_iter().enumerate() {
        layer = layer.child(
            el().absolute(hx - 4.5, hy - 4.5)
                .w(9.0)
                .h(9.0)
                .fill(Color::WHITE)
                .border(1.0, t.accent)
                .cursor(Cursor::Crosshair)
                .on_drag(move |s: &mut AppState, d: &mut Drag, tx: &mut Tx| drag_camera(s, d, tx, rect, Some(i))),
        );
    }
    let label = format!("{:.2}×{}", view.zoom, if on_key { " · keyframe" } else { "" });
    Some(layer.child(chip(label, f[0], f[1] - 30.0, rect)))
}

thread_local! {
    static CAMERA0: std::cell::Cell<Option<(f64, f64, f64)>> = const { std::cell::Cell::new(None) };
}

fn drag_camera(s: &mut AppState, d: &mut Drag, tx: &mut Tx, rect: [f32; 4], corner: Option<usize>) {
    let Some(e) = s.editor.as_mut() else { return };
    let Some((_, fx, origin)) = selected_transform(e) else { return };
    let view = fig_fx::effects::transform_view_at(&fx, edit_time(e, &fx, origin));
    match d.phase {
        Phase::Down => {
            CAMERA0.set(Some((view.center_x, view.center_y, view.zoom)));
            return;
        }
        Phase::Up => {
            CAMERA0.set(None);
            e.ed.break_coalescing();
            return;
        }
        Phase::Move => {}
    }
    let Some((cx0, cy0, zoom0)) = CAMERA0.get() else { return };
    let (w, h) = (f64::from(rect[2].max(1.0)), f64::from(rect[3].max(1.0)));
    match corner {
        None => set_camera(e, cx0 + f64::from(d.dx) / w, cy0 + f64::from(d.dy) / h, zoom0, None),
        Some(_) => {
            // the new size follows whichever axis the pointer is further along
            let nx = (f64::from(d.start_x + d.dx - rect[0])) / w;
            let ny = (f64::from(d.start_y + d.dy - rect[1])) / h;
            let size = ((nx - view.center_x).abs().max((ny - view.center_y).abs()) * 2.0).max(1.0 / 8.0);
            set_camera(e, view.center_x, view.center_y, 1.0 / size, None);
        }
    }
    tx.instant();
}

// ---- the Swirl transition --------------------------------------------------------------------

struct Swirl {
    left: String,
    right: String,
    cx: f64,
    cy: f64,
    radius: f64,
    rotation: f64,
    clockwise: bool,
}

fn selected_swirl(e: &Editor) -> Option<Swirl> {
    let key = e.ed.selection.selected_transition_key()?;
    let cut = fig_edit::transitions::find_cut(e.doc(), key)?;
    if cut.type_id != fig_fx::transitions::ids::SWIRL {
        return None;
    }
    let l = e.ed.find_clip(&cut.left_clip_id)?;
    let r = e.ed.find_clip(&cut.right_clip_id)?;
    let num = |k: &str, d: f64| {
        l.transition_out.as_ref().and_then(|t| t.params.get(k)).or_else(|| r.transition_in.as_ref().and_then(|t| t.params.get(k))).map_or(d, |v| v.as_number())
    };
    Some(Swirl {
        left: cut.left_clip_id.clone(),
        right: cut.right_clip_id.clone(),
        cx: num("centerX", 0.5),
        cy: num("centerY", 0.5),
        radius: num("radius", 0.42),
        rotation: num("rotation", 90.0),
        clockwise: num("direction", 0.0) < 0.5,
    })
}

#[derive(Clone, Copy)]
enum SwirlGrab {
    Center,
    Radius,
    Rotation,
}

fn swirl(e: &Editor, rect: [f32; 4], t: &Tokens) -> Option<El> {
    let s = selected_swirl(e)?;
    let c = [rect[0] + s.cx as f32 * rect[2], rect[1] + s.cy as f32 * rect[3]];
    let r = s.radius as f32 * rect[2].min(rect[3]);
    let dir = if s.clockwise { 1.0 } else { -1.0 };
    let at = |deg: f32| {
        let a = (deg - 90.0).to_radians();
        [c[0] + r * a.cos(), c[1] + r * a.sin()]
    };
    // the dashed circle, and the arc from "up" to the rotation handle
    let mut circle = Vec::new();
    for i in (0..72).step_by(2) {
        let (a, b) = (at(i as f32 * 5.0), at((i + 1) as f32 * 5.0));
        circle.push(canvas(Shape::new().stroke(vec![a, b], 1.5, t.accent)).absolute(0.0, 0.0).w_full().h_full().inert());
    }
    let sweep = (s.rotation.min(359.9) as f32) * dir;
    let arc: Vec<[f32; 2]> = (0..=48).map(|i| at(sweep * i as f32 / 48.0)).collect();
    let rot = at(sweep);
    let rad = [c[0] - r * 0.5f32.sqrt(), c[1] + r * 0.5f32.sqrt()];
    let handle = |p: [f32; 2], size: f32, grab: SwirlGrab| {
        el().absolute(p[0] - size / 2.0, p[1] - size / 2.0)
            .w(size)
            .h(size)
            .fill(Color::WHITE)
            .border(1.5, t.accent)
            .cursor(Cursor::Move)
            .on_drag(move |s: &mut AppState, d: &mut Drag, tx: &mut Tx| drag_swirl(s, d, tx, rect, grab))
    };
    Some(
        el().absolute(0.0, 0.0)
            .w_full()
            .h_full()
            .children(circle)
            .child(canvas(Shape::new().stroke(arc, 2.0, t.accent)).absolute(0.0, 0.0).w_full().h_full().inert())
            .child(handle(c, 11.0, SwirlGrab::Center))
            .child(handle(rad, 10.0, SwirlGrab::Radius))
            .child(handle(rot, 10.0, SwirlGrab::Rotation))
            .child(chip("Drag center".into(), c[0] + 14.0, c[1] + 12.0, rect))
            .child(chip(format!("Radius · {:.0}%", s.radius * 100.0), c[0] + r + 10.0, c[1] - 10.0, rect))
            .child(chip(format!("Rotation · {:.0}°", s.rotation), rot[0] + 12.0, rot[1] - 26.0, rect)),
    )
}

fn drag_swirl(s: &mut AppState, d: &mut Drag, tx: &mut Tx, rect: [f32; 4], grab: SwirlGrab) {
    let Some(e) = s.editor.as_mut() else { return };
    let Some(sw) = selected_swirl(e) else { return };
    if d.phase == Phase::Up {
        e.ed.break_coalescing();
        return;
    }
    let p = [d.start_x + d.dx, d.start_y + d.dy];
    let c = [rect[0] + sw.cx as f32 * rect[2], rect[1] + sw.cy as f32 * rect[3]];
    let mut set = |k: &str, v: f64| e.ed.set_transition_param(&sw.left, &sw.right, k, ParamValue::Double(v));
    match grab {
        SwirlGrab::Center => {
            set("centerX", f64::from((p[0] - rect[0]) / rect[2].max(1.0)).clamp(0.0, 1.0));
            set("centerY", f64::from((p[1] - rect[1]) / rect[3].max(1.0)).clamp(0.0, 1.0));
        }
        SwirlGrab::Radius => {
            let dist = ((p[0] - c[0]).powi(2) + (p[1] - c[1]).powi(2)).sqrt();
            set("radius", f64::from(dist / rect[2].min(rect[3]).max(1.0)).clamp(0.05, 1.0));
        }
        SwirlGrab::Rotation => {
            let angle = f64::from((p[1] - c[1]).atan2(p[0] - c[0]).to_degrees());
            let turned = (angle + 90.0 + 360.0) % 360.0;
            set("rotation", if sw.clockwise { turned } else { (360.0 - turned) % 360.0 });
        }
    }
    tx.instant();
}
