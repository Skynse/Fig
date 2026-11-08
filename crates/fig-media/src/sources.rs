//! The pictures a scene is built from, for the preview and export alike: a decoder per clip (so two clips of one file
//! don't fight over its position), and the layer rasterizer for titles and stills.

use std::collections::HashMap;
use std::path::Path;
use std::sync::Arc;

use fig_fx::Frame;
use fig_render::SceneSources;
use fig_render::layers::LayerRasterizer;
use fig_timeline::{Clip, MediaAsset};

use crate::{DecodeMode, VideoSource};

pub struct MediaSources {
    decoders: HashMap<(String, String), Option<VideoSource>>,
    pub layers: Arc<LayerRasterizer>,
    /// The library, for an image clip's file.
    pub media: Vec<MediaAsset>,
    pub mode: DecodeMode,
    /// Exports read the original file; the preview its proxy when it has one.
    pub use_proxies: bool,
}

impl MediaSources {
    pub fn new(media: Vec<MediaAsset>, layers: Arc<LayerRasterizer>) -> MediaSources {
        MediaSources { decoders: HashMap::new(), layers, media, mode: DecodeMode::Playback, use_proxies: false }
    }

    /// Closes decoders of clips no longer on the timeline.
    pub fn retain_clips(&mut self, keep: impl Fn(&str) -> bool) {
        self.decoders.retain(|(clip, _), _| keep(clip));
    }

    pub fn clear(&mut self) {
        self.decoders.clear();
    }
}

impl SceneSources for MediaSources {
    fn video(
        &mut self,
        clip: &Clip,
        asset: &MediaAsset,
        source_sec: f64,
        span_sec: f64,
        width: usize,
        height: usize,
    ) -> Option<Arc<Frame>> {
        let path = if self.use_proxies { asset.playback_video_path() } else { asset.url.as_str() };
        let key = (clip.id.clone(), format!("{path}|{width}x{height}"));
        let decoder = self
            .decoders
            .entry(key)
            .or_insert_with(|| VideoSource::open(Path::new(path), width, height).ok())
            .as_mut()?;
        let v = clip.as_video()?;
        // a blended frame sits up to a frame before the one decoded last: only seek back further than that
        let slack = if v.smooths() { 0.1 } else { 0.05 };
        if decoder.last_presented_sec() < 0.0 || source_sec < decoder.last_presented_sec() - slack {
            decoder.seek(source_sec);
        }
        if v.smooths() {
            decoder.decode_smoothed(source_sec, span_sec, v.blend_frames, v.motion_blur, self.mode)
        } else {
            decoder.decode_forward(source_sec, self.mode)
        }
    }

    fn layer(&mut self, clip: &Clip, width: usize, height: usize, local_t: f64) -> Option<Arc<Frame>> {
        let path = clip.as_image().and_then(|i| self.media.iter().find(|m| m.id == i.source_id)).map(|m| m.url.clone());
        self.layers.rasterize(clip, path.as_deref(), width, height, local_t)
    }
}
