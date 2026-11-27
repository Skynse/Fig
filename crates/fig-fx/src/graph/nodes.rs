//! The node types.

use std::sync::Arc;

use fig_timeline::GraphPortType::{Image, Mask};
use fig_timeline::{EffectGraph, GraphPortType};

use super::{NodeContext, Outputs, PortDef, Primitive};
use crate::frame::{Frame, blur, noise, render_rows, sample_rgb, shade, to_byte, warp};
use crate::param;

fn port(id: &'static str, label: &'static str, kind: GraphPortType) -> PortDef {
    PortDef { id, label, kind, required: true }
}

fn image_in() -> Vec<PortDef> {
    vec![port("image", "Image", Image)]
}

fn image_out() -> Vec<PortDef> {
    vec![port("image", "Image", Image)]
}

fn one(f: Arc<Frame>) -> Outputs {
    vec![("image", f)]
}

fn new(f: Frame) -> Outputs {
    one(Arc::new(f))
}

fn luma(b: u8, g: u8, r: u8) -> f64 {
    0.0722 * b as f64 + 0.7152 * g as f64 + 0.2126 * r as f64
}

/// A frame the size of `like`, each pixel made by `f(x, y, &mut [b, g, r, a])`.
fn fill(like: &Frame, f: impl Fn(usize, usize, &mut [u8]) + Sync + Send) -> Frame {
    render_rows(like, |y, row| {
        for (x, px) in row.as_chunks_mut::<4>().0.iter_mut().enumerate() {
            f(x, y, px);
        }
    })
}

fn px(f: &Frame, x: usize, y: usize) -> &[u8] {
    let i = (y * f.width + x) * 4;
    &f.pixels[i..i + 4]
}

fn wrap(v: f64) -> f64 {
    v - v.floor()
}

fn mirror(v: f64) -> f64 {
    let mut m = v % 2.0;
    if m < 0.0 {
        m += 2.0;
    }
    if m > 1.0 { 2.0 - m } else { m }
}

macro_rules! prim {
    ($id:literal, $name:literal, $cat:literal, $desc:literal, in: $inputs:expr, out: $outputs:expr, params: [$($p:expr),*], $eval:expr) => {
        Primitive {
            id: $id,
            display_name: $name,
            category: $cat,
            description: $desc,
            inputs: $inputs,
            outputs: $outputs,
            params: vec![$($p),*],
            eval: $eval,
        }
    };
}

pub(super) fn all() -> Vec<Primitive> {
    vec![
        // ---- sources ----
        prim!("input", "Input frame", "Sources", "The picture the effect is applied to.",
            in: vec![], out: image_out(), params: [], |c| one(c.source.clone())),
        prim!("solid", "Solid color", "Sources", "A flat color the size of the picture.",
            in: vec![], out: image_out(), params: [param!(Color "color", "Color", 0xFF000000u32)], solid),
        prim!("noise", "Noise", "Sources", "Animated blocky noise, as a mask.",
            in: vec![], out: vec![port("mask", "Mask", Mask)],
            params: [param!(Int "scale", "Cells across", 24, 2, 200), param!("speed", "Changes per second", 3, 0, 30)], noise_node),
        prim!("output", "Output", "Sources", "The finished picture. Every effect has exactly one.",
            in: image_in(), out: vec![], params: [], |_| Vec::new()),
        prim!("effect", "Effect", "Stock effects", "Use any built-in effect as a node.",
            in: image_in(), out: image_out(), params: [], stock_effect),
        // ---- blur ----
        prim!("blur", "Blur", "Blur & sharpen", "Soften detail with a fast two-pass blur.",
        in: image_in(), out: image_out(), params: [param!("radius", "Radius", 0.012, 0, 0.08)], |c| {
            let src = c.input("image");
            let r = (c.read().number("radius") * src.width.min(src.height) as f64).round_ties_even();
            if r <= 0.0 { one(src.clone()) } else { new(blur(src, r as usize)) }
        }),
        prim!("directional-blur", "Directional blur", "Blur & sharpen", "Smear the picture along an angle.",
        in: image_in(), out: image_out(),
        params: [param!("distance", "Distance", 0.025, 0, 0.15), param!("angle", "Angle", 0, -180, 180)], |c| {
            let src = c.input("image");
            let distance = c.read().number("distance") * src.width as f64;
            if distance <= 0.0 {
                return one(src.clone());
            }
            let a = c.read().number("angle").to_radians();
            let (sx, sy) = (a.cos() * distance / 4.0, a.sin() * distance / 4.0);
            new(shade(src, |x, y, acc| {
                for k in -4..=4 {
                    sample_rgb(src, x as f64 + sx * k as f64, y as f64 + sy * k as f64, 1.0 / 9.0, acc);
                }
            }))
        }),
        // ---- channels ----
        prim!("split-channels", "Split channels", "Channels", "Separate red, green and blue into three pictures.",
        in: image_in(), out: vec![port("r", "R", Image), port("g", "G", Image), port("b", "B", Image)], params: [], |c| {
            let src = c.input("image");
            let channel = |offset: usize| {
                Arc::new(fill(src, |x, y, o| {
                    let v = px(src, x, y)[offset];
                    o.copy_from_slice(&[v, v, v, 255]);
                }))
            };
            vec![("r", channel(2)), ("g", channel(1)), ("b", channel(0))]
        }),
        prim!("merge-channels", "Merge channels", "Channels", "Combine three pictures into red, green and blue.",
        in: vec![port("r", "R", Image), port("g", "G", Image), port("b", "B", Image)], out: image_out(), params: [], |c| {
            let (r, g, b) = (c.input("r"), c.input("g"), c.input("b"));
            if !g.same_size(r) || !b.same_size(r) {
                return one(r.clone());
            }
            new(fill(r, |x, y, o| {
                o.copy_from_slice(&[px(b, x, y)[0], px(g, x, y)[1], px(r, x, y)[2], 255]);
            }))
        }),
        // ---- colour ----
        prim!("color-matrix", "Color matrix", "Color", "Brightness, contrast and saturation in one step.",
            in: image_in(), out: image_out(),
            params: [
                param!("brightness", "Brightness", 0, -1, 1),
                param!("contrast", "Contrast", 0, -1, 1),
                param!("saturation", "Saturation", 0, -1, 1)
            ], color_matrix),
        prim!("levels", "Levels", "Color", "Set the black point, white point and midtones.",
            in: image_in(), out: image_out(),
            params: [
                param!("black", "Black point", 0, 0, 0.95),
                param!("white", "White point", 1, 0.05, 1),
                param!("gamma", "Midtones", 1, 0.2, 3)
            ], levels),
        prim!("duotone", "Duotone", "Color", "Map shadows and highlights to two colors.",
            in: image_in(), out: image_out(),
            params: [
                param!("amount", "Blend", 1, 0, 1),
                param!(Color "shadows", "Shadows", 0xFF14284Du32),
                param!(Color "highlights", "Highlights", 0xFFFFC857u32)
            ], duotone),
        // ---- distort ----
        prim!("uv-offset", "UV offset", "Distort", "Shift the picture sideways or up and down.",
        in: image_in(), out: image_out(),
        params: [
            param!("x", "Offset X", 0, -0.5, 0.5),
            param!("y", "Offset Y", 0, -0.5, 0.5),
            param!(List "edge", "Edge", 0, ["Clamp", "Wrap", "Mirror"])
        ], |c| {
            let src = c.input("image");
            let p = c.read();
            let (ox, oy) = (p.number("x"), p.number("y"));
            if ox == 0.0 && oy == 0.0 {
                return one(src.clone());
            }
            let fold: fn(f64) -> f64 = match p.choice("edge") {
                1 => wrap,
                2 => mirror,
                _ => |v| v,
            };
            new(warp(src, |u, v| (fold(u + ox), fold(v + oy))))
        }),
        prim!("lens-warp", "Lens warp", "Distort", "Barrel or pincushion distortion.",
        in: image_in(), out: image_out(), params: [param!("amount", "Distortion", 0.3, -0.6, 0.6)], |c| {
            let src = c.input("image");
            let amount = c.read().number("amount");
            if amount == 0.0 {
                return one(src.clone());
            }
            let fit = if amount > 0.0 { 1.0 + amount * 2.0 } else { 1.0 };
            new(warp(src, |u, v| {
                let (x, y) = (u - 0.5, v - 0.5);
                let s = (1.0 + amount * (x * x + y * y) * 4.0).max(0.0) / fit;
                (0.5 + x * s, 0.5 + y * s)
            }))
        }),
        prim!("mirror", "Mirror", "Distort", "Reflect half the picture or make four quadrants.",
        in: image_in(), out: image_out(),
        params: [param!(List "axis", "Reflection", 0, ["Left to right", "Top to bottom", "Four quadrants"])], |c| {
            let axis = c.read().choice("axis");
            new(warp(c.input("image"), |u, v| {
                (if axis != 1 { u.min(1.0 - u) } else { u }, if axis != 0 { v.min(1.0 - v) } else { v })
            }))
        }),
        // ---- mix and mask ----
        prim!("mix", "Mix", "Mix & mask", "Combine two pictures, optionally through a mask.",
            in: vec![port("a", "A", Image), port("b", "B", Image), PortDef { id: "mask", label: "Mask", kind: Mask, required: false }],
            out: image_out(),
            params: [param!("amount", "Amount", 0.5, 0, 1), param!(List "mode", "Mode", 0, ["Normal", "Add", "Multiply", "Screen"])],
            mix_node),
        prim!("mask-luma", "Mask from luma", "Mix & mask", "Turn brightness into a mask.",
        in: image_in(), out: vec![port("mask", "Mask", Mask)],
        params: [
            param!("threshold", "Threshold", 0.5, 0, 1),
            param!("softness", "Softness", 0.1, 0.001, 0.5),
            param!(Bool "invert", "Invert", 0)
        ], |c| {
            let src = c.input("image");
            let p = c.read();
            let (threshold, softness, invert) = (p.number("threshold"), p.number("softness"), p.bool("invert"));
            let mask = fill(src, |x, y, o| {
                let s = px(src, x, y);
                let l = luma(s[0], s[1], s[2]) / 255.0;
                let t = ((l - (threshold - softness / 2.0)) / softness).clamp(0.0, 1.0);
                let v = to_byte(if invert { 1.0 - t } else { t } * 255.0);
                o.copy_from_slice(&[v, v, v, 255]);
            });
            vec![("mask", Arc::new(mask))]
        }),
    ]
}

fn solid(c: &NodeContext) -> Outputs {
    let color = c.read().color("color");
    let p = [(color & 0xFF) as u8, (color >> 8) as u8, (color >> 16) as u8, 255];
    new(fill(c.source, |_, _, o| o.copy_from_slice(&p)))
}

fn noise_node(c: &NodeContext) -> Outputs {
    let p = c.read();
    let w = c.source.width;
    let cell = (w / p.int("scale").max(2) as usize).max(1);
    let seed = (c.local_t * p.number("speed")).floor() as i32;
    let mask = fill(c.source, |x, y, o| {
        let v = to_byte(noise((x / cell) as i32, (y / cell) as i32, seed) * 255.0);
        o.copy_from_slice(&[v, v, v, 255]);
    });
    vec![("mask", Arc::new(mask))]
}

fn stock_effect(c: &NodeContext) -> Outputs {
    let src = c.input("image");
    let Some(type_id) = c.node.effect_type_id.as_deref().filter(|t| !EffectGraph::is_graph_type_id(Some(t))) else {
        return one(src.clone());
    };
    let Some(entry) = crate::effects::find(type_id) else { return one(src.clone()) };
    let mut params = entry.default_params();
    params.extend(c.params.iter().map(|(k, v)| (k.clone(), *v)));
    match entry.apply(src, &params, c.local_t) {
        Some(f) => new(f),
        None => one(src.clone()),
    }
}

fn color_matrix(c: &NodeContext) -> Outputs {
    let src = c.input("image");
    let p = c.read();
    let (brightness, contrast, saturation) = (p.number("brightness"), p.number("contrast"), p.number("saturation"));
    if brightness == 0.0 && contrast == 0.0 && saturation == 0.0 {
        return one(src.clone());
    }
    let k = 1.0 + contrast;
    let add = 128.0 * (1.0 - k) + brightness * 255.0;
    let sat = 1.0 + saturation;
    new(fill(src, |x, y, o| {
        let s = px(src, x, y);
        let l = luma(s[0], s[1], s[2]);
        for ch in 0..3 {
            o[ch] = to_byte((l + (s[ch] as f64 - l) * sat) * k + add);
        }
        o[3] = s[3];
    }))
}

fn levels(c: &NodeContext) -> Outputs {
    let src = c.input("image");
    let p = c.read();
    let (black, mut white, gamma) = (p.number("black"), p.number("white"), p.number("gamma"));
    if white <= black + 0.01 {
        white = black + 0.01;
    }
    if black == 0.0 && white == 1.0 && (gamma - 1.0).abs() < 1e-6 {
        return one(src.clone());
    }
    let lut: Vec<u8> = (0..256)
        .map(|i| {
            let v = ((i as f64 / 255.0 - black) / (white - black)).clamp(0.0, 1.0);
            to_byte(v.powf(1.0 / gamma) * 255.0)
        })
        .collect();
    new(fill(src, |x, y, o| {
        let s = px(src, x, y);
        o.copy_from_slice(&[lut[s[0] as usize], lut[s[1] as usize], lut[s[2] as usize], s[3]]);
    }))
}

fn duotone(c: &NodeContext) -> Outputs {
    let src = c.input("image");
    let p = c.read();
    let amount = p.number("amount");
    if amount <= 0.0 {
        return one(src.clone());
    }
    let (low, high) = (p.color("shadows"), p.color("highlights"));
    new(fill(src, |x, y, o| {
        let s = px(src, x, y);
        let l = luma(s[0], s[1], s[2]) / 255.0;
        for ch in 0..3 {
            let target = ((low >> (ch * 8)) & 255) as f64 * (1.0 - l) + ((high >> (ch * 8)) & 255) as f64 * l;
            o[ch] = to_byte(s[ch] as f64 * (1.0 - amount) + target * amount);
        }
        o[3] = s[3];
    }))
}

fn mix_node(c: &NodeContext) -> Outputs {
    let (a, b) = (c.input("a"), c.input("b"));
    let mask = c.optional_input("mask");
    let p = c.read();
    let (amount, mode) = (p.number("amount"), p.choice("mode"));
    if !a.same_size(b) || mask.is_some_and(|m| !m.same_size(a)) || (mask.is_none() && amount <= 0.0) {
        return one(a.clone());
    }
    new(fill(a, |x, y, o| {
        let (pa, pb) = (px(a, x, y), px(b, x, y));
        let w = amount * mask.map_or(1.0, |m| px(m, x, y)[0] as f64 / 255.0);
        for ch in 0..3 {
            let (va, vb) = (pa[ch] as f64, pb[ch] as f64);
            let blended = match mode {
                1 => va + vb,
                2 => va * vb / 255.0,
                3 => 255.0 - (255.0 - va) * (255.0 - vb) / 255.0,
                _ => vb,
            };
            o[ch] = to_byte(va + (blended - va) * w);
        }
        o[3] = pa[3];
    }))
}
