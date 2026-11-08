//! What the library makes from a file: thumbnails, filmstrips, waveform peaks, single frames, and playback proxies.

use std::path::Path;

use ff::format::Pixel;
use ff::software::scaling;
use ffmpeg_next as ff;
use fig_fx::Frame;

use crate::encode::{EncoderChoice, Mp4Writer, VideoSettings};
use crate::{AudioSource, DecodeMode, MediaError, VideoSource};

/// The picture at a time, scaled to fit `width` × `height` (letterboxed), upright.
pub fn frame_at(path: &Path, time_sec: f64, width: usize, height: usize) -> Result<Frame, MediaError> {
    let mut v = VideoSource::open(path, width, height)?;
    v.seek(time_sec);
    let f = v
        .decode_forward(time_sec, DecodeMode::Playback)
        .ok_or_else(|| MediaError::Other("couldn't decode a frame".into()))?;
    Ok(std::sync::Arc::try_unwrap(f).unwrap_or_else(|a| (*a).clone()))
}

/// The upright size of a file's picture.
pub fn video_size(path: &Path) -> Result<(u32, u32), MediaError> {
    let a = crate::probe::probe_dimensions(path)?;
    Ok(a)
}

/// Writes a frame as a JPEG (alpha dropped).
pub fn save_jpeg(frame: &Frame, path: &Path, quality: u8) -> Result<(), MediaError> {
    if let Some(dir) = path.parent() {
        std::fs::create_dir_all(dir)?;
    }
    let rgb: Vec<u8> = frame.pixels.chunks_exact(4).flat_map(|p| [p[2], p[1], p[0]]).collect();
    let file = std::fs::File::create(path)?;
    let mut enc = image::codecs::jpeg::JpegEncoder::new_with_quality(std::io::BufWriter::new(file), quality);
    enc.encode(&rgb, frame.width as u32, frame.height as u32, image::ExtendedColorType::Rgb8)
        .map_err(|e| MediaError::Other(e.to_string()))
}

/// A frame of a file at a time, `width` wide (its own shape), as a JPEG.
pub fn save_frame_as_jpeg(path: &Path, time_sec: f64, out: &Path, width: u32) -> Result<(), MediaError> {
    let (sw, sh) = video_size(path)?;
    let w = (width & !1).max(2);
    let h = (((sh as f64 * w as f64 / sw.max(1) as f64).round() as u32) & !1).max(2);
    let f = frame_at(path, time_sec, w as usize, h as usize)?;
    save_jpeg(&f, out, 90)
}

/// The library card's picture: a frame about a second in (or the first), 320 wide.
pub fn thumbnail(path: &Path, out: &Path, width: u32) -> Result<(), MediaError> {
    save_frame_as_jpeg(path, 1.0, out, width).or_else(|_| save_frame_as_jpeg(path, 0.0, out, width))
}

/// How bright a file is at a time, 0..255 (a small grey version averaged).
pub fn average_luma(path: &Path, time_sec: f64) -> Result<f64, MediaError> {
    let f = frame_at(path, time_sec, 64, 36)?;
    let sum: f64 =
        f.pixels.chunks_exact(4).map(|p| 0.114 * p[0] as f64 + 0.587 * p[1] as f64 + 0.299 * p[2] as f64).sum();
    Ok(sum / (64.0 * 36.0))
}

/// A filmstrip: tiles side by side in one JPEG, one every `frame_interval_sec`.
#[derive(Clone, Debug, PartialEq)]
pub struct Filmstrip {
    pub frame_width: u32,
    pub frame_height: u32,
    pub frame_count: u32,
    pub frame_interval_sec: f64,
}

/// Builds a timeline filmstrip from keyframes only, in one forward pass: decoding every frame of a long WebM/AV1 file
/// takes minutes, keyframes 10–50× less, and look fine at timeline scale. A tile with no keyframe of its own repeats
/// the nearest one.
pub fn filmstrip(path: &Path, out: &Path, tile_height: u32) -> Result<Filmstrip, MediaError> {
    crate::init();
    let mut input = ff::format::input(path).map_err(|e| MediaError::open(path, e))?;
    let st = input.streams().best(ff::media::Type::Video).ok_or(MediaError::NoStream("video"))?;
    let index = st.index();
    let tb = f64::from(st.time_base());
    let rotation = crate::probe::rotation(&st);
    let mut duration = crate::probe::duration_sec(path, &input, &st);
    if duration <= 0.0 {
        duration = 1.0;
    }
    let ctx = ff::codec::context::Context::from_parameters(st.parameters())?;
    let mut decoder = ctx.decoder().video()?;
    unsafe { (*decoder.as_mut_ptr()).skip_frame = ff::ffi::AVDiscard::AVDISCARD_NONKEY };
    let (mut sw, mut sh) = (decoder.width().max(1), decoder.height().max(1));
    if rotation % 180 == 90 {
        std::mem::swap(&mut sw, &mut sh);
    }
    // about one tile every five seconds, held to 8..24
    let count = ((duration / 5.0).ceil() as u32).clamp(8, 24);
    let interval = duration / count as f64;
    let mut tile_w = (((tile_height as f64 * sw as f64 / sh as f64).round() as u32) & !1).max(2);
    if tile_w * count > 2048 {
        tile_w = ((2048 / count) & !1).max(2);
    }
    let height = (tile_height & !1).max(2);
    let width = tile_w * count;
    let mut strip = Frame::new(width as usize, height as usize);
    let mut filled = vec![false; count as usize];
    let (mut next, mut next_sec) = (0usize, 0.0f64);
    let mut frame = ff::frame::Video::empty();
    let stamp =
        |frame: &ff::frame::Video, strip: &mut Frame, next: &mut usize, next_sec: &mut f64, filled: &mut [bool]| {
            let sec = frame.timestamp().map_or(*next_sec, |p| p as f64 * tb);
            let mut tile: Option<Frame> = None;
            while *next < count as usize && sec + 1e-6 >= *next_sec {
                if !filled[*next] {
                    let t = tile.get_or_insert_with(|| scale_upright(frame, tile_w, height, rotation));
                    paste(strip, t, *next * tile_w as usize);
                    filled[*next] = true;
                }
                *next += 1;
                *next_sec = interval * *next as f64;
            }
        };
    let mut packet = ff::Packet::empty();
    while next < count as usize && packet.read(&mut input).is_ok() {
        if packet.stream() != index || !packet.is_key() || decoder.send_packet(&packet).is_err() {
            continue;
        }
        while next < count as usize && decoder.receive_frame(&mut frame).is_ok() {
            stamp(&frame, &mut strip, &mut next, &mut next_sec, &mut filled);
        }
    }
    let _ = decoder.send_eof();
    while next < count as usize && decoder.receive_frame(&mut frame).is_ok() {
        stamp(&frame, &mut strip, &mut next, &mut next_sec, &mut filled);
    }
    if !filled.iter().any(|f| *f) {
        return Err(MediaError::Other("No video frames decoded for filmstrip".into()));
    }
    // tiles with no keyframe of their own repeat the one before (or, at the start, the first)
    let first = filled.iter().position(|f| *f).unwrap();
    let mut last = first;
    for i in 0..count as usize {
        if filled[i] {
            last = i;
        } else {
            copy_tile(&mut strip, if i < first { first } else { last }, i, tile_w as usize);
        }
    }
    save_jpeg(&strip, out, 85)?;
    Ok(Filmstrip { frame_width: tile_w, frame_height: height, frame_count: count, frame_interval_sec: interval })
}

/// A decoded frame scaled to `w` × `h` upright (stretched to it: tiles are the picture's own shape already).
fn scale_upright(frame: &ff::frame::Video, w: u32, h: u32, rotation: i32) -> Frame {
    let (tw, th) = if rotation % 180 == 90 { (h, w) } else { (w, h) };
    let mut out = Frame::new(w as usize, h as usize);
    let Ok(mut sws) = scaling::Context::get(
        frame.format(),
        frame.width(),
        frame.height(),
        Pixel::BGRA,
        tw,
        th,
        scaling::Flags::BILINEAR,
    ) else {
        return out;
    };
    let mut bgra = ff::frame::Video::empty();
    if sws.run(frame, &mut bgra).is_err() {
        return out;
    }
    let (stride, data) = (bgra.stride(0), bgra.data(0));
    let (tw, th) = (tw as usize, th as usize);
    for y in 0..h as usize {
        for x in 0..w as usize {
            let (sx, sy) = match rotation {
                90 => (y, th - 1 - x),
                180 => (tw - 1 - x, th - 1 - y),
                270 => (tw - 1 - y, x),
                _ => (x, y),
            };
            let i = sy * stride + sx * 4;
            let o = (y * w as usize + x) * 4;
            out.pixels[o..o + 4].copy_from_slice(&data[i..i + 4]);
        }
    }
    out
}

fn paste(strip: &mut Frame, tile: &Frame, x0: usize) {
    for y in 0..tile.height {
        let d = (y * strip.width + x0) * 4;
        strip.pixels[d..d + tile.width * 4].copy_from_slice(&tile.pixels[y * tile.width * 4..(y + 1) * tile.width * 4]);
    }
}

fn copy_tile(strip: &mut Frame, from: usize, to: usize, tile_w: usize) {
    for y in 0..strip.height {
        let row = y * strip.width * 4;
        let (f, t) = (row + from * tile_w * 4, row + to * tile_w * 4);
        strip.pixels.copy_within(f..f + tile_w * 4, t);
    }
}

/// Normalized peak magnitudes (0..1) of the sound, one per bucket across the whole file.
pub fn peaks(path: &Path, buckets: usize) -> Result<Vec<f32>, MediaError> {
    crate::init();
    let duration = {
        let input = ff::format::input(path).map_err(|e| MediaError::open(path, e))?;
        let st = input.streams().best(ff::media::Type::Audio).ok_or(MediaError::NoStream("audio"))?;
        crate::probe::duration_sec(path, &input, &st)
    };
    if duration <= 0.0 || buckets == 0 {
        return Err(MediaError::Other("Cannot determine duration".into()));
    }
    // a low rate is plenty for peaks drawn a few pixels wide
    const RATE: u32 = 8000;
    let mut source = AudioSource::open(path, RATE)?;
    let total = (duration * RATE as f64) as usize;
    let per_bucket = (total / buckets).max(1);
    let mut out = vec![0f32; buckets];
    let (mut bucket, mut in_bucket) = (0usize, 0usize);
    let chunk = 4.0f64;
    let mut t = 0.0;
    while t < duration && bucket < buckets {
        let samples = source.read(t, chunk.min(duration - t));
        for pair in samples.chunks_exact(2) {
            let v = ((pair[0] + pair[1]) / 2.0).abs().min(1.0);
            if v > out[bucket] {
                out[bucket] = v;
            }
            in_bucket += 1;
            if in_bucket >= per_bucket && bucket < buckets - 1 {
                bucket += 1;
                in_bucket = 0;
            }
        }
        t += chunk;
    }
    Ok(out)
}

/// A proxy: what it is, or that none was needed.
#[derive(Clone, Debug, PartialEq)]
pub struct Proxy {
    pub width: u32,
    pub height: u32,
    /// The source is small enough already: nothing was written.
    pub skipped: bool,
}

/// True when the source is large enough that a 720p proxy is worth making.
pub fn should_generate_proxy(width: u32, height: u32, max_height: u32) -> bool {
    height > max_height || width > 1280
}

/// Re-encodes a video to a light, all-keyframe H.264 MP4 (no sound) inside `max_height`, for scrubbing and playback.
pub fn proxy(path: &Path, out: &Path, max_height: u32, cancel: &dyn Fn() -> bool) -> Result<Proxy, MediaError> {
    let asset = crate::probe::probe(path)?;
    let (sw, sh) = (asset.width.max(1) as u32, asset.height.max(1) as u32);
    if !should_generate_proxy(sw, sh, max_height) {
        return Ok(Proxy { width: sw, height: sh, skipped: true });
    }
    let h = (max_height.min(sh) & !1).max(2);
    let w = (((sw as f64 * h as f64 / sh as f64).round() as u32) & !1).max(2);
    let rate = fig_timeline::FrameRate::plausible(asset.source_rate).unwrap_or_default();
    let mut writer = Mp4Writer::create(out)?;
    writer.add_video(&VideoSettings {
        width: w,
        height: h,
        rate,
        encoder: EncoderChoice::Software,
        quality: 23,
        gop: 1,
        preset: Some("veryfast"),
        tune: Some("fastdecode"),
    })?;
    let mut v = VideoSource::open(path, w as usize, h as usize)?;
    let frames = (asset.duration_sec * rate.fps()).floor() as i64;
    for i in 0..frames.max(1) {
        if cancel() {
            drop(writer);
            let _ = std::fs::remove_file(out);
            return Err(MediaError::Other("cancelled".into()));
        }
        let Some(f) = v.decode_forward(rate.to_seconds(i), DecodeMode::Playback) else { break };
        writer.write_bgra(&f.pixels, f.width * 4)?;
    }
    writer.finish()?;
    Ok(Proxy { width: w, height: h, skipped: false })
}
