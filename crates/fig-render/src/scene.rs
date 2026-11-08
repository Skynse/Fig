//! A finished frame described before it is drawn: its layers, bottom of the stack first, each with its effects,
//! crop, transition and opacity resolved for that one moment.

use std::sync::Arc;

use fig_fx::effects::{self, EffectCall};
use fig_fx::transitions;
use fig_fx::{Frame, frame};
use fig_timeline::{Clip, ClipBody, MediaAsset, ParamMap, Timeline, automation, clip_time, fade};

/// Crop insets from each edge as fractions of the frame (0..1).
#[derive(Clone, Copy, Debug, PartialEq, Default)]
pub struct SceneCrop {
    pub left: f64,
    pub top: f64,
    pub right: f64,
    pub bottom: f64,
}

impl SceneCrop {
    pub fn is_none(&self) -> bool {
        self.left <= 1e-6 && self.top <= 1e-6 && self.right <= 1e-6 && self.bottom <= 1e-6
    }

    /// The crop in whole pixels (x, y, w, h), or none (also when it would leave nothing).
    pub fn to_pixels(&self, width: usize, height: usize) -> Option<(usize, usize, usize, usize)> {
        if self.is_none() || width == 0 || height == 0 {
            return None;
        }
        let r = |v: f64| v.round_ties_even() as i64;
        let (x, y) = (r(self.left * width as f64), r(self.top * height as f64));
        let (w, h) =
            (r((1.0 - self.left - self.right) * width as f64), r((1.0 - self.top - self.bottom) * height as f64));
        (w >= 1 && h >= 1).then_some((x.max(0) as usize, y.max(0) as usize, w as usize, h as usize))
    }
}

/// One picture, frame-sized, with the effects and crop that apply to it.
#[derive(Clone)]
pub struct RenderSource {
    pub frame: Arc<Frame>,
    pub effects: Vec<EffectCall>,
    pub crop: SceneCrop,
    /// A drawn layer (text, a still): premultiplied, its alpha saying where it covers what is below. Decoded video
    /// is opaque.
    pub has_alpha: bool,
}

/// One layer: a single source, or a transition between two. Opacity already includes fades.
#[derive(Clone)]
pub struct RenderLayer {
    pub source: RenderSource,
    pub transition: Option<LayerTransition>,
    /// Track effects, applied after the crop and any transition.
    pub effects: Vec<EffectCall>,
    pub opacity: f64,
}

#[derive(Clone)]
pub struct LayerTransition {
    pub incoming: RenderSource,
    pub type_id: String,
    pub progress: f64,
    pub params: ParamMap,
}

/// Everything needed to draw one frame: the layers, bottom of the stack first.
#[derive(Clone)]
pub struct RenderScene {
    pub width: usize,
    pub height: usize,
    pub layers: Vec<RenderLayer>,
}

/// Where a scene's pictures come from: decoded video (FFmpeg, in `fig-media`) and drawn layers (text and stills).
pub trait SceneSources {
    /// The picture of a video clip showing `source_sec` of its file, frame-sized. `span_sec` is how much of the file
    /// one project frame covers there (for frame blending and motion blur).
    fn video(
        &mut self,
        clip: &Clip,
        asset: &MediaAsset,
        source_sec: f64,
        span_sec: f64,
        width: usize,
        height: usize,
    ) -> Option<Arc<Frame>>;

    /// A text or still clip drawn at a moment of it, transparent, premultiplied, frame-sized.
    fn layer(&mut self, clip: &Clip, width: usize, height: usize, local_t: f64) -> Option<Arc<Frame>>;
}

/// The clip's crop at a moment (keyframes sampled); none for clips that can't be cropped.
pub fn crop_at(clip: &Clip, local_t: f64) -> SceneCrop {
    let t = local_t.clamp(0.0, (clip.dur_sec - 1e-4).max(0.0));
    let (left, top, right, bottom) = automation::sample_crop(clip, t);
    SceneCrop { left, top, right, bottom }
}

fn is_layer(clip: &Clip) -> bool {
    matches!(clip.body, ClipBody::Text(_) | ClipBody::Image(_))
}

/// The picture a clip shows at timeline time `t`: a video's decoded frame, or a still or title drawn (held at its
/// nearest end while a transition runs past it). With the time into the clip.
fn picture(
    clip: &Clip,
    t: f64,
    timeline: &Timeline,
    media: &[MediaAsset],
    sources: &mut dyn SceneSources,
    width: usize,
    height: usize,
) -> Option<(Arc<Frame>, f64)> {
    let local = t - clip.start_sec;
    if is_layer(clip) {
        return Some((sources.layer(clip, width, height, local.clamp(0.0, (clip.dur_sec - 1e-4).max(0.0)))?, local));
    }
    let ClipBody::Video(v) = &clip.body else { return None };
    let asset = media.iter().find(|m| m.id == v.source_id).filter(|a| !a.url.is_empty() && !a.offline)?;
    let start = asset.source_start_sec;
    let end = (start + (asset.duration_sec - 1e-4).max(0.0)).max(start);
    let source_sec = clip_time::source_at(clip, local).clamp(start, end);
    let span = clip_time::span_at(clip, local, 1.0 / timeline.rate.fps().max(1.0));
    Some((sources.video(clip, asset, source_sec, span, width, height)?, local))
}

fn source_of(clip: &Clip, frame: Arc<Frame>, local: f64) -> RenderSource {
    RenderSource {
        frame,
        effects: effects::resolve_stack(&clip.effects, local),
        crop: crop_at(clip, local),
        has_alpha: is_layer(clip),
    }
}

/// What is on screen at timeline time `t`, described layer by layer. Nothing is drawn here.
pub fn build_scene(
    timeline: &Timeline,
    media: &[MediaAsset],
    t: f64,
    width: usize,
    height: usize,
    sources: &mut dyn SceneSources,
) -> RenderScene {
    let mut layers = Vec::new();
    // paint from the lowest track up: a track higher on the timeline covers the ones below
    for (index, track) in timeline.bottom_up() {
        let track_effects = effects::resolve_stack(&track.effects, t);
        if let Some(active) = transitions::find_active_on(timeline, index, t)
            && transitions::find(&active.type_id).is_some()
        {
            let (out_clip, in_clip) = (&track.clips[active.outgoing], &track.clips[active.incoming]);
            let out = picture(out_clip, t, timeline, media, sources, width, height);
            let inc = picture(in_clip, t, timeline, media, sources, width, height);
            if let (Some((of, ol)), Some((inf, il))) = (out, inc) {
                let oo = fade::effective_opacity(out_clip, ol.clamp(0.0, out_clip.dur_sec));
                let io = fade::effective_opacity(in_clip, il.clamp(0.0, in_clip.dur_sec));
                layers.push(RenderLayer {
                    source: source_of(out_clip, of, ol),
                    transition: Some(LayerTransition {
                        incoming: source_of(in_clip, inf, il),
                        type_id: active.type_id.clone(),
                        progress: active.progress,
                        params: active.params.clone(),
                    }),
                    effects: track_effects,
                    opacity: oo + (io - oo) * active.progress,
                });
                continue;
            }
        }
        let mut on_track = Vec::new();
        for clip in &track.clips {
            if !clip.enabled || t < clip.start_sec || t >= clip.end_sec() {
                continue;
            }
            let Some((frame, local)) = picture(clip, t, timeline, media, sources, width, height) else { continue };
            on_track.push(RenderLayer {
                source: source_of(clip, frame, local),
                transition: None,
                effects: track_effects.clone(),
                opacity: fade::effective_opacity(clip, local),
            });
        }
        // clips earlier in a track sit above later ones
        on_track.reverse();
        layers.extend(on_track);
    }
    RenderScene { width, height, layers }
}

/// Draws a scene into a new opaque frame.
pub fn render(scene: &RenderScene) -> Frame {
    let (w, h) = (scene.width, scene.height);
    let mut canvas = Frame::solid(w, h, 0xFF000000);
    for layer in &scene.layers {
        let (picture, premultiplied, crop) = prepare(layer, w, h);
        let Some(picture) = picture else { continue };
        let pixels = match crop {
            Some(c) => crop_scale(&picture, c),
            None => Arc::try_unwrap(picture).unwrap_or_else(|a| (*a).clone()),
        };
        crate::compose::blend(&mut canvas, &pixels, layer.opacity, premultiplied);
    }
    canvas
}

/// A layer's picture after its effects, transition and track effects; with whether it is premultiplied, and the crop
/// still to apply (a crop the compositor can do while it scales).
fn prepare(
    layer: &RenderLayer,
    w: usize,
    h: usize,
) -> (Option<Arc<Frame>>, bool, Option<(usize, usize, usize, usize)>) {
    let src = &layer.source;
    if src.frame.width != w || src.frame.height != h || layer.opacity <= 0.0 {
        return (None, false, None);
    }
    let with_effects = |s: &RenderSource| -> Arc<Frame> {
        match effects::apply_calls(&s.frame, &s.effects) {
            Some(f) => Arc::new(f),
            None => s.frame.clone(),
        }
    };
    if let Some(tx) = &layer.transition
        && let Some(entry) = transitions::find(&tx.type_id)
    {
        let mut a = cropped(with_effects(src), src.crop);
        let mut b = cropped(with_effects(&tx.incoming), tx.incoming.crop);
        // a still has real transparency; video beside it must count as fully opaque in the blend
        let alpha = src.has_alpha || tx.incoming.has_alpha;
        if alpha && !src.has_alpha {
            Arc::make_mut(&mut a).make_opaque();
        }
        if alpha && !tx.incoming.has_alpha {
            Arc::make_mut(&mut b).make_opaque();
        }
        let blended = entry.blend(&a, &b, tx.progress, &tx.params);
        let out = effects::apply_calls(&blended, &layer.effects).unwrap_or(blended);
        return (Some(Arc::new(out)), alpha, None);
    }
    let pic = with_effects(src);
    if !layer.effects.is_empty() && !src.has_alpha {
        // track effects see the cropped picture, so the crop cannot wait for the compositor
        let pic = cropped(pic, src.crop);
        let out = effects::apply_calls(&pic, &layer.effects).map(Arc::new).unwrap_or(pic);
        return (Some(out), false, None);
    }
    // a drawn layer keeps its transparency, so its track effects run on it directly
    let pic = if src.has_alpha { effects::apply_calls(&pic, &layer.effects).map(Arc::new).unwrap_or(pic) } else { pic };
    (Some(pic), src.has_alpha, src.crop.to_pixels(w, h).filter(|c| meaningful(*c, w, h)))
}

fn meaningful((x, y, cw, ch): (usize, usize, usize, usize), w: usize, h: usize) -> bool {
    cw > 0 && ch > 0 && !(x == 0 && y == 0 && cw >= w && ch >= h)
}

fn cropped(f: Arc<Frame>, crop: SceneCrop) -> Arc<Frame> {
    match crop.to_pixels(f.width, f.height).filter(|c| meaningful(*c, f.width, f.height)) {
        Some(c) => Arc::new(crop_scale(&f, c)),
        None => f,
    }
}

/// The cropped region scaled back up to the whole frame (nearest neighbour).
pub fn crop_scale(src: &Frame, (cx, cy, cw, ch): (usize, usize, usize, usize)) -> Frame {
    let (w, h) = (src.width, src.height);
    let cx = cx.min(w.saturating_sub(1));
    let cy = cy.min(h.saturating_sub(1));
    let cw = cw.clamp(1, w - cx);
    let ch = ch.clamp(1, h - cy);
    frame::render_rows(src, |y, row| {
        let sy = cy + y * ch / h;
        let from = &src.pixels[sy * w * 4..(sy + 1) * w * 4];
        for (x, o) in row.chunks_exact_mut(4).enumerate() {
            let sx = (cx + x * cw / w) * 4;
            o.copy_from_slice(&from[sx..sx + 4]);
        }
    })
}
