//! Colour glyphs from a font's `COLR` table (v0 layers and v1 paint graphs), painted with tiny-skia. swash draws
//! COLRv0 and bitmap emoji but not COLRv1, which is the only colour emoji font Fedora ships (Noto Color Emoji), so
//! titles and captions get their emoji from here.

use skrifa::color::{Brush, ColorPainter, ColorStop, CompositeMode, Extend, Transform as FontTransform};
use skrifa::outline::{DrawSettings, OutlinePen};
use skrifa::prelude::{LocationRef, Size};
use skrifa::raw::types::BoundingBox;
use skrifa::{FontRef, GlyphId, MetadataProvider};
use tiny_skia::{
    BlendMode, Color, FillRule, GradientStop, LinearGradient, Mask, Paint, PathBuilder, Pixmap, PixmapPaint,
    Point, RadialGradient, Rect, Shader, SpreadMode, Transform,
};

/// A painted colour glyph and where its top-left sits from the pen position (pixels).
pub struct Painted {
    pub pixmap: Pixmap,
    pub left: f32,
    pub top: f32,
}

/// Paints glyph `glyph` of the font at `size` pixels, or None when it has no colour glyph. `foreground` fills the
/// parts the font leaves to the text colour (palette index 0xFFFF).
pub fn paint(data: &[u8], index: u32, glyph: u16, size: f32, foreground: Color) -> Option<Painted> {
    let font = FontRef::from_index(data, index).ok()?;
    let color = font.color_glyphs().get(GlyphId::new(u32::from(glyph)))?;
    let upem = f32::from(font.metrics(Size::unscaled(), LocationRef::default()).units_per_em.max(1));
    let scale = size / upem;
    // room for the emoji square and a little overhang; the pen is at the baseline
    let (w, h) = ((size * 1.4).ceil() as u32, (size * 1.4).ceil() as u32);
    let (left, top) = (-size * 0.2, -size * 1.1);
    let base = Transform::from_row(scale, 0.0, 0.0, -scale, -left, -top);
    let palette: Vec<Color> = font
        .color_palettes()
        .get(0)
        .map(|p| p.colors().iter().map(|c| Color::from_rgba8(c.red, c.green, c.blue, c.alpha)).collect())
        .unwrap_or_default();
    let mut painter = Painter {
        font: font.clone(),
        palette,
        foreground,
        layers: vec![(Pixmap::new(w, h)?, CompositeMode::SrcOver)],
        transforms: vec![base],
        clips: Vec::new(),
    };
    color.paint(LocationRef::default(), &mut painter).ok()?;
    let pixmap = painter.layers.pop()?.0;
    Some(Painted { pixmap, left, top })
}

struct Painter<'a> {
    font: FontRef<'a>,
    palette: Vec<Color>,
    foreground: Color,
    /// The layer being drawn into is the last; each was opened with the mode it merges down with.
    layers: Vec<(Pixmap, CompositeMode)>,
    /// Font units to pixels, as pushed so far.
    transforms: Vec<Transform>,
    clips: Vec<Mask>,
}

fn to_skia(t: FontTransform) -> Transform {
    Transform::from_row(t.xx, t.yx, t.xy, t.yy, t.dx, t.dy)
}

struct Pen(PathBuilder);

impl OutlinePen for Pen {
    fn move_to(&mut self, x: f32, y: f32) {
        self.0.move_to(x, y);
    }
    fn line_to(&mut self, x: f32, y: f32) {
        self.0.line_to(x, y);
    }
    fn quad_to(&mut self, cx0: f32, cy0: f32, x: f32, y: f32) {
        self.0.quad_to(cx0, cy0, x, y);
    }
    fn curve_to(&mut self, cx0: f32, cy0: f32, cx1: f32, cy1: f32, x: f32, y: f32) {
        self.0.cubic_to(cx0, cy0, cx1, cy1, x, y);
    }
    fn close(&mut self) {
        self.0.close();
    }
}

impl Painter<'_> {
    fn transform(&self) -> Transform {
        *self.transforms.last().unwrap_or(&Transform::identity())
    }

    fn size(&self) -> (u32, u32) {
        let p = &self.layers[0].0;
        (p.width(), p.height())
    }

    fn color(&self, index: u16, alpha: f32) -> Color {
        let mut c = if index == 0xFFFF { self.foreground } else { self.palette.get(usize::from(index)).copied().unwrap_or(Color::BLACK) };
        c.apply_opacity(alpha);
        c
    }

    fn stops(&self, stops: &[ColorStop]) -> Vec<GradientStop> {
        stops.iter().map(|s| GradientStop::new(s.offset.clamp(0.0, 1.0), self.color(s.palette_index, s.alpha))).collect()
    }

    /// A mask of `path` (font units) under the current transform, cut by the clip already in force.
    fn push_mask(&mut self, path: Option<tiny_skia::Path>) {
        let (w, h) = self.size();
        let mut mask = Mask::new(w, h).expect("mask");
        if let Some(path) = path {
            mask.fill_path(&path, FillRule::Winding, true, self.transform());
        }
        if let Some(outer) = self.clips.last() {
            for (a, b) in mask.data_mut().iter_mut().zip(outer.data()) {
                *a = ((u16::from(*a) * u16::from(*b)) / 255) as u8;
            }
        }
        self.clips.push(mask);
    }
}

fn spread(e: Extend) -> SpreadMode {
    match e {
        Extend::Repeat => SpreadMode::Repeat,
        Extend::Reflect => SpreadMode::Reflect,
        _ => SpreadMode::Pad,
    }
}

fn blend(mode: CompositeMode) -> BlendMode {
    match mode {
        CompositeMode::Clear => BlendMode::Clear,
        CompositeMode::Src => BlendMode::Source,
        CompositeMode::Dest => BlendMode::Destination,
        CompositeMode::DestOver => BlendMode::DestinationOver,
        CompositeMode::SrcIn => BlendMode::SourceIn,
        CompositeMode::DestIn => BlendMode::DestinationIn,
        CompositeMode::SrcOut => BlendMode::SourceOut,
        CompositeMode::DestOut => BlendMode::DestinationOut,
        CompositeMode::SrcAtop => BlendMode::SourceAtop,
        CompositeMode::DestAtop => BlendMode::DestinationAtop,
        CompositeMode::Xor => BlendMode::Xor,
        CompositeMode::Plus => BlendMode::Plus,
        CompositeMode::Screen => BlendMode::Screen,
        CompositeMode::Overlay => BlendMode::Overlay,
        CompositeMode::Darken => BlendMode::Darken,
        CompositeMode::Lighten => BlendMode::Lighten,
        CompositeMode::ColorDodge => BlendMode::ColorDodge,
        CompositeMode::ColorBurn => BlendMode::ColorBurn,
        CompositeMode::HardLight => BlendMode::HardLight,
        CompositeMode::SoftLight => BlendMode::SoftLight,
        CompositeMode::Difference => BlendMode::Difference,
        CompositeMode::Exclusion => BlendMode::Exclusion,
        CompositeMode::Multiply => BlendMode::Multiply,
        CompositeMode::HslHue => BlendMode::Hue,
        CompositeMode::HslSaturation => BlendMode::Saturation,
        CompositeMode::HslColor => BlendMode::Color,
        CompositeMode::HslLuminosity => BlendMode::Luminosity,
        _ => BlendMode::SourceOver,
    }
}

impl ColorPainter for Painter<'_> {
    fn push_transform(&mut self, t: FontTransform) {
        let next = self.transform().pre_concat(to_skia(t));
        self.transforms.push(next);
    }

    fn pop_transform(&mut self) {
        if self.transforms.len() > 1 {
            self.transforms.pop();
        }
    }

    fn push_clip_glyph(&mut self, glyph: GlyphId) {
        let mut pen = Pen(PathBuilder::new());
        if let Some(outline) = self.font.outline_glyphs().get(glyph) {
            let _ = outline.draw(DrawSettings::unhinted(Size::unscaled(), LocationRef::default()), &mut pen);
        }
        self.push_mask(pen.0.finish());
    }

    fn push_clip_box(&mut self, b: BoundingBox<f32>) {
        let rect = Rect::from_ltrb(b.x_min, b.y_min, b.x_max, b.y_max).map(PathBuilder::from_rect);
        self.push_mask(rect);
    }

    fn pop_clip(&mut self) {
        self.clips.pop();
    }

    fn fill(&mut self, brush: Brush<'_>) {
        let t = self.transform();
        let shader = match brush {
            Brush::Solid { palette_index, alpha } => Shader::SolidColor(self.color(palette_index, alpha)),
            Brush::LinearGradient { p0, p1, color_stops, extend } => {
                let stops = self.stops(color_stops);
                match stops.len() {
                    0 => return,
                    1 => Shader::SolidColor(self.color(color_stops[0].palette_index, color_stops[0].alpha)),
                    _ => LinearGradient::new(Point::from_xy(p0.x, p0.y), Point::from_xy(p1.x, p1.y), stops, spread(extend), t)
                        .unwrap_or(Shader::SolidColor(Color::TRANSPARENT)),
                }
            }
            Brush::RadialGradient { c0, r0, c1, r1, color_stops, extend } => {
                let stops = self.stops(color_stops);
                match stops.len() {
                    0 => return,
                    1 => Shader::SolidColor(self.color(color_stops[0].palette_index, color_stops[0].alpha)),
                    _ => RadialGradient::new(Point::from_xy(c0.x, c0.y), r0.max(0.0), Point::from_xy(c1.x, c1.y), r1.max(0.0), stops, spread(extend), t)
                        .unwrap_or(Shader::SolidColor(Color::TRANSPARENT)),
                }
            }
            // no sweep gradients in tiny-skia: the colour halfway round stands in
            Brush::SweepGradient { color_stops, .. } => match color_stops.get(color_stops.len() / 2) {
                Some(s) => Shader::SolidColor(self.color(s.palette_index, s.alpha)),
                None => return,
            },
        };
        let paint = Paint { shader, anti_alias: true, ..Default::default() };
        let (w, h) = self.size();
        let rect = Rect::from_xywh(0.0, 0.0, w as f32, h as f32).expect("rect");
        let clip = self.clips.last();
        let target = &mut self.layers.last_mut().expect("layer").0;
        target.fill_rect(rect, &paint, Transform::identity(), clip);
    }

    fn push_layer(&mut self, mode: CompositeMode) {
        let (w, h) = self.size();
        self.layers.push((Pixmap::new(w, h).expect("layer"), mode));
    }

    fn pop_layer(&mut self) {
        if self.layers.len() < 2 {
            return;
        }
        let (layer, mode) = self.layers.pop().expect("layer");
        let below = &mut self.layers.last_mut().expect("layer").0;
        below.draw_pixmap(0, 0, layer.as_ref(), &PixmapPaint { blend_mode: blend(mode), ..Default::default() }, Transform::identity(), None);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Paints 😀 from the system's colour emoji font, when it is a COLR one, and checks colour came out. (Ubuntu's
    /// Noto Color Emoji is the bitmap build, which `layers` draws without this module.)
    #[test]
    fn paints_a_colour_emoji() {
        use skrifa::raw::TableProvider;
        let mut db = cosmic_text::fontdb::Database::new();
        db.load_system_fonts();
        let Some(face) = db.faces().find(|f| f.families.iter().any(|(n, _)| n == "Noto Color Emoji")) else { return };
        let is_colr = db
            .with_face_data(face.id, |data, index| FontRef::from_index(data, index).is_ok_and(|f| f.colr().is_ok()))
            .unwrap_or(false);
        if !is_colr {
            return;
        }
        let painted = db
            .with_face_data(face.id, |data, index| {
                let font = FontRef::from_index(data, index).ok()?;
                let glyph = font.charmap().map('😀')?;
                paint(data, index, glyph.to_u32() as u16, 64.0, Color::WHITE)
            })
            .flatten()
            .expect("the emoji paints");
        let coloured = painted.pixmap.pixels().iter().filter(|p| p.alpha() > 200 && p.red() > 150 && p.blue() < 120).count();
        assert!(coloured > 200, "only {coloured} yellow pixels");
    }
}
