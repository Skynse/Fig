//! The colour and geometry effects.

use super::{EffectEntry, ids};
use crate::ParamSet;
use crate::frame::{Frame, map_color, remap, render_rows, rows_mut, to_byte};
use crate::param;
use fig_timeline::{EffectInstance, ParamMap};

pub(super) fn entries() -> Vec<EffectEntry> {
    vec![
        EffectEntry::builtin(
            ids::BRIGHTNESS,
            "Brightness",
            "Color",
            "sun",
            "Lift or crush midtones.",
            vec![param!("amount", "Amount", 0.15, -1, 1)],
            brightness,
        ),
        EffectEntry::builtin(
            ids::CONTRAST,
            "Contrast",
            "Color",
            "contrast",
            "Adjust contrast around mid-gray.",
            vec![param!("amount", "Amount", 1, 0, 2)],
            contrast,
        ),
        EffectEntry::builtin(
            ids::GRAYSCALE,
            "Grayscale",
            "Color",
            "contrast",
            "Desaturate to monochrome.",
            vec![param!("amount", "Amount", 1, 0, 1)],
            grayscale,
        ),
        EffectEntry::builtin(
            ids::HUE,
            "Hue",
            "Color",
            "rotate-ccw-clock",
            "Rotate the hue of the image.",
            vec![param!("degrees", "Degrees", 0, 0, 360)],
            hue,
        ),
        EffectEntry::builtin(
            ids::INVERT,
            "Invert",
            "Color",
            "circle-slash-2",
            "Invert the image colors.",
            vec![param!("amount", "Amount", 1, 0, 1)],
            invert,
        ),
        EffectEntry::builtin(
            ids::SATURATION,
            "Saturation",
            "Color",
            "droplet",
            "Adjust color saturation.",
            vec![param!("amount", "Amount", 1, 0, 2)],
            saturation,
        ),
        EffectEntry::builtin(
            ids::SEPIA,
            "Sepia",
            "Color",
            "image-down",
            "Warm, brown-toned monochrome.",
            vec![param!("amount", "Amount", 1, 0, 1)],
            sepia,
        ),
        EffectEntry::builtin(
            ids::TINT,
            "Tint",
            "Color",
            "palette",
            "Tint the image toward a color.",
            vec![
                param!(Color "color", "Color", 0xFF0080FFu32),
                param!("strength", "Strength", 0.5, 0, 1),
                param!(Bool "preserve_luma", "Preserve luma", 0),
            ],
            tint,
        ),
        EffectEntry::builtin(
            ids::POSTERIZE,
            "Posterize",
            "Color",
            "sliders-horizontal",
            "Quantize colors to fewer levels.",
            vec![param!(Int "levels", "Levels", 4, 2, 32)],
            posterize,
        ),
        EffectEntry::builtin(
            ids::FLIP,
            "Flip",
            "Stylize",
            "flip-horizontal-2",
            "Mirror the image.",
            vec![param!(List "axis", "Axis", 0, ["Horizontal", "Vertical", "Both"])],
            flip,
        ),
        EffectEntry::builtin(
            ids::PIXELATE,
            "Pixelate",
            "Stylize",
            "grid-3x3",
            "Mosaic into blocks.",
            vec![param!(Int "block", "Block size", 8, 1, 64)],
            pixelate,
        ),
        EffectEntry::builtin(
            ids::VIGNETTE,
            "Vignette",
            "Stylize",
            "focus",
            "Darken the frame edges.",
            vec![param!("strength", "Strength", 0.35, 0, 1), param!(Color "color", "Color", 0xFF000000u32)],
            vignette,
        ),
        EffectEntry::builtin(
            ids::SHARPEN,
            "Sharpen",
            "Stylize",
            "gauge",
            "Increase local contrast at edges.",
            vec![param!("amount", "Amount", 0.5, 0, 2)],
            sharpen,
        ),
        EffectEntry::builtin(
            ids::CROP_WITH_PADDING,
            "Crop with Padding",
            "Stylize",
            "crop",
            "Crop the edges and pad the freed space with a solid color.",
            vec![
                param!("left", "Crop left", 0, 0, 0.5),
                param!("right", "Crop right", 0, 0, 0.5),
                param!("top", "Crop top", 0, 0, 0.5),
                param!("bottom", "Crop bottom", 0, 0, 0.5),
                param!(Color "color", "Padding color", 0xFF000000u32),
            ],
            crop_with_padding,
        ),
        EffectEntry::builtin(
            ids::TRANSFORM,
            "Transform",
            "Motion",
            "focus",
            "Zoom, pan and rotate the picture. Keyframe it to animate.",
            transform_params(),
            transform,
        ),
        EffectEntry::builtin(
            ids::CHROMATIC_ABERRATION,
            "Chromatic Aberration",
            "Stylize",
            "sun",
            "Split the red and blue channels apart like a lens fringe.",
            vec![
                param!(Bool "radial", "Radial", 0),
                param!("x_offset", "X Offset", 0.01, -0.05, 0.05),
                param!("y_offset", "Y Offset", 0.01, -0.05, 0.05),
            ],
            chromatic_aberration,
        ),
    ]
}

fn brightness(f: &Frame, p: &ParamSet, _: f64) -> Option<Frame> {
    let amount = p.number("amount");
    if amount.abs() < 1e-6 {
        return None;
    }
    let delta = (amount * 255.0).round_ties_even() as i32;
    let stride = f.stride();
    Some(render_rows(f, |y, row| {
        let s = &f.pixels[y * stride..(y + 1) * stride];
        for (o, i) in row.chunks_exact_mut(4).zip(s.chunks_exact(4)) {
            for c in 0..3 {
                o[c] = (i[c] as i32 + delta).clamp(0, 255) as u8;
            }
            o[3] = i[3];
        }
    }))
}

fn contrast(f: &Frame, p: &ParamSet, _: f64) -> Option<Frame> {
    let a = p.number("amount");
    if (a - 1.0).abs() < 1e-6 {
        return None;
    }
    Some(map_color(f, |r, g, b| ((r - 128.0) * a + 128.0, (g - 128.0) * a + 128.0, (b - 128.0) * a + 128.0)))
}

fn grayscale(f: &Frame, p: &ParamSet, _: f64) -> Option<Frame> {
    let a = p.number("amount");
    if a < 1e-6 {
        return None;
    }
    Some(map_color(f, |r, g, b| {
        // Rec. 601 luma, truncated
        let y = ((0.299 * r + 0.587 * g + 0.114 * b) as i32).clamp(0, 255) as f64;
        if a >= 1.0 { (y, y, y) } else { (r + (y - r) * a, g + (y - g) * a, b + (y - b) * a) }
    }))
}

fn hue(f: &Frame, p: &ParamSet, _: f64) -> Option<Frame> {
    let degrees = (p.number("degrees") % 360.0 + 360.0) % 360.0;
    if !(1e-6..=359.999).contains(&degrees) {
        return None;
    }
    let rad = degrees.to_radians();
    let (cos, sin) = (rad.cos(), rad.sin());
    let m = [
        [0.213 + cos * 0.787 - sin * 0.213, 0.715 - cos * 0.715 - sin * 0.715, 0.072 - cos * 0.072 + sin * 0.928],
        [0.213 - cos * 0.213 + sin * 0.143, 0.715 + cos * 0.285 + sin * 0.140, 0.072 - cos * 0.072 - sin * 0.283],
        [0.213 - cos * 0.213 - sin * 0.787, 0.715 - cos * 0.715 + sin * 0.715, 0.072 + cos * 0.928 + sin * 0.072],
    ];
    Some(map_color(f, move |r, g, b| {
        (
            m[0][0] * r + m[0][1] * g + m[0][2] * b,
            m[1][0] * r + m[1][1] * g + m[1][2] * b,
            m[2][0] * r + m[2][1] * g + m[2][2] * b,
        )
    }))
}

fn invert(f: &Frame, p: &ParamSet, _: f64) -> Option<Frame> {
    let a = p.number("amount");
    if a < 1e-6 {
        return None;
    }
    if a < 0.9999 {
        return Some(map_color(f, |r, g, b| {
            (r + (255.0 - 2.0 * r) * a, g + (255.0 - 2.0 * g) * a, b + (255.0 - 2.0 * b) * a)
        }));
    }
    let stride = f.stride();
    Some(render_rows(f, |y, row| {
        let s = &f.pixels[y * stride..(y + 1) * stride];
        for (o, i) in row.chunks_exact_mut(4).zip(s.chunks_exact(4)) {
            o[0] = 255 - i[0];
            o[1] = 255 - i[1];
            o[2] = 255 - i[2];
            o[3] = i[3];
        }
    }))
}

fn saturation(f: &Frame, p: &ParamSet, _: f64) -> Option<Frame> {
    let a = p.number("amount");
    if (a - 1.0).abs() < 1e-6 {
        return None;
    }
    Some(map_color(f, |r, g, b| {
        let l = 0.299 * r + 0.587 * g + 0.114 * b;
        (l + (r - l) * a, l + (g - l) * a, l + (b - l) * a)
    }))
}

fn sepia(f: &Frame, p: &ParamSet, _: f64) -> Option<Frame> {
    let a = p.number("amount");
    if a < 1e-6 {
        return None;
    }
    Some(map_color(f, |r, g, b| {
        let sr = 0.393 * r + 0.769 * g + 0.189 * b;
        let sg = 0.349 * r + 0.686 * g + 0.168 * b;
        let sb = 0.272 * r + 0.534 * g + 0.131 * b;
        (r + (sr - r) * a, g + (sg - g) * a, b + (sb - b) * a)
    }))
}

fn tint(f: &Frame, p: &ParamSet, _: f64) -> Option<Frame> {
    let color = p.color("color");
    let strength = p.number("strength");
    if strength < 1e-6 {
        return None;
    }
    let preserve = p.bool("preserve_luma");
    let (tr, tg, tb) = (((color >> 16) & 0xFF) as f64, ((color >> 8) & 0xFF) as f64, (color & 0xFF) as f64);
    Some(map_color(f, move |r, g, b| {
        let mut nr = (r + (tr - r) * strength).round_ties_even();
        let mut ng = (g + (tg - g) * strength).round_ties_even();
        let mut nb = (b + (tb - b) * strength).round_ties_even();
        if preserve {
            let luma_in = 0.299 * r + 0.587 * g + 0.114 * b;
            let luma_out = 0.299 * nr + 0.587 * ng + 0.114 * nb;
            if luma_out > 1e-6 {
                let ratio = (luma_in / luma_out).clamp(0.0, 1.15);
                (nr, ng, nb) = (nr * ratio, ng * ratio, nb * ratio);
            }
        }
        (nr, ng, nb)
    }))
}

fn posterize(f: &Frame, p: &ParamSet, _: f64) -> Option<Frame> {
    let step = 255.0 / (p.int("levels") - 1) as f64;
    let q = move |v: f64| (v / step).round_ties_even() * step;
    Some(map_color(f, move |r, g, b| (q(r), q(g), q(b))))
}

fn flip(f: &Frame, p: &ParamSet, _: f64) -> Option<Frame> {
    let axis = p.choice("axis");
    let (fx, fy) = (axis == 0 || axis == 2, axis == 1 || axis == 2);
    if !fx && !fy {
        return None;
    }
    let (w, h) = (f.width, f.height);
    let columns: Vec<usize> = (0..w).map(|x| if fx { w - 1 - x } else { x }).collect();
    Some(remap(f, &columns, move |y| if fy { h - 1 - y } else { y }))
}

fn pixelate(f: &Frame, p: &ParamSet, _: f64) -> Option<Frame> {
    let block = p.int("block").max(1) as usize;
    if block == 1 {
        return None;
    }
    let columns: Vec<usize> = (0..f.width).map(|x| x / block * block).collect();
    Some(remap(f, &columns, move |y| y / block * block))
}

fn vignette(f: &Frame, p: &ParamSet, _: f64) -> Option<Frame> {
    let strength = p.number("strength");
    if strength < 1e-6 {
        return None;
    }
    let color = p.color("color");
    let v = [(color & 0xFF) as f64, ((color >> 8) & 0xFF) as f64, ((color >> 16) & 0xFF) as f64];
    let (w, h) = (f.width, f.height);
    let (cx, cy) = (w as f64 / 2.0, h as f64 / 2.0);
    let max_dist = (cx * cx + cy * cy).sqrt();
    let stride = f.stride();
    Some(render_rows(f, |y, row| {
        let dy = (y as f64 - cy) / max_dist;
        let s = &f.pixels[y * stride..(y + 1) * stride];
        for (x, (o, i)) in row.chunks_exact_mut(4).zip(s.chunks_exact(4)).enumerate() {
            let dx = (x as f64 - cx) / max_dist;
            let t = (dx * dx + dy * dy).min(1.0);
            let k = t * t * strength;
            for c in 0..3 {
                o[c] = to_byte(i[c] as f64 + (v[c] - i[c] as f64) * k);
            }
            o[3] = i[3];
        }
    }))
}

fn sharpen(f: &Frame, p: &ParamSet, _: f64) -> Option<Frame> {
    let amount = p.number("amount");
    if amount < 1e-6 {
        return None;
    }
    let (w, h) = (f.width, f.height);
    let src = &f.pixels;
    Some(render_rows(f, |y, row| {
        let (up, down) = (y.saturating_sub(1), (y + 1).min(h - 1));
        for x in 0..w {
            let (left, right) = (x.saturating_sub(1), (x + 1).min(w - 1));
            let i = (y * w + x) * 4;
            for c in 0..3 {
                let at = |yy: usize, xx: usize| src[(yy * w + xx) * 4 + c] as i32;
                let center = src[i + c] as i32;
                let lap = center * 4 - at(up, x) - at(down, x) - at(y, left) - at(y, right);
                row[x * 4 + c] = ((center as f64 + lap as f64 * amount).round_ties_even() as i32).clamp(0, 255) as u8;
            }
            row[x * 4 + 3] = src[i + 3];
        }
    }))
}

fn crop_with_padding(f: &Frame, p: &ParamSet, _: f64) -> Option<Frame> {
    let (w, h) = (f.width as i64, f.height as i64);
    let r = |v: f64| v.round_ties_even() as i64;
    // in picture coordinates with row 0 at the bottom, as the effect was first written
    let x0 = r(w as f64 * p.number("left"));
    let x1 = (w - r(w as f64 * p.number("right"))).max(x0);
    let y0 = r(h as f64 * p.number("bottom"));
    let y1 = (h - r(h as f64 * p.number("top"))).max(y0);
    let color = p.color("color");
    let pad = [(color & 0xFF) as u8, ((color >> 8) & 0xFF) as u8, ((color >> 16) & 0xFF) as u8, 255];
    let (cw, ch) = (x1 - x0, y1 - y0);
    if cw >= w && ch >= h {
        return None;
    }
    let mut out = Frame::new(f.width, f.height);
    if cw <= 0 || ch <= 0 {
        out.pixels.chunks_exact_mut(4).for_each(|px| px.copy_from_slice(&pad));
        return Some(out);
    }
    let scale = (w as f64 / cw as f64).min(h as f64 / ch as f64);
    let (content_w, content_h) = (cw as f64 * scale, ch as f64 * scale);
    let (off_x, off_y) = ((w as f64 - content_w) / 2.0, (h as f64 - content_h) / 2.0);
    let stride = out.stride();
    rows_mut(&mut out.pixels, stride, |row_index, row| {
        let y = (h - 1 - row_index as i64) as f64;
        for x in 0..w {
            let o = &mut row[x as usize * 4..x as usize * 4 + 4];
            let xf = x as f64;
            if xf >= off_x && xf < off_x + content_w && y >= off_y && y < off_y + content_h {
                let sx = (x0 + ((xf - off_x) / scale) as i64).clamp(0, w - 1);
                let sy = (y0 + ((y - off_y) / scale) as i64).clamp(0, h - 1);
                let si = ((h - 1 - sy) * w * 4 + sx * 4) as usize;
                o.copy_from_slice(&f.pixels[si..si + 4]);
            } else {
                o.copy_from_slice(&pad);
            }
        }
    });
    Some(out)
}

fn transform_params() -> Vec<fig_timeline::ParamDef> {
    vec![
        param!("centerX", "Center X", 0.5, 0, 1),
        param!("centerY", "Center Y", 0.5, 0, 1),
        param!("zoom", "Zoom", 1, 1, 8),
        param!("rotation", "Rotation", 0, -180, 180),
    ]
}

/// Where the Transform effect's camera looks (0..1 from the top-left), how close it is, and its turn.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct TransformView {
    pub center_x: f64,
    pub center_y: f64,
    pub zoom: f64,
    pub rotation_degrees: f64,
}

impl TransformView {
    pub fn is_identity(&self) -> bool {
        self.zoom <= 1.0001
            && self.rotation_degrees.abs() < 0.01
            && (self.center_x - 0.5).abs() < 1e-4
            && (self.center_y - 0.5).abs() < 1e-4
    }
}

/// The camera described by a Transform effect's resolved parameters, kept inside the picture.
pub fn transform_view(values: &ParamMap) -> TransformView {
    let schema = transform_params();
    view_from(&ParamSet::new(&schema, values))
}

/// The camera of a Transform effect at a time, keyframes included.
pub fn transform_view_at(effect: &EffectInstance, local_t: f64) -> TransformView {
    transform_view(&super::resolve_params(effect, local_t))
}

fn view_from(p: &ParamSet) -> TransformView {
    let zoom = p.number("zoom");
    let half = 0.5 / zoom;
    TransformView {
        center_x: p.number("centerX").clamp(half, 1.0 - half),
        center_y: p.number("centerY").clamp(half, 1.0 - half),
        zoom,
        rotation_degrees: p.number("rotation"),
    }
}

fn transform(f: &Frame, p: &ParamSet, _: f64) -> Option<Frame> {
    let view = view_from(p);
    if view.is_identity() {
        return None;
    }
    let (w, h) = (f.width, f.height);
    let rad = view.rotation_degrees.to_radians();
    let (cos, sin) = (rad.cos(), rad.sin());
    let (cpx, cpy) = (view.center_x * w as f64, view.center_y * h as f64);
    let inv = 1.0 / view.zoom;
    let src = &f.pixels;
    Some(render_rows(f, |y, row| {
        let dy = y as f64 + 0.5 - h as f64 / 2.0;
        for x in 0..w {
            let dx = x as f64 + 0.5 - w as f64 / 2.0;
            let sx = (cpx + (dx * cos - dy * sin) * inv - 0.5).clamp(0.0, (w - 1) as f64);
            let sy = (cpy + (dx * sin + dy * cos) * inv - 0.5).clamp(0.0, (h - 1) as f64);
            let (x0, y0) = (sx as usize, sy as usize);
            let (x1, y1) = ((x0 + 1).min(w - 1), (y0 + 1).min(h - 1));
            let (fx, fy) = (sx - x0 as f64, sy - y0 as f64);
            for c in 0..4 {
                let at = |yy: usize, xx: usize| src[(yy * w + xx) * 4 + c] as f64;
                let top = at(y0, x0) * (1.0 - fx) + at(y0, x1) * fx;
                let bottom = at(y1, x0) * (1.0 - fx) + at(y1, x1) * fx;
                row[x * 4 + c] = (top * (1.0 - fy) + bottom * fy + 0.5) as u8;
            }
        }
    }))
}

fn chromatic_aberration(f: &Frame, p: &ParamSet, _: f64) -> Option<Frame> {
    let (xo, yo) = (p.number("x_offset"), p.number("y_offset"));
    if xo.abs() < 1e-6 && yo.abs() < 1e-6 {
        return None;
    }
    let (w, h) = (f.width, f.height);
    // both axes are scaled by the width, so equal x and y offsets give an equal-length diagonal shift
    let (dx, dy) = (xo * w as f64, yo * w as f64);
    let src = &f.pixels;
    let clamp = |v: f64, max: usize| (v.round_ties_even() as i64).clamp(0, max as i64 - 1) as usize;
    if p.bool("radial") {
        let (cx, cy) = ((w as f64 - 1.0) / 2.0, (h as f64 - 1.0) / 2.0);
        let max_dist = (cx * cx + cy * cy).sqrt().max(1.0);
        return Some(render_rows(f, |row, out| {
            let oy = dy * (row as f64 - cy) / max_dist;
            for col in 0..w {
                let ox = dx * (col as f64 - cx) / max_dist;
                let (xr, yr) = (clamp(col as f64 - ox, w), clamp(row as f64 - oy, h));
                let (xb, yb) = (clamp(col as f64 + ox, w), clamp(row as f64 + oy, h));
                let i = (row * w + col) * 4;
                out[col * 4] = src[(yb * w + xb) * 4];
                out[col * 4 + 1] = src[i + 1];
                out[col * 4 + 2] = src[(yr * w + xr) * 4 + 2];
                out[col * 4 + 3] = src[i + 3];
            }
        }));
    }
    let (sx, sy) = (dx.round_ties_even() as i64, dy.round_ties_even() as i64);
    let red_x: Vec<usize> = (0..w as i64).map(|c| (c - sx).clamp(0, w as i64 - 1) as usize).collect();
    let blue_x: Vec<usize> = (0..w as i64).map(|c| (c + sx).clamp(0, w as i64 - 1) as usize).collect();
    Some(render_rows(f, |row, out| {
        let red_row = (row as i64 - sy).clamp(0, h as i64 - 1) as usize * w;
        let blue_row = (row as i64 + sy).clamp(0, h as i64 - 1) as usize * w;
        for col in 0..w {
            let i = (row * w + col) * 4;
            out[col * 4] = src[(blue_row + blue_x[col]) * 4];
            out[col * 4 + 1] = src[i + 1];
            out[col * 4 + 2] = src[(red_row + red_x[col]) * 4 + 2];
            out[col * 4 + 3] = src[i + 3];
        }
    }))
}
