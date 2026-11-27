//! What is in a file: its kind, size, length, frame rate and sound.

use std::io::Read;
use std::path::Path;

use ff::ffi;
use ffmpeg_next as ff;
use fig_timeline::{FrameRate, MediaAsset, MediaKind};

use crate::MediaError;

/// How far a stream's picture is turned for showing, clockwise: 0, 90, 180 or 270 (phones record on their side and
/// say so).
pub fn rotation(stream: &ff::format::stream::Stream) -> i32 {
    unsafe {
        let par = (*stream.as_ptr()).codecpar;
        let side = ffi::av_packet_side_data_get(
            (*par).coded_side_data,
            (*par).nb_coded_side_data,
            ffi::AVPacketSideDataType::AV_PKT_DATA_DISPLAYMATRIX,
        );
        if side.is_null() || (*side).size < 36 {
            return 0;
        }
        // the matrix says how far to turn counterclockwise
        let ccw = ffi::av_display_rotation_get((*side).data as *const i32);
        if !ccw.is_finite() {
            return 0;
        }
        let cw = (-ccw).rem_euclid(360.0);
        (((cw / 90.0).round() as i32) * 90).rem_euclid(360)
    }
}

const STILL_EXTENSIONS: [&str; 7] = ["png", "jpg", "jpeg", "webp", "bmp", "tif", "tiff"];

/// A file's SHA-256, hex.
pub fn hash_file(path: &Path) -> std::io::Result<String> {
    use sha2::Digest;
    let mut f = std::fs::File::open(path)?;
    let mut h = sha2::Sha256::new();
    let mut buf = vec![0u8; 1 << 16];
    loop {
        let n = f.read(&mut buf)?;
        if n == 0 {
            break;
        }
        h.update(&buf[..n]);
    }
    Ok(h.finalize().iter().map(|b| format!("{b:02x}")).collect())
}

/// Reads what a file holds, as a library entry (with a fresh id).
pub fn probe(path: &Path) -> Result<MediaAsset, MediaError> {
    crate::init();
    let input = ff::format::input(path).map_err(|e| MediaError::open(path, e))?;
    let mut asset =
        MediaAsset { url: path.to_string_lossy().into_owned(), hash: hash_file(path)?, ..Default::default() };
    let format = input.format().name().to_string();
    if let Some(st) = input.streams().best(ff::media::Type::Video) {
        let par = st.parameters();
        let (mut w, mut h) = unsafe { ((*par.as_ptr()).width, (*par.as_ptr()).height) };
        if rotation(&st) % 180 == 90 {
            std::mem::swap(&mut w, &mut h);
        }
        asset.width = w;
        asset.height = h;
        asset.duration_sec = duration_sec(path, &input, &st);
        asset.kind = MediaKind::Video;
        let ext = path.extension().and_then(|e| e.to_str()).unwrap_or("").to_lowercase();
        if format == "image2" || format.ends_with("_pipe") || STILL_EXTENSIONS.contains(&ext.as_str()) {
            // a still has no length of its own: an image clip lasts as long as the user makes it
            asset.kind = MediaKind::Image;
            asset.duration_sec = 0.0;
            return Ok(asset);
        }
        if format == "gif" {
            // a GIF is a looping picture with transparency, not a video: its length is one loop
            asset.kind = MediaKind::Image;
            return Ok(asset);
        }
        asset.has_audio = input.streams().any(|s| s.parameters().medium() == ff::media::Type::Audio);
        let fr = unsafe { (*par.as_ptr()).framerate };
        let rate = if fr.num > 0 && fr.den > 0 { ff::Rational::from(fr) } else { st.avg_frame_rate() };
        asset.source_rate = (rate.numerator() > 0 && rate.denominator() > 0)
            .then(|| FrameRate::try_new(rate.numerator(), rate.denominator()))
            .flatten();
        return Ok(asset);
    }
    let st = input.streams().best(ff::media::Type::Audio).ok_or(MediaError::NoStream("video or audio"))?;
    asset.duration_sec = duration_sec(path, &input, &st);
    asset.kind = MediaKind::Audio;
    asset.has_audio = true;
    Ok(asset)
}

/// How long the file plays: from its header, else its stream's, else measured from its packets (a WebM written by a
/// screen recorder has no length in its header).
pub fn duration_sec(path: &Path, input: &ff::format::context::Input, stream: &ff::format::stream::Stream) -> f64 {
    if input.duration() > 0 {
        return input.duration() as f64 / f64::from(ff::ffi::AV_TIME_BASE);
    }
    if stream.duration() > 0 {
        return stream.duration() as f64 * f64::from(stream.time_base());
    }
    measure_duration_sec(path)
}

/// Reads every packet (without decoding) to find where the last one ends. 0 when it can't.
fn measure_duration_sec(path: &Path) -> f64 {
    let Ok(mut input) = ff::format::input(path) else { return 0.0 };
    let bases: Vec<f64> = input.streams().map(|s| f64::from(s.time_base())).collect();
    let (mut first, mut end) = (f64::MAX, 0.0f64);
    let mut packet = ff::Packet::empty();
    while packet.read(&mut input).is_ok() {
        if let Some(ts) = packet.pts().or(packet.dts()) {
            let tb = bases[packet.stream()];
            first = first.min(ts as f64 * tb);
            end = end.max((ts + packet.duration().max(0)) as f64 * tb);
        }
    }
    if first == f64::MAX { 0.0 } else { (end - first.max(0.0)).max(0.0) }
}

/// How long the picture runs: from the first video packet to the end of the last, read without decoding. Editors
/// that count frames (Resolve) see this rather than the container's length, which can run longer with the sound.
pub fn video_length_sec(path: &Path) -> Option<f64> {
    let mut input = ff::format::input(path).ok()?;
    let stream = input.streams().best(ff::media::Type::Video)?;
    let (index, tb) = (stream.index(), f64::from(stream.time_base()));
    let (mut first, mut end) = (i64::MAX, i64::MIN);
    let mut packet = ff::Packet::empty();
    while packet.read(&mut input).is_ok() {
        if packet.stream() != index {
            continue;
        }
        if let Some(ts) = packet.pts().or(packet.dts()) {
            first = first.min(ts);
            end = end.max(ts + packet.duration().max(0));
        }
    }
    (end > first).then(|| (end - first) as f64 * tb)
}

/// True when a finished MP4 looks complete: an `ftyp` then a top-level `moov` (a proxy written without its trailer
/// fails). Doesn't open FFmpeg.
pub fn is_complete_mp4(path: &Path) -> bool {
    let Ok(mut f) = std::fs::File::open(path) else { return false };
    let Ok(len) = f.metadata().map(|m| m.len()) else { return false };
    if len < 16 {
        return false;
    }
    use std::io::{Seek, SeekFrom};
    let (mut pos, mut saw_ftyp) = (0u64, false);
    while pos + 8 <= len {
        let mut hdr = [0u8; 8];
        if f.seek(SeekFrom::Start(pos)).is_err() || f.read_exact(&mut hdr).is_err() {
            return false;
        }
        let size = u32::from_be_bytes(hdr[..4].try_into().unwrap()) as u64;
        let (header, boxed) = match size {
            1 => {
                let mut ext = [0u8; 8];
                if f.read_exact(&mut ext).is_err() {
                    return false;
                }
                (16, u64::from_be_bytes(ext))
            }
            0 => (8, len - pos),
            s => (8, s),
        };
        if boxed < header || pos + boxed > len {
            return false;
        }
        match &hdr[4..8] {
            b"ftyp" => saw_ftyp = true,
            b"moov" if saw_ftyp => return true,
            _ => {}
        }
        pos += boxed;
    }
    false
}

/// The upright size of a file's picture, without reading the rest.
pub fn probe_dimensions(path: &Path) -> Result<(u32, u32), MediaError> {
    crate::init();
    let input = ff::format::input(path).map_err(|e| MediaError::open(path, e))?;
    let st = input.streams().best(ff::media::Type::Video).ok_or(MediaError::NoStream("video"))?;
    let par = st.parameters();
    let (w, h) = unsafe { ((*par.as_ptr()).width as u32, (*par.as_ptr()).height as u32) };
    Ok(if rotation(&st) % 180 == 90 { (h, w) } else { (w, h) })
}
