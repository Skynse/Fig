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

#[cfg(test)]
mod tests {
    use std::path::PathBuf;
    use std::sync::Arc;

    use crate::encode::EncoderChoice;
    use crate::export::{ExportSettings, ExportTurn, export};
    use fig_render::layers::LayerRasterizer;
    use fig_timeline::{Clip, FrameRate, TextBody, Timeline, Track, TrackKind, VideoBody};

    fn timeline_with_clip() -> (Timeline, Vec<fig_timeline::MediaAsset>) {
        let path =
            PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../tests/assets/3 seconds timer [fxqE27gIZcc].webm");
        let mut asset = crate::probe::probe(&path).unwrap();
        asset.id = "m".into();
        let mut video = Track::new(TrackKind::Video);
        video.clips.push(
            Clip::new(VideoBody {
                source_id: "m".into(),
                src_in_sec: 0.0,
                src_out_sec: 1.0,
                plays_audio: true,
                ..Default::default()
            })
            .at(0.0, 1.0),
        );
        let mut titles = Track::new(TrackKind::Video);
        titles.clips.push(Clip::new(TextBody { text: "Fig".into(), ..Default::default() }).at(0.2, 0.6));
        let tl = Timeline { rate: FrameRate::new(30, 1), tracks: vec![titles, video], ..Default::default() };
        (tl, vec![asset])
    }

    fn probe_out(path: &std::path::Path) -> String {
        let out = std::process::Command::new("ffprobe")
            .args([
                "-v",
                "error",
                "-show_entries",
                "stream=codec_name,width,height,nb_read_frames",
                "-count_frames",
                "-of",
                "csv=p=0",
            ])
            .arg(path)
            .output()
            .unwrap();
        String::from_utf8_lossy(&out.stdout).into_owned()
    }

    #[test]
    fn a_timeline_exports_to_h264_and_aac() {
        let (tl, media) = timeline_with_clip();
        let dir = tempfile::tempdir().unwrap();
        let out = dir.path().join("out.mp4");
        let mut last = 0.0;
        let settings = ExportSettings {
            width: 320,
            height: 180,
            quality: 23,
            encoder: EncoderChoice::Software,
            turn: ExportTurn::None,
            range: None,
        };
        let label =
            export(&tl, &media, &out, &settings, Arc::new(LayerRasterizer::new()), &mut |p| last = p, &|| false)
                .unwrap();
        assert_eq!(label, "CPU (x264)");
        assert_eq!(last, 1.0);
        let info = probe_out(&out);
        assert!(info.contains("h264,320,180,30"), "{info}");
        assert!(info.contains("aac"), "{info}");
        assert!(crate::probe::is_complete_mp4(&out));
    }

    #[test]
    fn the_automatic_encoder_falls_back_to_one_that_opens() {
        let (tl, media) = timeline_with_clip();
        let dir = tempfile::tempdir().unwrap();
        let out = dir.path().join("turned.mp4");
        let settings = ExportSettings {
            width: 320,
            height: 180,
            quality: 23,
            encoder: EncoderChoice::Automatic,
            turn: ExportTurn::Right,
            range: Some((0.0, 0.5)),
        };
        let label =
            export(&tl, &media, &out, &settings, Arc::new(LayerRasterizer::new()), &mut |_| {}, &|| false).unwrap();
        println!("encoder: {label}");
        let info = probe_out(&out);
        assert!(info.contains("h264,180,320,15"), "{info}");
    }

    #[test]
    fn a_reversed_copy_plays_backwards() {
        let path = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../tests/segments/segment_1.mp4");
        let dir = tempfile::tempdir().unwrap();
        let out = dir.path().join("rev.mp4");
        crate::reverse::reverse(&path, &out, &mut |_| {}, &|| false).unwrap();
        let src = crate::probe::probe(&path).unwrap();
        let rev = crate::probe::probe(&out).unwrap();
        assert!((src.duration_sec - rev.duration_sec).abs() < 0.1, "{} vs {}", src.duration_sec, rev.duration_sec);
        // the copy's first frame is the source's last
        let (w, h) = (64, 64);
        let last = crate::artifacts::frame_at(&path, src.duration_sec - 0.01, w, h).unwrap();
        let first = crate::artifacts::frame_at(&out, 0.0, w, h).unwrap();
        let diff: f64 = last.pixels.iter().zip(&first.pixels).map(|(a, b)| (*a as f64 - *b as f64).abs()).sum::<f64>()
            / last.pixels.len() as f64;
        assert!(diff < 8.0, "mean difference {diff}");
    }
}
