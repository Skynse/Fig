use super::{TransitionEntry, ids};
use crate::ParamSet;
use crate::frame::{Frame, column_blend, lerp_bytes, lerp_frames, render_rows, scale_frame, weight256};
use crate::param;

pub(super) fn entries() -> Vec<TransitionEntry> {
    vec![
        TransitionEntry::new(ids::CROSS_DISSOLVE, "Cross Dissolve", cross_dissolve)
            .describe("Blend outgoing and incoming clips over the cut."),
        TransitionEntry::new(ids::CURTAIN, "Curtain", curtain)
            .category("Wipe")
            .icon("columns-2")
            .describe("Open vertical curtain panels.")
            .params(vec![param!(Int "panels", "Panels", 6, 2, 24), param!("soft", "Soft edge", 0.1, 0, 0.5)]),
        TransitionEntry::new(ids::FADE_TO_BLACK, "Fade to Black", fade_to_black)
            .icon("moon")
            .describe("Fade out, through black, then in."),
        TransitionEntry::new(ids::IRIS, "Iris", iris)
            .category("Wipe")
            .icon("circle-dot")
            .describe("Expand a circle from the center.")
            .params(vec![param!("soft", "Soft edge", 0.1, 0, 0.5)]),
        TransitionEntry::new(ids::PUSH, "Push", push)
            .category("Camera")
            .icon("move-right")
            .describe("Incoming pushes the outgoing off-screen.")
            .params(vec![param!(List "direction", "Direction", 0, ["Left", "Right", "Up", "Down"])]),
        TransitionEntry::new(ids::SLIDE, "Slide", slide)
            .category("Camera")
            .icon("move-horizontal")
            .describe("Slide the incoming clip over the outgoing.")
            .params(vec![param!(List "direction", "Direction", 0, ["Left", "Right", "Up", "Down"])]),
        TransitionEntry::new(ids::SWIRL, "Swirl", swirl)
            .category("Camera")
            .icon("tornado")
            .duration(1.4)
            .describe("Spin the picture around a point while dissolving.")
            .params(vec![
                param!("centerX", "Center X", 0.5, 0, 1),
                param!("centerY", "Center Y", 0.5, 0, 1),
                param!("radius", "Radius", 0.42, 0.05, 1),
                param!("rotation", "Rotation", 90, 0, 720),
                param!(List "direction", "Direction", 0, ["Clockwise", "Counterclockwise"]),
                param!(List "easing", "Easing", 3, ["Linear", "Ease in", "Ease out", "Ease in & out"]),
            ]),
        TransitionEntry::new(ids::WIPE, "Wipe", wipe)
            .category("Wipe")
            .icon("arrow-right-left")
            .describe("Sweep the incoming clip in from the left.")
            .params(vec![param!("soft", "Soft edge", 0.1, 0, 0.5)]),
    ]
}

fn cross_dissolve(a: &Frame, b: &Frame, t: f64, _: &ParamSet) -> Frame {
    lerp_frames(a, b, weight256(t))
}

fn curtain(a: &Frame, b: &Frame, t: f64, p: &ParamSet) -> Frame {
    let panels = p.int("panels").max(1) as f64;
    let soft = p.number("soft");
    let w = a.width;
    let panel_w = w as f64 / panels;
    let edge = (soft * panel_w).max(0.5);
    let weights: Vec<u32> = (0..w)
        .map(|x| {
            let panel = (x as f64 / panel_w) as i64;
            let local = x as f64 - panel as f64 * panel_w;
            let sweep = if panel % 2 == 0 { -1.0 } else { 1.0 };
            // sweep past each panel's far edge by the edge width so the soft band fully exits
            let boundary = panel_w / 2.0 + sweep * (panel_w / 2.0 + edge) * (2.0 * t - 1.0);
            weight256((sweep * (boundary - local) / (2.0 * edge) + 0.5).clamp(0.0, 1.0))
        })
        .collect();
    column_blend(a, b, &weights)
}

fn fade_to_black(a: &Frame, b: &Frame, t: f64, _: &ParamSet) -> Frame {
    // 1 at both ends, 0 in the middle
    scale_frame(if t < 0.5 { a } else { b }, weight256((2.0 * t - 1.0).abs()))
}

fn iris(a: &Frame, b: &Frame, t: f64, p: &ParamSet) -> Frame {
    let (w, h) = (a.width, a.height);
    let (cx, cy) = (w as f64 / 2.0, h as f64 / 2.0);
    let max_r = (cx * cx + cy * cy).sqrt();
    let edge = (p.number("soft") * max_r).max(0.5);
    // the soft band travels from -edge to past the corners, so both ends are clean
    let radius = t * (max_r + 2.0 * edge) - edge;
    let stride = a.stride();
    render_rows(a, |y, row| {
        let dy = y as f64 - cy;
        let base = y * stride;
        for x in 0..w {
            let dist = ((x as f64 - cx).powi(2) + dy * dy).sqrt();
            let wgt = weight256(((radius - dist) / (2.0 * edge) + 0.5).clamp(0.0, 1.0));
            let i = base + x * 4;
            lerp_bytes(&mut row[x * 4..x * 4 + 4], &a.pixels[i..i + 4], &b.pixels[i..i + 4], wgt);
        }
    })
}

const BLACK: [u8; 4] = [0, 0, 0, 255];

fn push(a: &Frame, b: &Frame, t: f64, p: &ParamSet) -> Frame {
    let dir = p.choice("direction");
    let (w, h) = (a.width as i64, a.height as i64);
    let span = if dir <= 1 { w } else { h };
    let shift = (t * span as f64).round_ties_even() as i64;
    let back = span - shift;
    let stride = a.stride();
    render_rows(a, |y, row| {
        let y = y as i64;
        if dir <= 1 {
            // incoming first, then outgoing, black where neither reaches
            let (b_off, a_off) = if dir == 0 { (back, shift) } else { (-back, -shift) };
            for x in 0..w {
                let o = &mut row[x as usize * 4..x as usize * 4 + 4];
                let (bx, ax) = (x + b_off, x + a_off);
                if (0..w).contains(&bx) {
                    let i = (y * w + bx) as usize * 4;
                    o.copy_from_slice(&b.pixels[i..i + 4]);
                } else if (0..w).contains(&ax) {
                    let i = (y * w + ax) as usize * 4;
                    o.copy_from_slice(&a.pixels[i..i + 4]);
                } else {
                    o.copy_from_slice(&BLACK);
                }
            }
            return;
        }
        // vertical pushes move whole rows
        let by = if dir == 2 { y + back } else { y - back };
        let ay = if dir == 2 { y + shift } else { y - shift };
        if (0..h).contains(&by) {
            row.copy_from_slice(&b.pixels[by as usize * stride..][..stride]);
        } else if (0..h).contains(&ay) {
            row.copy_from_slice(&a.pixels[ay as usize * stride..][..stride]);
        } else {
            row.chunks_exact_mut(4).for_each(|px| px.copy_from_slice(&BLACK));
        }
    })
}

fn slide(a: &Frame, b: &Frame, t: f64, p: &ParamSet) -> Frame {
    let dir = p.choice("direction");
    let (w, h) = (a.width as i64, a.height as i64);
    let r = |v: f64| v.round_ties_even() as i64;
    // the incoming edge sits off the frame and moves to 0: left/up negative, right/down positive
    let off_x = match dir {
        0 => -r((1.0 - t) * w as f64),
        1 => r((1.0 - t) * w as f64),
        _ => 0,
    };
    let off_y = match dir {
        2 => -r((1.0 - t) * h as f64),
        3 => r((1.0 - t) * h as f64),
        _ => 0,
    };
    let stride = a.stride();
    render_rows(a, |y, row| {
        let src_a = &a.pixels[y * stride..][..stride];
        let by = y as i64 - off_y;
        if !(0..h).contains(&by) {
            row.copy_from_slice(src_a);
            return;
        }
        let (x0, x1) = (off_x.clamp(0, w) as usize, (off_x + w).clamp(0, w) as usize);
        row[..x0 * 4].copy_from_slice(&src_a[..x0 * 4]);
        let from = by as usize * stride + (x0 as i64 - off_x) as usize * 4;
        row[x0 * 4..x1 * 4].copy_from_slice(&b.pixels[from..from + (x1 - x0) * 4]);
        row[x1 * 4..].copy_from_slice(&src_a[x1 * 4..]);
    })
}

fn swirl(a: &Frame, b: &Frame, t: f64, p: &ParamSet) -> Frame {
    if t <= 0.0 {
        return a.clone();
    }
    if t >= 1.0 {
        return b.clone();
    }
    let rotation = p.number("rotation").to_radians();
    let direction = if p.choice("direction") == 1 { -1.0 } else { 1.0 };
    let e = match p.choice("easing") {
        1 => t * t,
        2 => 1.0 - (1.0 - t) * (1.0 - t),
        3 => {
            if t < 0.5 {
                2.0 * t * t
            } else {
                1.0 - (-2.0 * t + 2.0).powi(2) / 2.0
            }
        }
        _ => t,
    };
    let (w, h) = (a.width, a.height);
    let (cx, cy) = (p.number("centerX") * w as f64, p.number("centerY") * h as f64);
    let radius = p.number("radius") * w.min(h) as f64;
    let weight = weight256(e);
    let stride = a.stride();
    render_rows(a, |y, row| {
        let dy = y as f64 + 0.5 - cy;
        let base = y * stride;
        for x in 0..w {
            let i = base + x * 4;
            let dx = x as f64 + 0.5 - cx;
            let dist = (dx * dx + dy * dy).sqrt();
            let o = &mut row[x * 4..x * 4 + 4];
            if dist >= radius {
                // outside the circle this is a plain dissolve
                lerp_bytes(o, &a.pixels[i..i + 4], &b.pixels[i..i + 4], weight);
                continue;
            }
            let falloff = (1.0 - dist / radius).powi(2);
            // the outgoing clip winds up as the transition goes on; the incoming unwinds
            let out = sample_turned(a, cx, cy, dx, dy, direction * rotation * e * falloff);
            let inc = sample_turned(b, cx, cy, dx, dy, -direction * rotation * (1.0 - e) * falloff);
            for c in 0..3 {
                o[c] = (out[c] + (inc[c] - out[c]) * e).round_ties_even() as u8;
            }
            o[3] = 255;
        }
    })
}

fn sample_turned(f: &Frame, cx: f64, cy: f64, dx: f64, dy: f64, angle: f64) -> [f64; 3] {
    let (sin, cos) = angle.sin_cos();
    let sx = cx + dx * cos - dy * sin - 0.5;
    let sy = cy + dx * sin + dy * cos - 0.5;
    let mut acc = [0.0; 3];
    crate::frame::sample_rgb(f, sx, sy, 1.0, &mut acc);
    acc
}

fn wipe(a: &Frame, b: &Frame, t: f64, p: &ParamSet) -> Frame {
    let w = a.width;
    let edge = p.number("soft") * w as f64;
    let incoming_cols = (t * w as f64).round_ties_even() as usize;
    // the soft band is centered on the boundary, which travels from -edge to w+edge so both ends are clean
    let boundary = t * (w as f64 + 2.0 * edge) - edge;
    let weights: Vec<u32> = (0..w)
        .map(|x| {
            let mix = if edge <= 0.5 {
                if x < incoming_cols { 1.0 } else { 0.0 }
            } else {
                ((boundary - x as f64) / (2.0 * edge) + 0.5).clamp(0.0, 1.0)
            };
            weight256(mix)
        })
        .collect();
    column_blend(a, b, &weights)
}
