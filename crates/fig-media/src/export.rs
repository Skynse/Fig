//! Writing the timeline to a video: every frame described as a scene and drawn by the same code as the preview, then
//! encoded in order with the mixed sound.

use std::path::Path;
use std::sync::Arc;

use fig_render::layers::LayerRasterizer;
use fig_timeline::{MediaAsset, Timeline};

use crate::encode::{EncoderChoice, Mp4Writer, VideoSettings};
use crate::mixer::{AudioMixer, SAMPLE_RATE};
use crate::{MediaError, MediaSources};

/// How the finished video lies in the file: as edited, or turned a quarter so a wide edit fills a phone held sideways.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub enum ExportTurn {
    #[default]
    None,
    /// Turned clockwise: watched with the phone turned to the left.
    Right,
    /// Turned anticlockwise: watched with the phone turned to the right.
    Left,
}

#[derive(Clone, Debug)]
pub struct ExportSettings {
    pub width: u32,
    pub height: u32,
    /// Constant quality, 0 best to 51 worst.
    pub quality: u32,
    pub encoder: EncoderChoice,
    pub turn: ExportTurn,
    /// Only this part of the timeline (the file still starts at 0).
    pub range: Option<(f64, f64)>,
}

/// Where the last enabled clip ends.
pub fn timeline_end_sec(timeline: &Timeline) -> f64 {
    timeline.clips().filter(|c| c.enabled).map(|c| c.end_sec()).fold(0.0, f64::max)
}

/// Writes the timeline to an MP4 (H.264 and AAC). `progress` gets 0..1; `cancel` stops it (the file is removed).
/// Returns the encoder used, for people.
pub fn export(
    timeline: &Timeline,
    media: &[MediaAsset],
    out: &Path,
    s: &ExportSettings,
    layers: Arc<LayerRasterizer>,
    progress: &mut dyn FnMut(f64),
    cancel: &dyn Fn() -> bool,
) -> Result<&'static str, MediaError> {
    let mut end = timeline_end_sec(timeline);
    if end <= 0.0 {
        return Err(MediaError::Other("Timeline is empty".into()));
    }
    let (start, stop) = s.range.unwrap_or((0.0, end));
    let start = start.clamp(0.0, end);
    end = stop.clamp(start, end);
    let length = end - start;
    if length <= 0.0 {
        return Err(MediaError::Other("The selected range is empty".into()));
    }
    let (w, h) = (((s.width & !1).max(2)) as usize, ((s.height & !1).max(2)) as usize);
    // the picture is composed as edited; turned, the file is the other way up
    let (ow, oh) = if s.turn == ExportTurn::None { (w, h) } else { (h, w) };
    let rate = timeline.rate;
    let mut writer = Mp4Writer::create(out)?;
    let label = writer.add_video(&VideoSettings {
        width: ow as u32,
        height: oh as u32,
        rate,
        encoder: s.encoder,
        quality: s.quality,
        gop: (rate.fps() * 2.0).round().max(1.0) as u32,
        preset: None,
        tune: None,
    })?;
    writer.add_audio()?;
    let result = (|| {
        let mut sources = MediaSources::new(media.to_vec(), layers);
        let mut mixer = AudioMixer::new();
        let total = ((length * rate.fps()).round() as i64).max(1);
        // frame f is at start + f × den / num: the timeline's exact rate
        let mut audio_until = start;
        for f in 0..total {
            if cancel() {
                return Err(MediaError::Other("cancelled".into()));
            }
            let t = start + rate.to_seconds(f);
            let scene = fig_render::build_scene(timeline, media, t, w, h, &mut sources);
            let frame = fig_render::render(&scene);
            match s.turn {
                ExportTurn::None => writer.write_bgra(&frame.pixels, w * 4)?,
                turn => writer.write_bgra(&turned(&frame.pixels, w, h, turn), ow * 4)?,
            }
            // the sound keeps pace with the picture, a frame's worth at a time
            let next = (start + rate.to_seconds(f + 1)).min(end);
            if next > audio_until {
                let samples = mixer.mix(timeline, media, audio_until, next - audio_until);
                writer.write_audio(&samples)?;
                audio_until += samples.len() as f64 / 2.0 / SAMPLE_RATE as f64;
            }
            progress(((t - start) / length).min(1.0));
        }
        Ok(())
    })();
    match result {
        Ok(()) => {
            writer.finish()?;
            progress(1.0);
            Ok(label)
        }
        Err(e) => {
            drop(writer);
            let _ = std::fs::remove_file(out);
            Err(e)
        }
    }
}

/// A `w` × `h` picture turned a quarter into an `h` × `w` one: turned right, its top lies along the right side.
fn turned(src: &[u8], w: usize, h: usize, turn: ExportTurn) -> Vec<u8> {
    let mut dst = vec![0u8; src.len()];
    for y in 0..h {
        for x in 0..w {
            let (dx, dy) = if turn == ExportTurn::Right { (h - 1 - y, x) } else { (y, w - 1 - x) };
            let (s, d) = ((y * w + x) * 4, (dy * h + dx) * 4);
            dst[d..d + 4].copy_from_slice(&src[s..s + 4]);
        }
    }
    dst
}
