//! The creative effects: blurs, light, retro, glitch and motion looks. All resolution independent and repeatable
//! (noise is seeded by time, so an export matches the preview).

use super::EffectEntry;
use crate::ParamSet;
use crate::frame::{Frame, blur, noise, rgb, sample, sample_rgb, shade, warp};
use crate::param;

pub(super) fn entries() -> Vec<EffectEntry> {
    vec![
        EffectEntry::builtin(
            "soft-blur",
            "Soft Blur",
            "Blur",
            "droplets",
            "Soften detail with a fast two-pass blur.",
            vec![param!("amount", "Radius", 0.012, 0, 0.08)],
            soft_blur,
        ),
        EffectEntry::builtin(
            "directional-blur",
            "Directional Blur",
            "Blur",
            "move-horizontal",
            "Smear the picture along an adjustable angle.",
            vec![param!("amount", "Distance", 0.025, 0, 0.15), param!("angle", "Angle", 0, -180, 180)],
            directional_blur,
        ),
        EffectEntry::builtin(
            "zoom-blur",
            "Zoom Blur",
            "Blur",
            "focus",
            "Streak details radially toward the center.",
            vec![param!("amount", "Strength", 0.15, 0, 0.7)],
            zoom_blur,
        ),
        EffectEntry::builtin(
            "glow",
            "Glow",
            "Light",
            "sun",
            "Bloom bright highlights into a soft halo.",
            vec![
                param!("amount", "Intensity", 0.6, 0, 2),
                param!("radius", "Spread", 0.025, 0.002, 0.1),
                param!("threshold", "Highlight threshold", 0.65, 0, 1),
            ],
            glow,
        ),
        EffectEntry::builtin(
            "film-grain",
            "Film Grain",
            "Retro",
            "film",
            "Animated monochrome grain with repeatable results on export.",
            vec![param!("amount", "Strength", 0.15, 0, 1), param!(Int "size", "Grain size", 1, 1, 6)],
            film_grain,
        ),
        EffectEntry::builtin(
            "vhs",
            "VHS Tape",
            "Retro",
            "tv",
            "Rolling tape wobble, scanlines, color bleed and analog noise.",
            vec![param!("amount", "Intensity", 0.45, 0, 1), param!("speed", "Tracking speed", 1, 0.1, 4)],
            vhs,
        ),
        EffectEntry::builtin(
            "digital-glitch",
            "Digital Glitch",
            "Glitch",
            "zap",
            "Animated horizontal slice displacement and RGB separation.",
            vec![param!("amount", "Intensity", 0.4, 0, 1), param!("speed", "Changes per second", 12, 1, 30)],
            digital_glitch,
        ),
        EffectEntry::builtin(
            "camera-shake",
            "Camera Shake",
            "Motion",
            "move",
            "Handheld-style animated movement with protective zoom.",
            vec![param!("amount", "Strength", 0.025, 0, 0.12), param!("speed", "Speed", 8, 0.5, 25)],
            camera_shake,
        ),
        EffectEntry::builtin(
            "pulse-zoom",
            "Pulse Zoom",
            "Motion",
            "focus",
            "Repeated camera pushes at an adjustable tempo.",
            vec![param!("amount", "Zoom strength", 0.12, 0, 0.8), param!("bpm", "Tempo (BPM)", 120, 30, 240)],
            pulse_zoom,
        ),
        EffectEntry::builtin(
            "strobe",
            "Strobe Flash",
            "Light",
            "zap",
            "Short white flashes or dark pulses at a controlled rate.",
            vec![
                param!("amount", "Intensity", 0.7, 0, 1),
                param!("frequency", "Flashes per second", 2, 0.25, 12),
                param!(Bool "dark", "Black flash", 0),
            ],
            strobe,
        ),
        EffectEntry::builtin(
            "lens-distortion",
            "Lens Distortion",
            "Distort",
            "aperture",
            "Barrel or pincushion distortion around the image center.",
            vec![param!("amount", "Distortion", 0.3, -0.6, 0.6)],
            lens_distortion,
        ),
        EffectEntry::builtin(
            "mirror",
            "Mirror",
            "Distort",
            "columns-2",
            "Reflect half the picture or create four mirrored quadrants.",
            vec![param!(List "axis", "Reflection", 0, ["Left to right", "Top to bottom", "Four quadrants"])],
            mirror_effect,
        ),
        EffectEntry::builtin(
            "light-leak",
            "Light Leak",
            "Light",
            "sunrise",
            "Animated warm film light spilling in from the frame edge.",
            vec![param!("amount", "Intensity", 0.5, 0, 1), param!("speed", "Drift speed", 0.5, 0, 3)],
            light_leak,
        ),
        EffectEntry::builtin(
            "duotone",
            "Duotone",
            "Color",
            "palette",
            "Map shadows and highlights to two chosen colors.",
            vec![
                param!("amount", "Blend", 1, 0, 1),
                param!(Color "shadows", "Shadows", 0xFF14284Du32),
                param!(Color "highlights", "Highlights", 0xFFFFC857u32),
            ],
            duotone,
        ),
    ]
}

fn px(f: &Frame, x: usize, y: usize, c: usize) -> f64 {
    f.pixels[(y * f.width + x) * 4 + c] as f64
}

fn soft_blur(f: &Frame, p: &ParamSet, _: f64) -> Option<Frame> {
    let amount = p.number("amount");
    if amount == 0.0 {
        return None;
    }
    let radius = ((amount * f.width.min(f.height) as f64).round_ties_even() as usize).max(1);
    Some(blur(f, radius))
}

fn directional_blur(f: &Frame, p: &ParamSet, _: f64) -> Option<Frame> {
    let distance = p.number("amount") * f.width as f64;
    if distance == 0.0 {
        return None;
    }
    let angle = p.number("angle").to_radians();
    let (sx, sy) = (angle.cos() * distance / 4.0, angle.sin() * distance / 4.0);
    Some(shade(f, |x, y, acc| {
        for k in -4..=4 {
            sample_rgb(f, x as f64 + sx * k as f64, y as f64 + sy * k as f64, 1.0 / 9.0, acc);
        }
    }))
}

fn zoom_blur(f: &Frame, p: &ParamSet, _: f64) -> Option<Frame> {
    let amount = p.number("amount");
    if amount == 0.0 {
        return None;
    }
    let (cx, cy) = ((f.width as f64 - 1.0) / 2.0, (f.height as f64 - 1.0) / 2.0);
    Some(shade(f, |x, y, acc| {
        for k in 0..10 {
            let scale = 1.0 - amount * k as f64 / 9.0;
            sample_rgb(f, cx + (x as f64 - cx) * scale, cy + (y as f64 - cy) * scale, 0.1, acc);
        }
    }))
}

fn glow(f: &Frame, p: &ParamSet, _: f64) -> Option<Frame> {
    let amount = p.number("amount");
    if amount == 0.0 {
        return None;
    }
    let threshold = p.number("threshold") * 255.0;
    let highlights = rgb(f, |x, y, c| (px(f, x, y, c) - threshold).max(0.0));
    let radius = ((p.number("radius") * f.width.min(f.height) as f64) as usize).max(1);
    let blurred = blur(&highlights, radius);
    Some(rgb(f, |x, y, c| px(f, x, y, c) + px(&blurred, x, y, c) * amount))
}

fn film_grain(f: &Frame, p: &ParamSet, t: f64) -> Option<Frame> {
    let amount = p.number("amount") * 100.0;
    if amount == 0.0 {
        return None;
    }
    let size = p.int("size").max(1) as usize;
    let seed = (t * 24.0).floor() as i32;
    Some(rgb(f, |x, y, c| px(f, x, y, c) + (noise((x / size) as i32, (y / size) as i32, seed) - 0.5) * amount))
}

fn vhs(f: &Frame, p: &ParamSet, t: f64) -> Option<Frame> {
    let (amount, speed) = (p.number("amount"), p.number("speed"));
    if amount == 0.0 {
        return None;
    }
    let seed = (t * 30.0).floor() as i32;
    let bleed = f.width as f64 * amount * 0.004;
    Some(shade(f, |x, y, acc| {
        let v = y as f64 / f.height as f64;
        let shift = (v * 35.0 + t * speed * 8.0).sin() * f.width as f64 * amount * 0.006;
        let scan = 1.0 - amount * 0.18 * (0.5 + 0.5 * (v * 360.0 * std::f64::consts::PI).cos());
        let n = (noise(x as i32, y as i32, seed) - 0.5) * amount * 30.0;
        for c in 0..3 {
            acc[c] = sample(f, x as f64 + shift + (c as f64 - 1.0) * bleed, y as f64, c) * scan + n;
        }
    }))
}

fn digital_glitch(f: &Frame, p: &ParamSet, t: f64) -> Option<Frame> {
    let amount = p.number("amount");
    if amount == 0.0 {
        return None;
    }
    let seed = (t * p.number("speed")).floor() as i32;
    let split = f.width as f64 * amount * 0.015;
    Some(shade(f, |x, y, acc| {
        let band = (y * 24 / f.height) as i32;
        let shift = if noise(0, band, seed) > 0.65 {
            (noise(1, band, seed) - 0.5) * f.width as f64 * amount * 0.25
        } else {
            0.0
        };
        for c in 0..3 {
            acc[c] = sample(f, x as f64 + shift + (c as f64 - 1.0) * split, y as f64, c);
        }
    }))
}

fn camera_shake(f: &Frame, p: &ParamSet, t: f64) -> Option<Frame> {
    let (amount, speed) = (p.number("amount"), p.number("speed"));
    if amount == 0.0 {
        return None;
    }
    // 6.28, not τ: the shake has always been tuned to it
    #[allow(clippy::approx_constant)]
    let dx = amount * ((t * speed * 6.28).sin() + 0.4 * (t * speed * 13.7).sin()) / 1.4;
    let dy = amount * ((t * speed * 8.1 + 1.0).sin() + 0.4 * (t * speed * 17.3).sin()) / 1.4;
    let zoom = 1.0 + amount * 5.0;
    Some(warp(f, |u, v| (0.5 + (u - 0.5) / zoom + dx, 0.5 + (v - 0.5) / zoom + dy)))
}

fn pulse_zoom(f: &Frame, p: &ParamSet, t: f64) -> Option<Frame> {
    let amount = p.number("amount");
    if amount == 0.0 {
        return None;
    }
    let pulse = (0.5 + 0.5 * (t * p.number("bpm") / 60.0 * std::f64::consts::TAU).cos()).powi(4);
    let zoom = 1.0 + amount * pulse;
    Some(warp(f, |u, v| (0.5 + (u - 0.5) / zoom, 0.5 + (v - 0.5) / zoom)))
}

fn strobe(f: &Frame, p: &ParamSet, t: f64) -> Option<Frame> {
    let phase = t * p.number("frequency");
    let flash = (1.0 - (phase - phase.floor()) / 0.2).max(0.0) * p.number("amount");
    if flash <= 0.0 {
        return None;
    }
    let target = if p.bool("dark") { 0.0 } else { 255.0 };
    Some(rgb(f, |x, y, c| px(f, x, y, c) * (1.0 - flash) + target * flash))
}

fn lens_distortion(f: &Frame, p: &ParamSet, _: f64) -> Option<Frame> {
    let amount = p.number("amount");
    if amount == 0.0 {
        return None;
    }
    // barrel distortion pushes samples past the edge; a compensating zoom keeps the farthest corner on the border
    let fit = if amount > 0.0 { 1.0 + amount * 2.0 } else { 1.0 };
    Some(warp(f, |u, v| {
        let (x, y) = (u - 0.5, v - 0.5);
        let scale = (1.0 + amount * (x * x + y * y) * 4.0).max(0.0) / fit;
        (0.5 + x * scale, 0.5 + y * scale)
    }))
}

fn mirror_effect(f: &Frame, p: &ParamSet, _: f64) -> Option<Frame> {
    let axis = p.choice("axis");
    Some(warp(f, |u, v| (if axis != 1 { u.min(1.0 - u) } else { u }, if axis != 0 { v.min(1.0 - v) } else { v })))
}

fn light_leak(f: &Frame, p: &ParamSet, t: f64) -> Option<Frame> {
    let (amount, speed) = (p.number("amount"), p.number("speed"));
    if amount == 0.0 {
        return None;
    }
    let center = -0.12 + (t * speed).sin() * 0.15;
    Some(rgb(f, |x, y, c| {
        let u = (x as f64 + 0.5) / f.width as f64;
        let v = (y as f64 + 0.5) / f.height as f64;
        let light = (-((u - center) / 0.3).powi(2) - ((v - 0.4) / 0.8).powi(2)).exp() * amount;
        let color = match c {
            2 => 255.0,
            1 => 110.0,
            _ => 35.0,
        };
        255.0 - (255.0 - px(f, x, y, c)) * (1.0 - light * color / 255.0)
    }))
}

fn duotone(f: &Frame, p: &ParamSet, _: f64) -> Option<Frame> {
    let amount = p.number("amount");
    if amount == 0.0 {
        return None;
    }
    let (low, high) = (p.color("shadows"), p.color("highlights"));
    Some(rgb(f, |x, y, c| {
        let luma = (0.0722 * px(f, x, y, 0) + 0.7152 * px(f, x, y, 1) + 0.2126 * px(f, x, y, 2)) / 255.0;
        let channel = |argb: u32| ((argb >> (c * 8)) & 255) as f64;
        let color = channel(low) * (1.0 - luma) + channel(high) * luma;
        px(f, x, y, c) * (1.0 - amount) + color * amount
    }))
}
