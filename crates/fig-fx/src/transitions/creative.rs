//! The creative transitions, all sharing one eased progress, with clean endpoints.

use super::TransitionEntry;
use crate::ParamSet;
use crate::frame::{Frame, blur, mirror, mix, noise, render_rows, rgb, sample, to_byte, warp};
use crate::param;

pub(super) fn entries() -> Vec<TransitionEntry> {
    vec![
        TransitionEntry::new("white-flash", "White Flash", flash)
            .category("Light")
            .icon("zap")
            .duration(0.35)
            .preview_at(0.35)
            .describe("A bright flash bridges the cut.")
            .params(vec![param!("amount", "Flash intensity", 1, 0, 1)]),
        TransitionEntry::new("blur-dissolve", "Blur Dissolve", blur_dissolve)
            .category("Blur")
            .icon("droplets")
            .duration(0.6)
            .describe("Defocus both shots into a soft dissolve.")
            .params(vec![param!("amount", "Blur strength", 0.5, 0, 1)]),
        TransitionEntry::new("zoom-in", "Zoom In", zoom_in)
            .category("Camera")
            .icon("zoom-in")
            .duration(0.45)
            .describe("Push through the cut into the next shot.")
            .params(vec![param!("amount", "Zoom strength", 0.55, 0, 1)]),
        TransitionEntry::new("zoom-out", "Zoom Out", zoom_out)
            .category("Camera")
            .icon("zoom-out")
            .duration(0.45)
            .describe("Pull back through the cut to reveal the next shot.")
            .params(vec![param!("amount", "Zoom strength", 0.55, 0, 1)]),
        TransitionEntry::new("whip-pan", "Whip Pan", whip)
            .category("Camera")
            .icon("move-horizontal")
            .duration(0.3)
            .describe("Wrap the image sideways across a fast camera pan.")
            .params(vec![param!(List "direction", "Direction", 0, ["Left", "Right"])]),
        TransitionEntry::new("spin", "Spin", spin)
            .category("Camera")
            .icon("rotate-cw")
            .duration(0.55)
            .describe("Rotate and zoom both shots through the cut.")
            .params(vec![
                param!("amount", "Rotation strength", 0.5, 0, 1),
                param!(List "direction", "Direction", 0, ["Clockwise", "Counterclockwise"]),
            ]),
        TransitionEntry::new("glitch-cut", "Glitch Cut", glitch)
            .category("Glitch")
            .icon("zap")
            .duration(0.3)
            .describe("Displaced horizontal strips break up the cut.")
            .params(vec![param!("amount", "Displacement", 0.65, 0, 1)]),
        TransitionEntry::new("pixel-dissolve", "Pixel Dissolve", pixel)
            .category("Glitch")
            .icon("grid-3x3")
            .duration(0.6)
            .describe("Small tiles reveal the next shot in a scattered pattern.")
            .params(vec![
                param!(Int "tiles", "Tile columns", 24, 4, 100),
                param!("softness", "Edge softness", 0.08, 0.001, 0.4),
            ]),
        TransitionEntry::new("luma-dissolve", "Luma Dissolve", luma)
            .category("Light")
            .icon("sun")
            .duration(0.7)
            .describe("Highlights reveal the incoming shot before shadows.")
            .params(vec![param!("softness", "Edge softness", 0.12, 0.001, 0.4)]),
        TransitionEntry::new("radial-wipe", "Clock Wipe", radial)
            .category("Wipe")
            .icon("clock")
            .duration(0.7)
            .describe("Sweep around the image center like a clock hand.")
            .params(vec![
                param!("softness", "Edge softness", 0.04, 0.001, 0.4),
                param!(List "direction", "Direction", 0, ["Clockwise", "Counterclockwise"]),
            ]),
        TransitionEntry::new("blinds", "Venetian Blinds", blinds)
            .category("Wipe")
            .icon("columns-2")
            .duration(0.65)
            .describe("Open repeating vertical strips onto the next shot.")
            .params(vec![
                param!(Int "stripes", "Strips", 10, 2, 40),
                param!("softness", "Edge softness", 0.08, 0.001, 0.4),
            ]),
        TransitionEntry::new("card-flip", "Card Flip", card_flip)
            .category("Camera")
            .icon("columns-2")
            .duration(0.6)
            .preview_at(0.35)
            .describe("Turn the picture edge-on, then unfold the next shot."),
    ]
}

/// The eased progress and the bump that peaks at the middle, or the endpoint itself at either end.
fn phase(a: &Frame, b: &Frame, t: f64) -> Result<(f64, f64), Frame> {
    if t <= 0.0 {
        return Err(a.clone());
    }
    if t >= 1.0 {
        return Err(b.clone());
    }
    Ok((t * t * (3.0 - 2.0 * t), (std::f64::consts::PI * t).sin()))
}

fn amount(p: &ParamSet) -> f64 {
    if p.declares("amount") { p.number("amount") } else { 0.0 }
}

fn direction(p: &ParamSet) -> f64 {
    if p.declares("direction") && p.choice("direction") == 1 { -1.0 } else { 1.0 }
}

fn flash(a: &Frame, b: &Frame, t: f64, p: &ParamSet) -> Frame {
    let (progress, peak) = match phase(a, b, t) {
        Ok(v) => v,
        Err(end) => return end,
    };
    let mixed = mix(a, b, |_, _| progress);
    let flash = peak.powi(6) * amount(p);
    let w = a.width;
    rgb(&mixed, |x, y, c| mixed.pixels[(y * w + x) * 4 + c] as f64 * (1.0 - flash) + 255.0 * flash)
}

fn blur_dissolve(a: &Frame, b: &Frame, t: f64, p: &ParamSet) -> Frame {
    let (progress, peak) = match phase(a, b, t) {
        Ok(v) => v,
        Err(end) => return end,
    };
    let mixed = mix(a, b, |_, _| progress);
    let radius = (peak * amount(p) * a.width.min(a.height) as f64 * 0.08).round_ties_even() as usize;
    if radius == 0 { mixed } else { blur(&mixed, radius) }
}

/// Both pictures warped (the incoming one by `map(u, v, true)`), then mixed by the progress.
fn warped(a: &Frame, b: &Frame, progress: f64, map: impl Fn(f64, f64, bool) -> (f64, f64) + Sync + Send) -> Frame {
    let wa = warp(a, |u, v| map(u, v, false));
    let wb = warp(b, |u, v| map(u, v, true));
    mix(&wa, &wb, |_, _| progress)
}

fn zoom(a: &Frame, b: &Frame, t: f64, p: &ParamSet, sign: f64) -> Frame {
    let (progress, _) = match phase(a, b, t) {
        Ok(v) => v,
        Err(end) => return end,
    };
    let amount = amount(p);
    warped(a, b, progress, |u, v, incoming| {
        let scale = (sign * amount * 1.5 * if incoming { 1.0 - progress } else { -progress }).exp();
        // the zoomed-out side is smaller than the frame: mirror it out to the edges
        (mirror(0.5 + (u - 0.5) * scale), mirror(0.5 + (v - 0.5) * scale))
    })
}

fn zoom_in(a: &Frame, b: &Frame, t: f64, p: &ParamSet) -> Frame {
    zoom(a, b, t, p, 1.0)
}

fn zoom_out(a: &Frame, b: &Frame, t: f64, p: &ParamSet) -> Frame {
    zoom(a, b, t, p, -1.0)
}

fn whip(a: &Frame, b: &Frame, t: f64, p: &ParamSet) -> Frame {
    let (progress, _) = match phase(a, b, t) {
        Ok(v) => v,
        Err(end) => return end,
    };
    let dir = direction(p);
    warped(a, b, progress, |u, v, incoming| {
        let wrap = u + dir * if incoming { 1.0 - progress } else { -progress };
        (wrap - wrap.floor(), v)
    })
}

fn spin(a: &Frame, b: &Frame, t: f64, p: &ParamSet) -> Frame {
    let (progress, peak) = match phase(a, b, t) {
        Ok(v) => v,
        Err(end) => return end,
    };
    let (amount, dir) = (amount(p), direction(p));
    let aspect = a.width as f64 / a.height as f64;
    warped(a, b, progress, |u, v, incoming| {
        let angle = dir * amount * std::f64::consts::PI * if incoming { 1.0 - progress } else { -progress };
        let (x, y) = ((u - 0.5) * aspect, v - 0.5);
        let zoom = 1.0 + peak * amount;
        let (sin, cos) = angle.sin_cos();
        (0.5 + (x * cos - y * sin) / aspect / zoom, 0.5 + (x * sin + y * cos) / zoom)
    })
}

fn glitch(a: &Frame, b: &Frame, t: f64, p: &ParamSet) -> Frame {
    let (progress, peak) = match phase(a, b, t) {
        Ok(v) => v,
        Err(end) => return end,
    };
    let amount = amount(p);
    // reproducible bands, so scrubbing and export match
    let seed = (t * 24.0) as i32;
    warped(a, b, progress, |u, v, _| {
        let band = (v * 24.0) as i32;
        (u + (noise(0, band, seed) - 0.5) * peak * amount * 0.35, v)
    })
}

fn card_flip(a: &Frame, b: &Frame, t: f64, _: &ParamSet) -> Frame {
    if let Err(end) = phase(a, b, t) {
        return end;
    }
    let source = if t < 0.5 { a } else { b };
    let width = (std::f64::consts::PI * t).cos().abs().max(0.001);
    let w = a.width;
    render_rows(source, |y, row| {
        for x in 0..w {
            let u = (x as f64 + 0.5) / w as f64;
            let inside = (u - 0.5).abs() <= width / 2.0;
            for c in 0..3 {
                row[x * 4 + c] = if inside {
                    to_byte(sample(source, (0.5 + (u - 0.5) / width) * w as f64 - 0.5, y as f64, c))
                } else {
                    0
                };
            }
            row[x * 4 + 3] = source.pixels[(y * w + x) * 4 + 3];
        }
    })
}

/// A reveal: each pixel turns over when the eased progress passes its threshold, softened by `softness`.
fn reveal(
    a: &Frame,
    b: &Frame,
    t: f64,
    p: &ParamSet,
    threshold: impl Fn(f64, f64, usize, usize) -> f64 + Sync + Send,
) -> Frame {
    let (progress, _) = match phase(a, b, t) {
        Ok(v) => v,
        Err(end) => return end,
    };
    let softness = p.number("softness");
    // extending the threshold range by the softness margin gives clean endpoints
    let reveal = progress * (1.0 + softness) - softness / 2.0;
    let (w, h) = (a.width as f64, a.height as f64);
    mix(a, b, |x, y| {
        let (u, v) = ((x as f64 + 0.5) / w, (y as f64 + 0.5) / h);
        (0.5 + (reveal - threshold(u, v, x, y)) / softness).clamp(0.0, 1.0)
    })
}

fn pixel(a: &Frame, b: &Frame, t: f64, p: &ParamSet) -> Frame {
    let columns = p.int("tiles");
    let rows = ((columns as f64 * a.height as f64 / a.width as f64).round_ties_even() as i32).max(1);
    reveal(a, b, t, p, |u, v, _, _| noise((u * columns as f64) as i32, (v * rows as f64) as i32, 17))
}

fn luma(a: &Frame, b: &Frame, t: f64, p: &ParamSet) -> Frame {
    let w = a.width;
    reveal(a, b, t, p, |_, _, x, y| {
        let i = (y * w + x) * 4;
        let px = &a.pixels;
        1.0 - (0.0722 * px[i] as f64 + 0.7152 * px[i + 1] as f64 + 0.2126 * px[i + 2] as f64) / 255.0
    })
}

fn radial(a: &Frame, b: &Frame, t: f64, p: &ParamSet) -> Frame {
    let dir = direction(p);
    let aspect = a.width as f64 / a.height as f64;
    reveal(a, b, t, p, |u, v, _, _| {
        let angle = ((v - 0.5).atan2((u - 0.5) * aspect) / std::f64::consts::TAU + 1.25) % 1.0;
        if dir < 0.0 { 1.0 - angle } else { angle }
    })
}

fn blinds(a: &Frame, b: &Frame, t: f64, p: &ParamSet) -> Frame {
    let stripes = p.number("stripes");
    reveal(a, b, t, p, |u, _, _, _| {
        let pos = u * stripes;
        pos - pos.floor()
    })
}
