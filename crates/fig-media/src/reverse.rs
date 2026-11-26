//! Reversed copies: a file played backwards, the picture and its sound, for clips the user reverses. Time `t` in the
//! copy is time `length - t` in the source. The source is decoded in chunks from its end, so memory stays bounded
//! however long it is.

use std::path::Path;

use ffmpeg_next as ff;
use fig_timeline::{FrameRate, MediaKind};

use crate::encode::{EncoderChoice, Mp4Writer, VideoSettings};
use crate::mixer::SAMPLE_RATE;
use crate::{AudioSource, DecodeMode, MediaError, VideoSource};

/// The most decoded picture held at once while reversing.
const FRAME_BUDGET_BYTES: usize = 192 * 1024 * 1024;
/// Sound is reversed in pieces this long.
const AUDIO_CHUNK_SEC: f64 = 1.0;

/// Every video frame's time in the file, read from its packets (no decoding), in order.
fn frame_times(path: &Path) -> Result<Vec<f64>, MediaError> {
    let mut input = ff::format::input(path).map_err(|e| MediaError::open(path, e))?;
    let st = input.streams().best(ff::media::Type::Video).ok_or(MediaError::NoStream("video"))?;
    let (index, tb) = (st.index(), f64::from(st.time_base()));
    let mut ts: Vec<i64> = Vec::new();
    let mut packet = ff::Packet::empty();
    while packet.read(&mut input).is_ok() {
        if packet.stream() == index
            && let Some(t) = packet.pts().or(packet.dts())
        {
            ts.push(t);
        }
    }
    ts.sort_unstable();
    ts.dedup();
    Ok(ts.into_iter().map(|t| t as f64 * tb).collect())
}

/// Writes `source` played backwards to `out` (H.264 and AAC in MP4; sound only, as M4A, for an audio file).
/// `progress` runs 0..1.
pub fn reverse(
    source: &Path,
    out: &Path,
    progress: &mut dyn FnMut(f64),
    cancel: &dyn Fn() -> bool,
) -> Result<(), MediaError> {
    let asset = crate::probe::probe(source)?;
    let has_video = asset.kind == MediaKind::Video;
    if !has_video && !asset.has_audio {
        return Err(MediaError::Other("Nothing to reverse: no video or audio".into()));
    }
    let ext = out.extension().and_then(|e| e.to_str()).unwrap_or(if has_video { "mp4" } else { "m4a" });
    let partial = out.with_extension(format!("partial.{ext}"));
    let result = (|| {
        let mut writer = Mp4Writer::create(&partial)?;
        let rate = asset.source_rate.and_then(|r| FrameRate::plausible(Some(r))).unwrap_or_default();
        let (w, h) = (((asset.width as u32) & !1).max(2), ((asset.height as u32) & !1).max(2));
        let times = if has_video { frame_times(source)? } else { Vec::new() };
        if has_video {
            if times.is_empty() {
                return Err(MediaError::Other("The video has no frames".into()));
            }
            // edited like footage: good quality, a keyframe every half second so seeking stays quick
            writer.add_video(&VideoSettings {
                width: w,
                height: h,
                rate,
                encoder: EncoderChoice::Software,
                quality: 18,
                gop: (rate.fps() / 2.0).round().max(1.0) as u32,
                preset: Some("veryfast"),
                tune: None,
            })?;
        }
        let mut audio = if asset.has_audio {
            writer.add_audio()?;
            AudioSource::open(source, SAMPLE_RATE).ok()
        } else {
            None
        };
        // the copy's length: the frames at the source's rate, or the sound's for an audio file
        let frame_sec = 1.0 / rate.fps();
        let length = if has_video { times.len() as f64 * frame_sec } else { asset.duration_sec };
        let mut audio_done = 0.0;
        let mut write_audio_until =
            |writer: &mut Mp4Writer, until: f64, audio_done: &mut f64| -> Result<(), MediaError> {
                let Some(a) = audio.as_mut() else { return Ok(()) };
                while *audio_done < until - 1e-9 {
                    let piece = (until - *audio_done).min(AUDIO_CHUNK_SEC);
                    // copy time t is source time length - t: read the piece that ends where this one starts, backwards
                    let from = (length - *audio_done - piece).max(0.0);
                    let mut samples = a.read(from, piece);
                    let frames: Vec<[f32; 2]> = samples.chunks_exact(2).rev().map(|p| [p[0], p[1]]).collect();
                    samples = frames.into_iter().flatten().collect();
                    writer.write_audio(&samples)?;
                    *audio_done += piece;
                }
                Ok(())
            };
        if has_video {
            let mut decoder = VideoSource::open(source, w as usize, h as usize)?;
            let per_chunk = (FRAME_BUDGET_BYTES / (w as usize * h as usize * 4)).clamp(4, 240);
            let mut written = 0usize;
            let mut end = times.len();
            while end > 0 {
                if cancel() {
                    return Err(MediaError::Other("cancelled".into()));
                }
                let start = end.saturating_sub(per_chunk);
                decoder.seek(times[start]);
                let held: Vec<_> =
                    times[start..end].iter().filter_map(|&t| decoder.decode_forward(t, DecodeMode::Playback)).collect();
                // newest first: the chunk's last frame is the copy's next one
                for f in held.iter().rev() {
                    writer.write_bgra(&f.pixels, f.width * 4)?;
                    written += 1;
                }
                write_audio_until(&mut writer, written as f64 * frame_sec, &mut audio_done)?;
                progress(written as f64 / times.len() as f64);
                end = start;
            }
        }
        let mut t = AUDIO_CHUNK_SEC;
        loop {
            write_audio_until(&mut writer, t.min(length), &mut audio_done)?;
            if !has_video {
                progress((t / length.max(1e-6)).min(1.0));
            }
            if t >= length {
                break;
            }
            t += AUDIO_CHUNK_SEC;
        }
        writer.finish()
    })();
    match result {
        Ok(()) => {
            std::fs::rename(&partial, out)?;
            progress(1.0);
            Ok(())
        }
        Err(e) => {
            let _ = std::fs::remove_file(&partial);
            Err(e)
        }
    }
}
