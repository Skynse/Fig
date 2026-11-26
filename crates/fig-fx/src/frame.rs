//! Decoded pictures and the pixel loops every effect, transition and compositor is built from.
//!
//! A [`Frame`] is BGRA, 8 bits a channel, top row first (what FFmpeg decodes to for us). Pictures drawn rather than
//! decoded (text, stills) carry real transparency and are premultiplied; video is opaque.

use rayon::prelude::*;

#[derive(Clone, PartialEq, Eq)]
pub struct Frame {
    pub width: usize,
    pub height: usize,
    /// BGRA, `width * height * 4` bytes, top row first.
    pub pixels: Vec<u8>,
}

impl std::fmt::Debug for Frame {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "Frame({}×{})", self.width, self.height)
    }
}

impl Frame {
    /// A transparent black frame.
    pub fn new(width: usize, height: usize) -> Frame {
        Frame { width, height, pixels: vec![0; width * height * 4] }
    }

    /// A frame of one colour (ARGB).
    pub fn solid(width: usize, height: usize, argb: u32) -> Frame {
        let px = [(argb & 0xFF) as u8, (argb >> 8) as u8, (argb >> 16) as u8, (argb >> 24) as u8];
        let mut f = Frame::new(width, height);
        for p in f.pixels.chunks_exact_mut(4) {
            p.copy_from_slice(&px);
        }
        f
    }

    pub fn from_pixels(width: usize, height: usize, pixels: Vec<u8>) -> Frame {
        debug_assert_eq!(pixels.len(), width * height * 4);
        Frame { width, height, pixels }
    }

    pub fn stride(&self) -> usize {
        self.width * 4
    }

    pub fn same_size(&self, other: &Frame) -> bool {
        self.width == other.width && self.height == other.height
    }

    /// The pixel at (x, y), as [b, g, r, a].
    pub fn at(&self, x: usize, y: usize) -> [u8; 4] {
        let i = (y * self.width + x) * 4;
        [self.pixels[i], self.pixels[i + 1], self.pixels[i + 2], self.pixels[i + 3]]
    }

    /// Sets every pixel's alpha to 255. Video counts as opaque wherever it's drawn, but its frames don't promise a
    /// full alpha byte; this makes that true before it's mixed with a picture that has real transparency.
    pub fn make_opaque(&mut self) {
        let stride = self.stride();
        rows_mut(&mut self.pixels, stride, |_, row| {
            for p in row.chunks_exact_mut(4) {
                p[3] = 255;
            }
        });
    }

    /// RGBA bytes, for the screen.
    pub fn to_rgba(&self) -> Vec<u8> {
        let mut out = self.pixels.clone();
        let stride = self.stride();
        rows_mut(&mut out, stride, |_, row| {
            for p in row.chunks_exact_mut(4) {
                p.swap(0, 2);
            }
        });
        out
    }

    /// A frame made from RGBA bytes.
    pub fn from_rgba(width: usize, height: usize, mut rgba: Vec<u8>) -> Frame {
        for p in rgba.chunks_exact_mut(4) {
            p.swap(0, 2);
        }
        Frame::from_pixels(width, height, rgba)
    }
}

/// Frames this tall or taller run their rows in parallel.
const PARALLEL_MIN_ROWS: usize = 160;

/// Runs `f(y, row)` over every row of a BGRA buffer, in parallel on larger frames.
pub fn rows_mut(pixels: &mut [u8], stride: usize, f: impl Fn(usize, &mut [u8]) + Sync + Send) {
    if stride == 0 {
        return;
    }
    if pixels.len() / stride >= PARALLEL_MIN_ROWS {
        pixels.par_chunks_mut(stride).enumerate().for_each(|(y, row)| f(y, row));
    } else {
        pixels.chunks_mut(stride).enumerate().for_each(|(y, row)| f(y, row));
    }
}

/// A new frame the size of `source`, each row written by `f(y, row)`.
pub fn render_rows(source: &Frame, f: impl Fn(usize, &mut [u8]) + Sync + Send) -> Frame {
    let mut out = Frame::new(source.width, source.height);
    let stride = out.stride();
    rows_mut(&mut out.pixels, stride, f);
    out
}

/// A channel value rounded (halves to even, as .NET does) and held to 0–255.
#[inline]
pub fn to_byte(v: f64) -> u8 {
    let r = v.round_ties_even();
    if r <= 0.0 {
        0
    } else if r >= 255.0 {
        255
    } else {
        r as u8
    }
}

/// A colour change made pixel by pixel: `map` takes (r, g, b), 0–255 each, and gives what they become. Rounding and
/// clamping, alpha kept.
pub fn map_color(frame: &Frame, map: impl Fn(f64, f64, f64) -> (f64, f64, f64) + Sync + Send) -> Frame {
    let src = &frame.pixels;
    let stride = frame.stride();
    render_rows(frame, |y, row| {
        let s = &src[y * stride..(y + 1) * stride];
        for (o, i) in row.chunks_exact_mut(4).zip(s.chunks_exact(4)) {
            let (r, g, b) = map(i[2] as f64, i[1] as f64, i[0] as f64);
            o[0] = to_byte(b);
            o[1] = to_byte(g);
            o[2] = to_byte(r);
            o[3] = i[3];
        }
    })
}

/// A picture rearranged: each pixel of the new frame is the source pixel at column `source_x[x]` of row
/// `source_y(y)` (a flip, a mosaic...).
pub fn remap(frame: &Frame, source_x: &[usize], source_y: impl Fn(usize) -> usize + Sync + Send) -> Frame {
    let stride = frame.stride();
    let src = &frame.pixels;
    render_rows(frame, |y, row| {
        let from = &src[source_y(y) * stride..][..stride];
        for (x, o) in row.chunks_exact_mut(4).enumerate() {
            let s = source_x[x] * 4;
            o.copy_from_slice(&from[s..s + 4]);
        }
    })
}

/// `dst = (a * (256 - w) + b * w + 128) >> 8` byte by byte (w in 0..=256). Within one level of the exact blend.
#[inline]
pub fn lerp_bytes(dst: &mut [u8], a: &[u8], b: &[u8], w256: u32) {
    if w256 == 0 {
        dst.copy_from_slice(a);
    } else if w256 >= 256 {
        dst.copy_from_slice(b);
    } else {
        let inv = 256 - w256;
        for ((d, &x), &y) in dst.iter_mut().zip(a).zip(b) {
            *d = ((x as u32 * inv + y as u32 * w256 + 128) >> 8) as u8;
        }
    }
}

/// Two same-size frames mixed by one weight (0..=256).
pub fn lerp_frames(a: &Frame, b: &Frame, w256: u32) -> Frame {
    let stride = a.stride();
    render_rows(a, |y, row| {
        let r = y * stride..(y + 1) * stride;
        lerp_bytes(row, &a.pixels[r.clone()], &b.pixels[r], w256);
    })
}

/// `src * k / 256` (k in 0..=256) on every byte: a fade to black.
pub fn scale_frame(src: &Frame, k256: u32) -> Frame {
    let stride = src.stride();
    render_rows(src, |y, row| {
        let s = &src.pixels[y * stride..(y + 1) * stride];
        if k256 >= 256 {
            row.copy_from_slice(s);
        } else if k256 > 0 {
            for (d, &v) in row.iter_mut().zip(s) {
                *d = ((v as u32 * k256 + 128) >> 8) as u8;
            }
        }
    })
}

/// Blends two frames with a different weight per column (wipe, curtains): the same weights apply to every row.
pub fn column_blend(a: &Frame, b: &Frame, weight256: &[u32]) -> Frame {
    // runs of equal weight are one kernel call each
    let mut runs: Vec<(usize, usize, u32)> = Vec::new();
    let mut start = 0;
    for x in 1..=a.width {
        if x == a.width || weight256[x] != weight256[start] {
            runs.push((start, x, weight256[start]));
            start = x;
        }
    }
    let stride = a.stride();
    render_rows(a, |y, row| {
        let base = y * stride;
        for &(x0, x1, w) in &runs {
            let r = x0 * 4..x1 * 4;
            lerp_bytes(
                &mut row[r.clone()],
                &a.pixels[base + r.start..base + r.end],
                &b.pixels[base + r.start..base + r.end],
                w,
            );
        }
    })
}

/// Weight (0..=256) for a mix in 0..1.
pub fn weight256(mix: f64) -> u32 {
    (mix * 256.0).round_ties_even().clamp(0.0, 256.0) as u32
}

/// A bilinear sample of one channel at a fractional pixel position (held to the frame).
#[inline]
pub fn sample(f: &Frame, x: f64, y: f64, channel: usize) -> f64 {
    let x = x.clamp(0.0, (f.width - 1) as f64);
    let y = y.clamp(0.0, (f.height - 1) as f64);
    let (x0, y0) = (x as usize, y as usize);
    let (x1, y1) = ((x0 + 1).min(f.width - 1), (y0 + 1).min(f.height - 1));
    let (fx, fy) = (x - x0 as f64, y - y0 as f64);
    let p = &f.pixels;
    let w = f.width;
    let top = p[(y0 * w + x0) * 4 + channel] as f64 * (1.0 - fx) + p[(y0 * w + x1) * 4 + channel] as f64 * fx;
    let bottom = p[(y1 * w + x0) * 4 + channel] as f64 * (1.0 - fx) + p[(y1 * w + x1) * 4 + channel] as f64 * fx;
    top * (1.0 - fy) + bottom * fy
}

/// Adds a bilinear sample of the three colour channels, scaled by `weight`, to `acc` ([b, g, r]).
#[inline]
pub fn sample_rgb(f: &Frame, x: f64, y: f64, weight: f64, acc: &mut [f64; 3]) {
    let x = x.clamp(0.0, (f.width - 1) as f64);
    let y = y.clamp(0.0, (f.height - 1) as f64);
    let (x0, y0) = (x as usize, y as usize);
    let (x1, y1) = ((x0 + 1).min(f.width - 1), (y0 + 1).min(f.height - 1));
    let (fx, fy) = (x - x0 as f64, y - y0 as f64);
    let w = f.width;
    let (i00, i10, i01, i11) = ((y0 * w + x0) * 4, (y0 * w + x1) * 4, (y1 * w + x0) * 4, (y1 * w + x1) * 4);
    let (w00, w10) = ((1.0 - fx) * (1.0 - fy) * weight, fx * (1.0 - fy) * weight);
    let (w01, w11) = ((1.0 - fx) * fy * weight, fx * fy * weight);
    let p = &f.pixels;
    for c in 0..3 {
        acc[c] += p[i00 + c] as f64 * w00 + p[i10 + c] as f64 * w10 + p[i01 + c] as f64 * w01 + p[i11 + c] as f64 * w11;
    }
}

/// A picture resampled: each output pixel shows the source at `map(u, v)` (0..1 across), all four channels.
pub fn warp(source: &Frame, map: impl Fn(f64, f64) -> (f64, f64) + Sync + Send) -> Frame {
    let (w, h) = (source.width as f64, source.height as f64);
    render_rows(source, |y, row| {
        for (x, o) in row.chunks_exact_mut(4).enumerate() {
            let (u, v) = map((x as f64 + 0.5) / w, (y as f64 + 0.5) / h);
            for c in 0..4 {
                o[c] = to_byte(sample(source, u * w - 0.5, v * h - 0.5, c));
            }
        }
    })
}

/// Each colour channel of each pixel worked out by `channel(x, y, c)` (c: 0 blue, 1 green, 2 red), alpha kept.
pub fn rgb(source: &Frame, channel: impl Fn(usize, usize, usize) -> f64 + Sync + Send) -> Frame {
    let stride = source.stride();
    render_rows(source, |y, row| {
        for (x, o) in row.chunks_exact_mut(4).enumerate() {
            for c in 0..3 {
                o[c] = to_byte(channel(x, y, c));
            }
            o[3] = source.pixels[y * stride + x * 4 + 3];
        }
    })
}

/// Like [`rgb`] but the shader fills all three channels at once ([b, g, r], starting at 0), so work shared between
/// channels is not repeated.
pub fn shade(source: &Frame, shader: impl Fn(usize, usize, &mut [f64; 3]) + Sync + Send) -> Frame {
    let stride = source.stride();
    render_rows(source, |y, row| {
        for (x, o) in row.chunks_exact_mut(4).enumerate() {
            let mut acc = [0.0; 3];
            shader(x, y, &mut acc);
            for c in 0..3 {
                o[c] = to_byte(acc[c]);
            }
            o[3] = source.pixels[y * stride + x * 4 + 3];
        }
    })
}

/// Two frames mixed pixel by pixel by `weight(x, y)` (0..1), all four channels.
pub fn mix(a: &Frame, b: &Frame, weight: impl Fn(usize, usize) -> f64 + Sync + Send) -> Frame {
    let stride = a.stride();
    render_rows(a, |y, row| {
        for (x, o) in row.chunks_exact_mut(4).enumerate() {
            let t = weight(x, y).clamp(0.0, 1.0);
            let i = y * stride + x * 4;
            for c in 0..4 {
                o[c] = to_byte(a.pixels[i + c] as f64 * (1.0 - t) + b.pixels[i + c] as f64 * t);
            }
        }
    })
}

/// Repeatable noise in 0..1 for an integer cell and seed (float multiply/add/fract only, so it is the same
/// everywhere).
pub fn noise(x: i32, y: i32, seed: i32) -> f64 {
    fn frac(v: f32) -> f32 {
        v - v.floor()
    }
    let (mut px, mut py, mut pz) = (frac(x as f32 * 0.1031), frac(y as f32 * 0.1031), frac(seed as f32 * 0.1031));
    let d = px * (pz + 31.32) + py * (py + 31.32) + pz * (px + 31.32);
    px += d;
    py += d;
    pz += d;
    frac((px + py) * pz) as f64
}

/// A coordinate (0..1 across the picture) folded back inside it, mirroring at each edge.
pub fn mirror(t: f64) -> f64 {
    let m = t - 2.0 * (t / 2.0).floor();
    1.0 - (1.0 - m).abs()
}

/// Sliding-window separable box blur: the work per pixel doesn't depend on the radius.
pub fn blur(source: &Frame, radius: usize) -> Frame {
    if radius == 0 {
        return source.clone();
    }
    let radius = radius.min(128) as isize;
    let (w, h) = (source.width, source.height);
    let count = (radius * 2 + 1) as u32;
    let clamp = |v: isize, max: usize| v.clamp(0, max as isize - 1) as usize;
    let horizontal = render_rows(source, |y, row| {
        let s = &source.pixels[y * w * 4..(y + 1) * w * 4];
        for c in 0..4 {
            let mut sum: u32 = (-radius..=radius).map(|k| s[clamp(k, w) * 4 + c] as u32).sum();
            for x in 0..w {
                row[x * 4 + c] = (sum / count) as u8;
                sum -= s[clamp(x as isize - radius, w) * 4 + c] as u32;
                sum += s[clamp(x as isize + radius + 1, w) * 4 + c] as u32;
            }
        }
    });
    // columns: transpose-free, each worker takes a band of columns
    let mut out = Frame::new(w, h);
    let columns: Vec<Vec<u8>> = (0..w)
        .into_par_iter()
        .map(|x| {
            let mut col = vec![0u8; h * 4];
            let px = |y: usize, c: usize| horizontal.pixels[(y * w + x) * 4 + c] as u32;
            for c in 0..4 {
                let mut sum: u32 = (-radius..=radius).map(|k| px(clamp(k, h), c)).sum();
                for y in 0..h {
                    col[y * 4 + c] = (sum / count) as u8;
                    sum -= px(clamp(y as isize - radius, h), c);
                    sum += px(clamp(y as isize + radius + 1, h), c);
                }
            }
            col
        })
        .collect();
    let stride = out.stride();
    rows_mut(&mut out.pixels, stride, |y, row| {
        for (x, col) in columns.iter().enumerate() {
            row[x * 4..x * 4 + 4].copy_from_slice(&col[y * 4..y * 4 + 4]);
        }
    });
    out
}
