//! Drawing the clips that are drawn rather than decoded: titles and stills (and animated GIFs and WebPs, which loop),
//! into transparent, frame-sized, premultiplied BGRA pictures. Pictures are cached by what they show, so a layer
//! that isn't changing is drawn once, not every frame. Safe to use from the preview and export threads at once.

use std::collections::{HashMap, VecDeque};
use std::hash::{Hash, Hasher};
use std::path::Path;
use std::sync::{Arc, LazyLock, Mutex};

use cosmic_text::{Attrs, Buffer, Family, FontSystem, Metrics, Shaping, Style, SwashCache, SwashContent, Weight};
use fig_fx::Frame;
use fig_timeline::{Clip, ClipBody, LayerTransform, TextAlign, TextBody, TextStyle, properties, text_words};
use swash::zeno::Command;
use tiny_skia::{
    BlendMode, Color, FillRule, FilterQuality, LineCap, LineJoin, Paint, PathBuilder, Pixmap, PixmapPaint, Rect,
    Stroke, Transform,
};

/// A layer's unscaled size, in frame heights (so a full-height square in a 16:9 frame is 1 × 1).
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct LayerSize {
    pub width: f64,
    pub height: f64,
}

/// Where a drawn layer sits in the frame, matching how it is drawn: moved to its centre, turned, then scaled. Points
/// are in frame units (0..1 on each axis); `aspect` is the frame's width over its height.
pub mod geometry {
    use super::LayerSize;
    use fig_timeline::LayerTransform;

    /// The layer's four corners, clockwise from the top left.
    pub fn corners(layout: &LayerTransform, size: LayerSize, aspect: f64) -> [(f64, f64); 4] {
        let hw = size.width * layout.scale / 2.0;
        let hh = size.height * layout.scale / 2.0;
        [(-hw, -hh), (hw, -hh), (hw, hh), (-hw, hh)].map(|(x, y)| to_frame(layout, x, y, aspect))
    }

    /// A point in the layer's own units (frame heights from its centre, before turning) in frame units.
    pub fn to_frame(layout: &LayerTransform, x: f64, y: f64, aspect: f64) -> (f64, f64) {
        let (sin, cos) = layout.rotation.to_radians().sin_cos();
        (layout.center_x + (x * cos - y * sin) / aspect, layout.center_y + x * sin + y * cos)
    }

    /// A frame point in the layer's own units: frame heights from its centre, its turn undone.
    pub fn to_local(layout: &LayerTransform, x: f64, y: f64, aspect: f64) -> (f64, f64) {
        let dx = (x - layout.center_x) * aspect;
        let dy = y - layout.center_y;
        let (sin, cos) = (-layout.rotation).to_radians().sin_cos();
        (dx * cos - dy * sin, dx * sin + dy * cos)
    }

    pub fn contains(layout: &LayerTransform, size: LayerSize, x: f64, y: f64, aspect: f64) -> bool {
        let (lx, ly) = to_local(layout, x, y, aspect);
        lx.abs() <= size.width * layout.scale / 2.0 && ly.abs() <= size.height * layout.scale / 2.0
    }
}

// ---- fonts ------------------------------------------------------------------------------------

struct Fonts {
    system: FontSystem,
    cache: SwashCache,
    /// COLR emoji painted so far, by font, glyph, size and text colour (none: the glyph has no COLR picture).
    colr: std::collections::HashMap<(cosmic_text::fontdb::ID, u16, u32, u32), Option<std::sync::Arc<crate::colr::Painted>>>,
}

/// The system's fonts, found once (it takes a moment).
static FONTS: LazyLock<Mutex<Fonts>> =
    LazyLock::new(|| Mutex::new(Fonts { system: FontSystem::new(), cache: SwashCache::new(), colr: Default::default() }));

/// One positioned glyph of a laid-out title.
struct Glyph {
    key: cosmic_text::CacheKey,
    x: f32,
    y: f32,
    lit: bool,
}

/// A title laid out: its glyphs (pen positions in pixels, block centred on 0,0) and its size.
struct TextBlock {
    glyphs: Vec<Glyph>,
    width: f32,
    height: f32,
    size: f32,
    stroke: f32,
    background: bool,
}

impl TextBlock {
    fn pad(&self) -> f32 {
        self.size * 0.3
    }

    /// What the gizmo's box goes round: the text (and its outline), or the box behind it when there is one.
    fn bounds(&self) -> (f32, f32) {
        if self.background {
            (self.width + self.pad() * 2.0, self.height + self.pad() * 1.2)
        } else {
            (self.width + self.stroke, self.height + self.stroke)
        }
    }
}

/// Lays out a title as `style` sets it on a `width` × `height` frame. `lit` is the spoken word's bytes in the
/// displayed text.
fn lay_out(
    fonts: &mut Fonts,
    text: &str,
    style: &TextStyle,
    width: usize,
    height: usize,
    lit: Option<std::ops::Range<usize>>,
) -> TextBlock {
    let size = (style.size_percent / 100.0 * height as f64).max(1.0) as f32;
    let line_height = size * 1.21;
    let display = text_words::display(text, style);
    let mut buffer = Buffer::new(&mut fonts.system, Metrics::new(size, line_height));
    let wrap = (style.wrap_percent > 0.0).then(|| (style.wrap_percent / 100.0 * width as f64) as f32);
    buffer.set_size(wrap, None);
    let base = Attrs::new()
        .family(Family::Name(&style.font_family))
        .weight(if style.bold { Weight::BOLD } else { Weight::NORMAL })
        .style(if style.italic { Style::Italic } else { Style::Normal });
    // every line left-aligned here; the block lines them up below, across its widest line
    let align = Some(cosmic_text::Align::Left);
    match &lit {
        Some(r) if r.end <= display.len() => {
            let spans = [
                (&display[..r.start], base.clone()),
                (&display[r.clone()], base.clone().metadata(1)),
                (&display[r.end..], base.clone()),
            ];
            buffer.set_rich_text(spans, &base, Shaping::Advanced, align);
        }
        _ => buffer.set_text(&display, &base, Shaping::Advanced, align),
    }
    buffer.shape_until_scroll(&mut fonts.system, false);
    let runs: Vec<_> = buffer.layout_runs().collect();
    let block_w = runs.iter().map(|r| r.line_w).fold(0.0f32, f32::max);
    let block_h = line_height * runs.len() as f32;
    let mut glyphs = Vec::new();
    for run in &runs {
        let line_left = match style.align {
            TextAlign::Left => 0.0,
            TextAlign::Center => (block_w - run.line_w) / 2.0,
            TextAlign::Right => block_w - run.line_w,
        };
        for g in run.glyphs {
            let p = g.physical((0.0, 0.0), 1.0);
            glyphs.push(Glyph {
                key: p.cache_key,
                x: p.x as f32 + line_left - block_w / 2.0,
                y: p.y as f32 + run.line_y - block_h / 2.0,
                lit: g.metadata == 1,
            });
        }
    }
    let stroke = if style.outline { size * (style.outline_percent / 100.0) as f32 } else { 0.0 };
    TextBlock { glyphs, width: block_w, height: block_h, size, stroke, background: style.background }
}

fn color(argb: u32) -> Color {
    Color::from_rgba8((argb >> 16) as u8, (argb >> 8) as u8, argb as u8, (argb >> 24) as u8)
}

/// The font families installed on this machine, sorted, for a font picker.
pub fn font_families() -> Vec<String> {
    let fonts = FONTS.lock().unwrap();
    let mut names: Vec<String> =
        fonts.system.db().faces().filter_map(|f| f.families.first().map(|(name, _)| name.clone())).collect();
    names.sort_by_key(|n| n.to_lowercase());
    names.dedup();
    names
}

fn paint(argb: u32) -> Paint<'static> {
    let mut p = Paint::default();
    p.set_color(color(argb));
    p.anti_alias = true;
    p
}

/// Moves to the layer's centre, then turns and scales around it.
fn place(layout: &LayerTransform, width: usize, height: usize) -> Transform {
    Transform::from_translate((layout.center_x * width as f64) as f32, (layout.center_y * height as f64) as f32)
        .pre_rotate(layout.rotation as f32)
        .pre_scale(layout.scale.max(0.01) as f32, layout.scale.max(0.01) as f32)
}

/// A glyph's outline as a path at its pen position (font outlines run y up; the picture runs y down).
fn glyph_path(fonts: &mut Fonts, g: &Glyph) -> Option<tiny_skia::Path> {
    let commands = fonts.cache.get_outline_commands(&mut fonts.system, g.key)?;
    let mut pb = PathBuilder::new();
    let (ox, oy) = (g.x, g.y);
    for c in commands {
        match *c {
            Command::MoveTo(p) => pb.move_to(ox + p.x, oy - p.y),
            Command::LineTo(p) => pb.line_to(ox + p.x, oy - p.y),
            Command::QuadTo(a, p) => pb.quad_to(ox + a.x, oy - a.y, ox + p.x, oy - p.y),
            Command::CurveTo(a, b, p) => pb.cubic_to(ox + a.x, oy - a.y, ox + b.x, oy - b.y, ox + p.x, oy - p.y),
            Command::Close => pb.close(),
        }
    }
    pb.finish()
}

/// Draws a colour glyph (an emoji) at its pen position: from the font's COLR table (v0 or v1), else the bitmap
/// swash finds (CBDT, sbix).
fn draw_color_glyph(fonts: &mut Fonts, g: &Glyph, target: &mut Pixmap, t: Transform, text: u32) {
    let key = (g.key.font_id, g.key.glyph_id, g.key.font_size_bits, text);
    let painted = match fonts.colr.get(&key) {
        Some(p) => p.clone(),
        None => {
            let foreground = tiny_skia::Color::from_rgba8((text >> 16) as u8, (text >> 8) as u8, text as u8, (text >> 24) as u8);
            let size = f32::from_bits(g.key.font_size_bits);
            let painted = fonts
                .system
                .db()
                .with_face_data(g.key.font_id, |data, index| crate::colr::paint(data, index, g.key.glyph_id, size, foreground))
                .flatten()
                .map(std::sync::Arc::new);
            fonts.colr.insert(key, painted.clone());
            painted
        }
    };
    if let Some(p) = painted {
        let at = Transform::from_translate(g.x + p.left, g.y + p.top);
        target.draw_pixmap(0, 0, p.pixmap.as_ref(), &PixmapPaint { quality: FilterQuality::Bilinear, ..Default::default() }, t.pre_concat(at), None);
        return;
    }
    let Some(image) = fonts.cache.get_image(&mut fonts.system, g.key).as_ref() else { return };
    if image.content != SwashContent::Color || image.placement.width == 0 {
        return;
    }
    let (w, h) = (image.placement.width, image.placement.height);
    let Some(mut pm) = Pixmap::new(w, h) else { return };
    for (dst, src) in pm.pixels_mut().iter_mut().zip(image.data.chunks_exact(4)) {
        let c = tiny_skia::ColorU8::from_rgba(src[0], src[1], src[2], src[3]);
        *dst = c.premultiply();
    }
    let at = Transform::from_translate(g.x + image.placement.left as f32, g.y - image.placement.top as f32);
    target.draw_pixmap(
        0,
        0,
        pm.as_ref(),
        &PixmapPaint { quality: FilterQuality::Bilinear, ..Default::default() },
        t.pre_concat(at),
        None,
    );
}

fn draw_text(
    fonts: &mut Fonts,
    target: &mut Pixmap,
    body: &TextBody,
    style: &TextStyle,
    layout: &LayerTransform,
    lit: Option<std::ops::Range<usize>>,
) {
    let (width, height) = (target.width() as usize, target.height() as usize);
    let block = lay_out(fonts, &body.text, style, width, height, lit);
    let t = place(layout, width, height);
    if style.background {
        let pad = block.pad();
        let rect = Rect::from_xywh(
            -block.width / 2.0 - pad,
            -block.height / 2.0 - pad * 0.6,
            block.width + pad * 2.0,
            block.height + pad * 1.2,
        );
        if let Some(path) = rect.and_then(|r| rounded_rect(r, block.size * 0.2)) {
            target.fill_path(&path, &paint(style.background_color), FillRule::Winding, t, None);
        }
    }
    let paths: Vec<(Option<tiny_skia::Path>, bool)> =
        block.glyphs.iter().map(|g| (glyph_path(fonts, g), g.lit)).collect();
    let outline = (block.stroke > 0.0).then(|| Stroke {
        width: block.stroke,
        line_join: LineJoin::Round,
        line_cap: LineCap::Round,
        ..Default::default()
    });
    // the shadow goes under whichever is drawn first (the outline, or the letters), so it never falls on the text
    if style.shadow {
        let mut shadow = Pixmap::new(target.width(), target.height()).unwrap();
        let offset = t.post_translate(0.0, block.size * 0.04 * layout.scale.max(0.01) as f32);
        let black = paint(0xFF000000);
        for (path, _) in &paths {
            let Some(path) = path else { continue };
            match &outline {
                Some(s) => shadow.stroke_path(path, &black, s, offset, None),
                None => shadow.fill_path(path, &black, FillRule::Winding, offset, None),
            }
        }
        blur_alpha(&mut shadow, (block.size * 0.06 * layout.scale.max(0.01) as f32).max(1.0), 170);
        target.draw_pixmap(0, 0, shadow.as_ref(), &PixmapPaint::default(), Transform::identity(), None);
    }
    if let Some(s) = &outline {
        let p = paint(style.outline_color);
        for path in paths.iter().filter_map(|(p, _)| p.as_ref()) {
            target.stroke_path(path, &p, s, t, None);
        }
    }
    let (fill, lit_fill) = (paint(style.color), paint(style.highlight_color));
    for ((path, is_lit), g) in paths.iter().zip(&block.glyphs) {
        match path {
            Some(path) => target.fill_path(path, if *is_lit { &lit_fill } else { &fill }, FillRule::Winding, t, None),
            None => draw_color_glyph(fonts, g, target, t, style.color),
        }
    }
}

fn rounded_rect(r: Rect, radius: f32) -> Option<tiny_skia::Path> {
    let rad = radius.min(r.width() / 2.0).min(r.height() / 2.0);
    let (l, t, rr, b) = (r.left(), r.top(), r.right(), r.bottom());
    let k = 0.5523 * rad;
    let mut pb = PathBuilder::new();
    pb.move_to(l + rad, t);
    pb.line_to(rr - rad, t);
    pb.cubic_to(rr - rad + k, t, rr, t + rad - k, rr, t + rad);
    pb.line_to(rr, b - rad);
    pb.cubic_to(rr, b - rad + k, rr - rad + k, b, rr - rad, b);
    pb.line_to(l + rad, b);
    pb.cubic_to(l + rad - k, b, l, b - rad + k, l, b - rad);
    pb.line_to(l, t + rad);
    pb.cubic_to(l, t + rad - k, l + rad - k, t, l + rad, t);
    pb.close();
    pb.finish()
}

/// Softens a shadow picture (a box blur three times is close to a Gaussian) and sets its strength (0..255).
fn blur_alpha(pm: &mut Pixmap, sigma: f32, strength: u32) {
    let (w, h) = (pm.width() as usize, pm.height() as usize);
    let mut alpha: Vec<u8> = pm.pixels().iter().map(|p| p.alpha()).collect();
    let radius = ((sigma * 3.0_f32.sqrt()) as usize).max(1);
    let frame = |a: &[u8]| Frame::from_pixels(w, h, a.iter().flat_map(|&v| [v, v, v, v]).collect());
    let mut f = frame(&alpha);
    for _ in 0..3 {
        f = fig_fx::frame::blur(&f, radius);
    }
    alpha = f.pixels.chunks_exact(4).map(|p| (p[3] as u32 * strength / 255) as u8).collect();
    for (p, a) in pm.pixels_mut().iter_mut().zip(alpha) {
        *p = tiny_skia::PremultipliedColorU8::from_rgba(0, 0, 0, a).unwrap();
    }
}

fn to_frame(pm: Pixmap) -> Frame {
    // tiny-skia is premultiplied RGBA; ours is premultiplied BGRA
    let (w, h) = (pm.width() as usize, pm.height() as usize);
    Frame::from_rgba(w, h, pm.take())
}

// ---- images -----------------------------------------------------------------------------------

/// The picture behind an image clip: one frame for a still, every frame for an animation (each composed onto the
/// frames it builds on, by the decoder), with when each one ends.
pub struct LayerImage {
    frames: Vec<Pixmap>,
    /// When each frame ends, in milliseconds from the start of a loop.
    ends: Vec<f64>,
}

impl LayerImage {
    /// Frames shown for less than this (GIFs often say 0 ms) last 100 ms instead, as in browsers.
    const MIN_FRAME_MS: f64 = 20.0;
    const QUICK_FRAME_MS: f64 = 100.0;

    pub fn width(&self) -> u32 {
        self.frames[0].width()
    }

    pub fn height(&self) -> u32 {
        self.frames[0].height()
    }

    /// The frame showing `local_t` seconds into the clip (looping).
    pub fn frame_at(&self, local_t: f64) -> usize {
        if self.frames.len() == 1 {
            return 0;
        }
        let ms = (local_t.max(0.0) * 1000.0) % self.ends[self.ends.len() - 1];
        // a frame ending exactly now has given way to the next
        self.ends.partition_point(|&e| e <= ms).min(self.frames.len() - 1)
    }

    /// Decodes a picture file, or none when it can't be read.
    pub fn load(path: &Path) -> Option<LayerImage> {
        use image::AnimationDecoder;
        let bytes = std::fs::read(path).ok()?;
        let format = image::guess_format(&bytes).ok()?;
        let pixmap = |rgba: image::RgbaImage| {
            let (w, h) = rgba.dimensions();
            let mut pm = Pixmap::new(w, h)?;
            for (dst, src) in pm.pixels_mut().iter_mut().zip(rgba.pixels()) {
                *dst = tiny_skia::ColorU8::from_rgba(src[0], src[1], src[2], src[3]).premultiply();
            }
            Some(pm)
        };
        let animated: Option<Vec<image::Frame>> = match format {
            image::ImageFormat::Gif => image::codecs::gif::GifDecoder::new(std::io::Cursor::new(&bytes))
                .ok()?
                .into_frames()
                .collect_frames()
                .ok(),
            image::ImageFormat::WebP => {
                let d = image::codecs::webp::WebPDecoder::new(std::io::Cursor::new(&bytes)).ok()?;
                if d.has_animation() { d.into_frames().collect_frames().ok() } else { None }
            }
            _ => None,
        };
        if let Some(frames) = animated.filter(|f| f.len() > 1) {
            let mut elapsed = 0.0;
            let mut ends = Vec::with_capacity(frames.len());
            let mut pms = Vec::with_capacity(frames.len());
            for f in frames {
                let (num, den) = f.delay().numer_denom_ms();
                let ms = num as f64 / den.max(1) as f64;
                elapsed += if ms < Self::MIN_FRAME_MS { Self::QUICK_FRAME_MS } else { ms };
                ends.push(elapsed);
                pms.push(pixmap(f.into_buffer())?);
            }
            return Some(LayerImage { frames: pms, ends });
        }
        let still = image::load_from_memory_with_format(&bytes, format).ok()?.to_rgba8();
        Some(LayerImage { frames: vec![pixmap(still)?], ends: vec![Self::QUICK_FRAME_MS] })
    }
}

/// A picture's size fitted inside the frame (letterboxed).
fn fit(w: u32, h: u32, width: usize, height: usize) -> (f32, f32) {
    let k = (width as f64 / w as f64).min(height as f64 / h as f64);
    ((w as f64 * k) as f32, (h as f64 * k) as f32)
}

// ---- the rasterizer ---------------------------------------------------------------------------

const MAX_PICTURES: usize = 24;
const MAX_IMAGES: usize = 8;

#[derive(Default)]
struct Caches {
    pictures: HashMap<u64, Arc<Frame>>,
    picture_order: VecDeque<u64>,
    images: HashMap<String, Option<Arc<LayerImage>>>,
    image_order: VecDeque<String>,
    sizes: HashMap<u64, Option<LayerSize>>,
}

/// Draws text and image clips, caching what it has drawn. `find_media` gives a library file's path by its id.
pub struct LayerRasterizer {
    caches: Mutex<Caches>,
}

impl Default for LayerRasterizer {
    fn default() -> Self {
        Self::new()
    }
}

fn key_of(parts: impl Hash) -> u64 {
    let mut h = std::collections::hash_map::DefaultHasher::new();
    parts.hash(&mut h);
    h.finish()
}

/// A hashable picture of a style: its numbers as bits.
fn style_key(s: &TextStyle) -> String {
    format!("{s:?}")
}

fn layout_key(l: &LayerTransform) -> [u64; 4] {
    [l.center_x.to_bits(), l.center_y.to_bits(), l.scale.to_bits(), l.rotation.to_bits()]
}

impl LayerRasterizer {
    pub fn new() -> LayerRasterizer {
        LayerRasterizer { caches: Mutex::new(Caches::default()) }
    }

    /// The text's style at a moment: its keyable values (the size) as they are then, rounded so sizes too close to
    /// tell apart share one picture.
    fn style_at(clip: &Clip, body: &TextBody, local_t: f64) -> TextStyle {
        let size = (properties::TEXT_SIZE.number_at(clip, local_t) * 100.0).round() / 100.0;
        TextStyle { size_percent: size, ..body.style.clone() }
    }

    fn image(&self, path: &str) -> Option<Arc<LayerImage>> {
        if let Some(cached) = self.caches.lock().unwrap().images.get(path) {
            return cached.clone();
        }
        let image = LayerImage::load(Path::new(path)).map(Arc::new);
        let mut c = self.caches.lock().unwrap();
        if !c.images.contains_key(path) {
            c.images.insert(path.into(), image.clone());
            c.image_order.push_back(path.into());
            while c.image_order.len() > MAX_IMAGES {
                let old = c.image_order.pop_front().unwrap();
                c.images.remove(&old);
            }
        }
        image
    }

    /// A text or still clip drawn at a moment of it, transparent, premultiplied BGRA, frame-sized. `path` is an image
    /// clip's file.
    pub fn rasterize(
        &self,
        clip: &Clip,
        path: Option<&str>,
        width: usize,
        height: usize,
        local_t: f64,
    ) -> Option<Arc<Frame>> {
        if width == 0 || height == 0 {
            return None;
        }
        let layout = properties::layout_at(clip, local_t);
        let (key, draw): (u64, Box<dyn FnOnce(&mut Pixmap)>) = match &clip.body {
            ClipBody::Text(body) => {
                let style = Self::style_at(clip, body, local_t);
                // the caption word being said then, lit
                let lit = text_words::active_span(body, local_t);
                let key = key_of((&body.text, style_key(&style), layout_key(&layout), width, height, lit.clone()));
                let body = body.clone();
                (key, Box::new(move |pm| draw_text(&mut FONTS.lock().unwrap(), pm, &body, &style, &layout, lit)))
            }
            ClipBody::Image(_) => {
                let path = path.filter(|p| !p.is_empty())?;
                let image = self.image(path)?;
                // an animation's picture changes with time: which frame is part of what is shown
                let index = image.frame_at(local_t);
                let key = key_of((path, layout_key(&layout), width, height, index));
                (
                    key,
                    Box::new(move |pm| {
                        let src = &image.frames[index];
                        let (w, h) = fit(src.width(), src.height(), width, height);
                        let t = place(&layout, width, height)
                            .pre_translate(-w / 2.0, -h / 2.0)
                            .pre_scale(w / src.width() as f32, h / src.height() as f32);
                        let paint = PixmapPaint {
                            quality: FilterQuality::Bilinear,
                            blend_mode: BlendMode::SourceOver,
                            opacity: 1.0,
                        };
                        pm.draw_pixmap(0, 0, src.as_ref(), &paint, t, None);
                    }),
                )
            }
            _ => return None,
        };
        if let Some(f) = self.caches.lock().unwrap().pictures.get(&key) {
            return Some(f.clone());
        }
        let mut pm = Pixmap::new(width as u32, height as u32)?;
        draw(&mut pm);
        let picture = Arc::new(to_frame(pm));
        let mut c = self.caches.lock().unwrap();
        if !c.pictures.contains_key(&key) {
            c.pictures.insert(key, picture.clone());
            c.picture_order.push_back(key);
            while c.picture_order.len() > MAX_PICTURES {
                let old = c.picture_order.pop_front().unwrap();
                c.pictures.remove(&old);
            }
        }
        Some(picture)
    }

    /// The size of what the layer draws before its layout scales and turns it, in frame heights (the gizmo's box).
    /// None when there is nothing to draw.
    pub fn measure(
        &self,
        clip: &Clip,
        path: Option<&str>,
        width: usize,
        height: usize,
        local_t: f64,
    ) -> Option<LayerSize> {
        if width == 0 || height == 0 {
            return None;
        }
        let key = match &clip.body {
            ClipBody::Text(b) => key_of((&b.text, style_key(&b.style), width, height)),
            ClipBody::Image(_) => key_of((path?, width, height)),
            _ => return None,
        };
        if let Some(known) = self.caches.lock().unwrap().sizes.get(&key) {
            return *known;
        }
        let (w, h) = match &clip.body {
            ClipBody::Text(b) if !b.text.is_empty() => {
                let style = Self::style_at(clip, b, local_t);
                lay_out(&mut FONTS.lock().unwrap(), &b.text, &style, width, height, None).bounds()
            }
            ClipBody::Image(_) => match self.image(path?) {
                Some(img) => fit(img.width(), img.height(), width, height),
                None => (0.0, 0.0),
            },
            _ => (0.0, 0.0),
        };
        let size = (w > 0.0 && h > 0.0)
            .then(|| LayerSize { width: w as f64 / height as f64, height: h as f64 / height as f64 });
        let mut c = self.caches.lock().unwrap();
        if c.sizes.len() > 256 {
            c.sizes.clear();
        }
        c.sizes.insert(key, size);
        size
    }
}
