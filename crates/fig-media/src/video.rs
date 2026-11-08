//! A persistent video decoder tuned for preview and export: it decodes forward from where it is, so playback doesn't
//! seek every frame; seeks when a jump is cheaper; shows the nearest keyframe at once while scrubbing; and smooths
//! frames for a clip's speed (blending in slow motion, smearing in fast).

use std::path::Path;
use std::sync::Arc;

use ff::ffi;
use ff::format::Pixel;
use ff::software::scaling;
use ffmpeg_next as ff;
use fig_fx::Frame;
use fig_timeline::FrameRate;

use crate::MediaError;

/// Decode quality against speed, for the preview.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub enum DecodeMode {
    /// Forward play: every frame, exact timing.
    #[default]
    Playback,
    /// Scrub or seek: the nearest keyframe at once (see `last_frame_approximate`).
    Scrub,
}

const FAST_CATCH_UP_SEC: f64 = 0.5;
/// A scrub step this close ahead of the last frame just keeps decoding (exact, a few frames).
const SCRUB_CONTINUE_SEC: f64 = 0.35;
/// Farther ahead than this, re-seeking to the keyframe before the target beats decoding every frame between.
const SEEK_INSTEAD_SEC: f64 = 1.5;

pub struct VideoSource {
    input: ff::format::context::Input,
    decoder: ff::decoder::Video,
    stream: usize,
    time_base: f64,
    scaler: Option<(Pixel, scaling::Context)>,
    out_w: usize,
    out_h: usize,
    fit_w: u32,
    fit_h: u32,
    /// How far the picture turns for showing, clockwise (phones record on their side).
    rotation: i32,
    off_x: usize,
    off_y: usize,
    /// Half a source frame. A frame is shown for a time when it is the nearest one: containers round frame times
    /// (WebM to the millisecond), and asking for exactly 0.0334 must still get the 0.033 frame.
    half_frame_sec: f64,
    /// Where the final frame starts, when known: asking for later holds it instead of seeking off the end.
    last_frame_sec: f64,
    pending: ff::frame::Video,
    tail: Option<ff::frame::Video>,
    held_packet: Option<ff::Packet>,
    mode: DecodeMode,
    last_presented_sec: f64,
    last_frame: Option<Arc<Frame>>,
    last_approximate: bool,
    sought_since_frame: bool,
    at_keyframe_after_seek: bool,
    // smoothing: blending keeps the frame shown before the current one; motion blur adds up every frame a step passes
    keep_previous: bool,
    previous: Option<(Arc<Frame>, f64)>,
    /// When the frame on screen is from (-1: none since a seek).
    canvas: f64,
    accumulate_from: Option<f64>,
    accumulated: Vec<u32>,
    accumulated_count: u32,
    /// Polled while decoding; true abandons the current decode (a newer position arrived).
    pub abort: Option<Box<dyn Fn() -> bool + Send>>,
}

fn skip(decoder: &mut ff::decoder::Video, nonref: bool) {
    let d = if nonref { ffi::AVDiscard::AVDISCARD_NONREF } else { ffi::AVDiscard::AVDISCARD_DEFAULT };
    unsafe {
        let ctx = decoder.as_mut_ptr();
        (*ctx).skip_frame = d;
        (*ctx).skip_idct = d;
        (*ctx).skip_loop_filter = d;
    }
}

impl VideoSource {
    /// Opens a decoder that makes `width` × `height` frames: the picture scaled to the largest centred region that
    /// keeps its shape, the rest black. Every layer is then frame-sized, and nothing is stretched.
    pub fn open(path: &Path, width: usize, height: usize) -> Result<VideoSource, MediaError> {
        crate::init();
        let input = ff::format::input(path).map_err(|e| MediaError::open(path, e))?;
        let st = input.streams().best(ff::media::Type::Video).ok_or(MediaError::NoStream("video"))?;
        let stream = st.index();
        let time_base = f64::from(st.time_base());
        let mut ctx = ff::codec::context::Context::from_parameters(st.parameters())?;
        let threads = (std::thread::available_parallelism().map_or(4, |n| n.get()) / 2).clamp(2, 8);
        ctx.set_threading(ff::threading::Config {
            kind: ff::threading::Type::Frame,
            count: threads,
            ..Default::default()
        });
        unsafe { (*ctx.as_mut_ptr()).pkt_timebase = st.time_base().into() };
        let decoder = ctx.decoder().video()?;
        let rate = st.avg_frame_rate();
        let reported = if rate.numerator() > 0 && rate.denominator() > 0 { rate } else { st.rate() };
        let plausible = (reported.numerator() > 0 && reported.denominator() > 0)
            .then(|| FrameRate::try_new(reported.numerator(), reported.denominator()))
            .flatten()
            .and_then(|r| FrameRate::plausible(Some(r)));
        // a junk rate (a recording's 1000/1): assume frames no closer than 60 fps
        let half_frame_sec = 0.5 / plausible.map_or(60.0, |r| r.fps());
        let fps = if rate.numerator() > 0 && rate.denominator() > 0 { f64::from(rate) } else { 30.0 };
        let last_frame_sec = if input.duration() > 0 {
            let start = unsafe { (*input.as_ptr()).start_time };
            let start = if start != ffi::AV_NOPTS_VALUE { start as f64 / 1e6 } else { 0.0 };
            (start + input.duration() as f64 / 1e6 - 1.5 / fps.max(1.0)).max(0.0)
        } else {
            f64::INFINITY
        };
        let rotation = crate::probe::rotation(&st);
        let (mut sw, mut sh) = (decoder.width().max(1) as f64, decoder.height().max(1) as f64);
        if rotation % 180 == 90 {
            std::mem::swap(&mut sw, &mut sh);
        }
        let scale = (width as f64 / sw).min(height as f64 / sh);
        let fit_w = ((sw * scale).round() as u32).max(1);
        let fit_h = ((sh * scale).round() as u32).max(1);
        let mut source = VideoSource {
            input,
            decoder,
            stream,
            time_base,
            scaler: None,
            out_w: width,
            out_h: height,
            fit_w,
            fit_h,
            rotation,
            off_x: (width - fit_w as usize) / 2,
            off_y: (height - fit_h as usize) / 2,
            half_frame_sec,
            last_frame_sec,
            pending: ff::frame::Video::empty(),
            tail: None,
            held_packet: None,
            mode: DecodeMode::Playback,
            last_presented_sec: -1.0,
            last_frame: None,
            last_approximate: false,
            sought_since_frame: false,
            at_keyframe_after_seek: false,
            keep_previous: false,
            previous: None,
            canvas: -1.0,
            accumulate_from: None,
            accumulated: Vec::new(),
            accumulated_count: 0,
            abort: None,
        };
        source.set_mode(DecodeMode::Playback);
        Ok(source)
    }

    pub fn last_presented_sec(&self) -> f64 {
        self.last_presented_sec
    }

    /// True when the last scrub frame is the nearest earlier keyframe rather than the exact one asked for. Decode
    /// again in Playback to settle on it.
    pub fn last_frame_approximate(&self) -> bool {
        self.last_approximate
    }

    pub fn mode(&self) -> DecodeMode {
        self.mode
    }

    pub fn set_mode(&mut self, mode: DecodeMode) {
        self.mode = mode;
        skip(&mut self.decoder, mode == DecodeMode::Scrub);
    }

    fn aborted(&self) -> bool {
        self.abort.as_ref().is_some_and(|a| a())
    }

    /// Jumps to the keyframe at or before a time (scrubbing back, or a jump).
    pub fn seek(&mut self, time_sec: f64) {
        let ts = (time_sec.max(0.0) / self.time_base) as i64;
        unsafe {
            let ctx = self.input.as_mut_ptr();
            if ffi::av_seek_frame(ctx, self.stream as i32, ts.max(0), ffi::AVSEEK_FLAG_BACKWARD) < 0 {
                ffi::av_seek_frame(ctx, self.stream as i32, 0, ffi::AVSEEK_FLAG_BACKWARD);
            }
        }
        self.decoder.flush();
        self.held_packet = None;
        self.previous = None;
        self.canvas = -1.0;
        self.last_presented_sec = -1.0;
        self.sought_since_frame = true;
        self.at_keyframe_after_seek = false;
        // the last frame stays, so a scrub shows something until the new decode lands
    }

    /// The packet the decoder refused last time, if any, else the next one from the file.
    fn read_packet(&mut self) -> Option<ff::Packet> {
        if let Some(p) = self.held_packet.take() {
            return Some(p);
        }
        let mut p = ff::Packet::empty();
        p.read(&mut self.input).ok().map(|_| p)
    }

    /// Sends a packet. True when there may be frames to take: it was accepted, or the decoder is full and must give
    /// up its frames first; then the packet is held and sent again next. Dropping it would starve every frame that
    /// refers to it.
    fn send(&mut self, packet: ff::Packet) -> bool {
        match self.decoder.send_packet(&packet) {
            Ok(()) => true,
            Err(ff::Error::Other { errno }) if errno == ff::util::error::EAGAIN => {
                self.held_packet = Some(packet);
                true
            }
            Err(_) => false,
        }
    }

    fn pending_sec(&self) -> f64 {
        self.pending.timestamp().or(self.pending.pts()).unwrap_or(0) as f64 * self.time_base
    }

    /// The frame showing at `time_sec`, decoding forward from where the source is.
    pub fn decode_forward(&mut self, time_sec: f64, mode: DecodeMode) -> Option<Arc<Frame>> {
        self.set_mode(mode);
        let t = time_sec.min(self.last_frame_sec);
        // still nearest the frame on screen
        if self.last_presented_sec >= 0.0 && t < self.last_presented_sec + self.half_frame_sec {
            return self.last_frame.clone();
        }
        self.last_approximate = false;
        if mode == DecodeMode::Scrub {
            return self.decode_scrub(t);
        }
        // far ahead (or after a gap): the keyframe just before the target is nearer than the last frame
        if self.last_presented_sec >= 0.0
            && !self.at_keyframe_after_seek
            && t - self.last_presented_sec > SEEK_INSTEAD_SEC
        {
            self.seek(t);
        }
        let catch_up = self.last_presented_sec < 0.0 || t - self.last_presented_sec > FAST_CATCH_UP_SEC;
        self.decode_sequential(t, catch_up)
    }

    /// Scrubbing: a small step forward keeps decoding (exact); anything else shows the nearest keyframe at or before
    /// the target right away. A Playback decode afterwards settles on the exact frame, carrying on from here.
    fn decode_scrub(&mut self, t: f64) -> Option<Arc<Frame>> {
        if self.last_presented_sec >= 0.0
            && t >= self.last_presented_sec + self.half_frame_sec
            && t - self.last_presented_sec <= SCRUB_CONTINUE_SEC
        {
            return self.decode_sequential(t, false);
        }
        if !self.sought_since_frame {
            self.seek(t);
        }
        while let Some(packet) = self.read_packet() {
            if packet.stream() == self.stream
                && self.send(packet)
                && self.decoder.receive_frame(&mut self.pending).is_ok()
            {
                self.last_presented_sec = self.pending_sec();
                self.sought_since_frame = false;
                self.at_keyframe_after_seek = true;
                self.last_approximate = t - self.last_presented_sec > 0.02;
                return self.present(self.last_presented_sec);
            }
        }
        self.last_frame.clone()
    }

    fn decode_sequential(&mut self, target: f64, mut catch_up: bool) -> Option<Arc<Frame>> {
        // while far behind, drop the frames nothing refers to so the reference frames reach the target quickly
        skip(&mut self.decoder, catch_up);
        let result = self.decode_sequential_inner(target, &mut catch_up);
        skip(&mut self.decoder, self.mode == DecodeMode::Scrub);
        result
    }

    fn decode_sequential_inner(&mut self, target: f64, catch_up: &mut bool) -> Option<Arc<Frame>> {
        self.tail = None;
        let mut last_seen = -1.0;
        while let Some(packet) = self.read_packet() {
            if self.aborted() {
                // a newer position arrived: stop here, keeping the packet for the decode that carries on
                self.held_packet = Some(packet);
                self.last_presented_sec = -1.0;
                self.last_approximate = true;
                return self.last_frame.clone();
            }
            if packet.stream() != self.stream || !self.send(packet) {
                continue;
            }
            while self.decoder.receive_frame(&mut self.pending).is_ok() {
                let sec = self.pending_sec();
                if sec > target - self.half_frame_sec {
                    self.last_presented_sec = sec;
                    self.sought_since_frame = false;
                    self.at_keyframe_after_seek = false;
                    return self.present(sec);
                }
                if self.accumulate_from.is_some_and(|from| sec >= from)
                    && let Some(f) = self.present(sec)
                {
                    // motion blur: this frame counts too
                    self.accumulate(&f);
                }
                last_seen = sec;
                self.tail = Some(self.pending.clone());
                // inside the tail window: decode everything so the frame shown is exact
                if *catch_up && sec >= target - FAST_CATCH_UP_SEC {
                    *catch_up = false;
                    skip(&mut self.decoder, false);
                }
            }
        }
        let _ = self.decoder.send_eof();
        while self.decoder.receive_frame(&mut self.pending).is_ok() {
            let sec = self.pending_sec();
            if sec > target - self.half_frame_sec {
                self.last_presented_sec = sec;
                return self.present(sec);
            }
            last_seen = sec;
            self.tail = Some(self.pending.clone());
        }
        // the media ends before the target: hold its final frame
        if last_seen >= 0.0
            && self.last_frame.is_some()
            && let Some(tail) = self.tail.take()
        {
            self.pending = tail;
            self.last_presented_sec = last_seen;
            self.sought_since_frame = false;
            self.at_keyframe_after_seek = false;
            return self.present(last_seen);
        }
        self.last_frame.clone()
    }

    /// Scales the pending frame into a frame-sized picture, letterboxed.
    fn present(&mut self, sec: f64) -> Option<Arc<Frame>> {
        let format = self.pending.format();
        let (sw, sh) = (self.pending.width(), self.pending.height());
        if sw == 0 || sh == 0 {
            return self.last_frame.clone();
        }
        // scaled the way the file stores it, then turned upright
        let (tw, th) = if self.rotation % 180 == 90 { (self.fit_h, self.fit_w) } else { (self.fit_w, self.fit_h) };
        if self.scaler.as_ref().is_none_or(|(f, s)| *f != format || s.input().width != sw || s.input().height != sh) {
            let ctx = scaling::Context::get(format, sw, sh, Pixel::BGRA, tw, th, scaling::Flags::BILINEAR).ok()?;
            self.scaler = Some((format, ctx));
        }
        let mut fit = ff::frame::Video::empty();
        self.scaler.as_mut().unwrap().1.run(&self.pending, &mut fit).ok()?;
        let mut out = Frame::new(self.out_w, self.out_h);
        out.pixels.chunks_exact_mut(4).for_each(|p| p[3] = 255);
        let stride = fit.stride(0);
        let data = fit.data(0);
        let (fw, fh) = (self.fit_w as usize, self.fit_h as usize);
        let (tw, th) = (tw as usize, th as usize);
        for y in 0..fh {
            let dst = ((y + self.off_y) * self.out_w + self.off_x) * 4;
            let row = &mut out.pixels[dst..dst + fw * 4];
            if self.rotation == 0 {
                row.copy_from_slice(&data[y * stride..y * stride + fw * 4]);
                continue;
            }
            for x in 0..fw {
                // the stored pixel that lands at (x, y) once turned clockwise
                let (sx, sy) = match self.rotation {
                    90 => (y, th - 1 - x),
                    180 => (tw - 1 - x, th - 1 - y),
                    _ => (tw - 1 - y, x),
                };
                let i = sy * stride + sx * 4;
                row[x * 4..x * 4 + 4].copy_from_slice(&data[i..i + 4]);
            }
        }
        let out = Arc::new(out);
        // blending: the frame on screen becomes the previous one
        if self.keep_previous
            && let Some(last) = &self.last_frame
            && let Some(at) = self.canvas_sec()
            && sec > at
        {
            self.previous = Some((last.clone(), at));
        }
        self.last_frame = Some(out.clone());
        self.canvas = sec;
        Some(out)
    }

    fn canvas_sec(&self) -> Option<f64> {
        (self.canvas >= 0.0).then_some(self.canvas)
    }

    /// A frame smoothed for the clip's speed there. `span_sec` is how much of the file one output frame covers.
    /// Slower than the file's frames with `blend`: the two real frames around the moment, mixed by how far between
    /// them it is. Faster with `motion_blur` (0..1, the shutter): the frames the step passes over, averaged. Scrubbing
    /// always takes the plain frame.
    pub fn decode_smoothed(
        &mut self,
        time_sec: f64,
        span_sec: f64,
        blend: bool,
        motion_blur: f64,
        mode: DecodeMode,
    ) -> Option<Arc<Frame>> {
        let frame_sec = 2.0 * self.half_frame_sec;
        if mode == DecodeMode::Scrub {
            self.keep_previous = false;
            return self.decode_forward(time_sec, mode);
        }
        if motion_blur > 0.0 && span_sec > frame_sec * 1.25 {
            self.keep_previous = false;
            return self.decode_blurred(time_sec, span_sec * motion_blur.clamp(0.0, 1.0), mode);
        }
        if blend && span_sec < frame_sec * 0.95 {
            return self.decode_blended(time_sec, mode);
        }
        self.keep_previous = false;
        self.decode_forward(time_sec, mode)
    }

    fn decode_blended(&mut self, t: f64, mode: DecodeMode) -> Option<Arc<Frame>> {
        self.keep_previous = true;
        // the first real frame after the moment; the one shown before it is the frame at or before it
        let after = self.decode_forward(t + self.half_frame_sec + 1e-6, mode)?;
        let after_sec = self.last_presented_sec;
        let Some((prev, prev_sec)) = self.previous.clone() else { return Some(after) };
        if prev_sec > t + 1e-6 || after_sec - prev_sec > 2.0 * self.half_frame_sec * 1.6 {
            return Some(after); // no neighbour to blend with (just sought, or a frame was skipped)
        }
        let mix = ((t - prev_sec) / (after_sec - prev_sec).max(1e-9)).clamp(0.0, 1.0);
        Some(Arc::new(fig_fx::frame::lerp_frames(&prev, &after, fig_fx::frame::weight256(mix))))
    }

    fn decode_blurred(&mut self, t: f64, window: f64, mode: DecodeMode) -> Option<Arc<Frame>> {
        self.accumulated.clear();
        self.accumulated.resize(self.out_w * self.out_h * 4, 0);
        self.accumulated_count = 0;
        self.accumulate_from = Some(t - window);
        let before = self.last_presented_sec;
        let frame = self.decode_forward(t, mode);
        self.accumulate_from = None;
        let frame = frame?;
        if self.last_presented_sec == before {
            return Some(frame); // the frame on screen still stands: nothing new to smear
        }
        self.accumulate(&frame);
        if self.accumulated_count <= 1 {
            return Some(frame);
        }
        let n = self.accumulated_count;
        let pixels = self.accumulated.iter().map(|&v| (v / n) as u8).collect();
        Some(Arc::new(Frame::from_pixels(self.out_w, self.out_h, pixels)))
    }

    fn accumulate(&mut self, f: &Frame) {
        for (a, &p) in self.accumulated.iter_mut().zip(&f.pixels) {
            *a += p as u32;
        }
        self.accumulated_count += 1;
    }
}

#[cfg(test)]
mod tests {
    use std::path::PathBuf;

    use crate::{AudioSource, DecodeMode, VideoSource};

    fn asset(name: &str) -> PathBuf {
        PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../tests").join(name)
    }

    fn mean(f: &fig_fx::Frame) -> f64 {
        f.pixels.iter().map(|&b| b as f64).sum::<f64>() / f.pixels.len() as f64
    }

    #[test]
    fn video_decodes_forward_seeks_back_and_holds_the_last_frame() {
        let path = asset("segments/segment_0.mp4");
        let mut v = VideoSource::open(&path, 320, 320).unwrap();
        let a = v.decode_forward(0.5, DecodeMode::Playback).unwrap();
        assert_eq!((a.width, a.height), (320, 320));
        assert!((v.last_presented_sec() - 0.5).abs() < 0.05, "{}", v.last_presented_sec());
        let b = v.decode_forward(1.0, DecodeMode::Playback).unwrap();
        assert!(mean(&b) > 1.0);
        v.seek(0.2);
        v.decode_forward(0.2, DecodeMode::Playback).unwrap();
        assert!((v.last_presented_sec() - 0.2).abs() < 0.05);
        // asking past the end holds the final frame
        assert!(v.decode_forward(10_000.0, DecodeMode::Playback).is_some());
        // a scrub shows a keyframe at once
        v.seek(0.0);
        assert!(v.decode_forward(0.9, DecodeMode::Scrub).is_some());
    }

    #[test]
    fn a_webm_with_rounded_frame_times_decodes_every_frame_once() {
        let path = asset("assets/3 seconds timer [fxqE27gIZcc].webm");
        let mut v = VideoSource::open(&path, 160, 90).unwrap();
        let mut times = Vec::new();
        for i in 0..30 {
            v.decode_forward(i as f64 / 30.0, DecodeMode::Playback).unwrap();
            times.push(v.last_presented_sec());
        }
        times.dedup();
        assert!(times.len() >= 28, "{times:?}");
    }

    #[test]
    fn audio_reads_contiguous_chunks_at_full_length() {
        let path = asset("assets/3 seconds timer [fxqE27gIZcc].webm");
        let mut a = AudioSource::open(&path, 48_000).unwrap();
        let first = a.read(0.0, 0.5);
        assert_eq!(first.len(), 48_000);
        assert!((a.next_sec() - 0.5).abs() < 1e-3);
        let second = a.read(0.5, 0.5);
        assert_eq!(second.len(), 48_000);
        assert!(first.iter().chain(&second).any(|s| s.abs() > 1e-4), "the sound is silent");
        let past = a.read(1_000.0, 0.1);
        assert!(past.iter().all(|s| *s == 0.0));
    }

    #[test]
    fn importing_makes_a_thumbnail_and_finalizing_a_filmstrip_and_peaks() {
        let dir = tempfile::tempdir().unwrap();
        let lib = crate::library::Library::new(dir.path());
        let mut project = fig_project::Project::default();
        let path = asset("assets/3 seconds timer [fxqE27gIZcc].webm");
        let id = lib.import(&mut project, &path).unwrap();
        assert_eq!(lib.import(&mut project, &path).unwrap(), id); // the same file is found again
        let a = project.media.iter_mut().find(|m| m.id == id).unwrap();
        assert!(a.thumbnail.as_deref().is_some_and(|t| std::path::Path::new(t).exists()));
        lib.finalize(a);
        assert!(a.filmstrip_frame_count >= 8 && a.filmstrip.is_some());
        assert!(a.waveform_peaks.as_ref().is_some_and(|p| p.iter().any(|v| *v > 0.01)));
    }
}
