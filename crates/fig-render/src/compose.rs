//! Laying one picture over another.

use fig_fx::Frame;
use fig_fx::frame::rows_mut;

/// Lays `src` over `dst` (same size) at `opacity`: premultiplied "over" for a picture with real transparency,
/// otherwise a straight mix (video is opaque wherever it is drawn).
pub fn blend(dst: &mut Frame, src: &Frame, opacity: f64, premultiplied: bool) {
    if !dst.same_size(src) || opacity <= 0.0 {
        return;
    }
    let k = (opacity * 255.0).round_ties_even().clamp(0.0, 255.0) as u32;
    let stride = dst.stride();
    if premultiplied {
        rows_mut(&mut dst.pixels, stride, |y, row| {
            let s = &src.pixels[y * stride..(y + 1) * stride];
            for (d, p) in row.chunks_exact_mut(4).zip(s.chunks_exact(4)) {
                let a = p[3] as u32 * k / 255;
                if a == 0 {
                    continue;
                }
                let keep = 255 - a;
                for c in 0..3 {
                    d[c] = (p[c] as u32 * k / 255 + d[c] as u32 * keep / 255).min(255) as u8;
                }
            }
        });
    } else if k >= 255 {
        dst.pixels.copy_from_slice(&src.pixels);
    } else {
        let inv = 255 - k;
        rows_mut(&mut dst.pixels, stride, |y, row| {
            let s = &src.pixels[y * stride..(y + 1) * stride];
            for (d, &p) in row.iter_mut().zip(s) {
                *d = ((*d as u32 * inv + p as u32 * k) / 255) as u8;
            }
        });
    }
}
