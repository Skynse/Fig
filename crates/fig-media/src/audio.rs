//! A persistent audio reader: demux, decode and resample stay open, so playback pulls contiguous chunks without
//! re-seeking every mix window (re-seeking was what crackled).

use std::path::Path;

use ff::ffi;
use ff::format::Sample;
use ff::format::sample::Type as Planar;
use ff::software::resampling;
use ff::util::channel_layout::ChannelLayout;
use ffmpeg_next as ff;

use crate::MediaError;

/// If the next request starts farther than this from where the last one ended, seek.
const CONTIGUITY_EPS_SEC: f64 = 0.002;

pub struct AudioSource {
    input: ff::format::context::Input,
    decoder: ff::decoder::Audio,
    resampler: Option<resampling::Context>,
    stream: usize,
    time_base: f64,
    sample_rate: u32,
    frame: ff::frame::Audio,
    /// Resampled stereo samples left over from the last decode (interleaved L/R).
    carry: Vec<f32>,
    next_sec: f64,
    skip_to_pts: Option<i64>,
    eof: bool,
}

impl AudioSource {
    pub fn open(path: &Path, sample_rate: u32) -> Result<AudioSource, MediaError> {
        crate::init();
        let input = ff::format::input(path).map_err(|e| MediaError::open(path, e))?;
        let st = input.streams().best(ff::media::Type::Audio).ok_or(MediaError::NoStream("audio"))?;
        let stream = st.index();
        let time_base = f64::from(st.time_base());
        let mut ctx = ff::codec::context::Context::from_parameters(st.parameters())?;
        unsafe { (*ctx.as_mut_ptr()).pkt_timebase = st.time_base().into() };
        let decoder = ctx.decoder().audio()?;
        Ok(AudioSource {
            input,
            decoder,
            resampler: None,
            stream,
            time_base,
            sample_rate,
            frame: ff::frame::Audio::empty(),
            carry: Vec::new(),
            next_sec: -1.0,
            skip_to_pts: None,
            eof: false,
        })
    }

    /// The source time of the next sample a contiguous read would give.
    pub fn next_sec(&self) -> f64 {
        self.next_sec
    }

    pub fn seek(&mut self, time_sec: f64) {
        let t = time_sec.max(0.0);
        let ts = (t / self.time_base) as i64;
        unsafe {
            let ctx = self.input.as_mut_ptr();
            if ffi::av_seek_frame(ctx, self.stream as i32, ts.max(0), ffi::AVSEEK_FLAG_BACKWARD) < 0 {
                ffi::av_seek_frame(ctx, self.stream as i32, 0, ffi::AVSEEK_FLAG_BACKWARD);
            }
        }
        self.decoder.flush();
        self.resampler = None;
        self.carry.clear();
        self.next_sec = t;
        self.skip_to_pts = Some(ts);
        self.eof = false;
    }

    /// Interleaved stereo samples covering `start_sec` for `duration_sec`, always the full length (silence past the
    /// end). Seeks when the start doesn't follow on from the last read.
    pub fn read(&mut self, start_sec: f64, duration_sec: f64) -> Vec<f32> {
        let frames = (duration_sec * self.sample_rate as f64).round().max(0.0) as usize;
        let mut out = vec![0f32; frames * 2];
        if frames == 0 {
            return out;
        }
        if self.next_sec < 0.0 || (start_sec - self.next_sec).abs() > CONTIGUITY_EPS_SEC {
            self.seek(start_sec);
        }
        let mut written = self.drain_carry(&mut out, 0);
        while written < out.len() && !self.eof {
            let mut packet = ff::Packet::empty();
            if packet.read(&mut self.input).is_err() {
                let _ = self.decoder.send_eof();
                written = self.drain_decoder(&mut out, written);
                written = self.flush_resampler(&mut out, written);
                self.eof = true;
                break;
            }
            if packet.stream() != self.stream || self.decoder.send_packet(&packet).is_err() {
                continue;
            }
            written = self.drain_decoder(&mut out, written);
        }
        self.next_sec = start_sec + (written / 2) as f64 / self.sample_rate as f64;
        out
    }

    fn drain_carry(&mut self, out: &mut [f32], written: usize) -> usize {
        let take = self.carry.len().min(out.len() - written);
        out[written..written + take].copy_from_slice(&self.carry[..take]);
        self.carry.drain(..take);
        written + take
    }

    fn drain_decoder(&mut self, out: &mut [f32], mut written: usize) -> usize {
        while written < out.len() && self.decoder.receive_frame(&mut self.frame).is_ok() {
            if let Some(skip) = self.skip_to_pts {
                let pts = self.frame.timestamp().unwrap_or(-1);
                if pts >= 0 && pts < skip {
                    continue;
                }
                self.skip_to_pts = None;
            }
            written = self.resample(out, written);
        }
        written
    }

    fn resampler(&mut self) -> Option<&mut resampling::Context> {
        if self.resampler.is_none() {
            let f = &self.frame;
            let layout = if f.channel_layout().channels() > 0 {
                f.channel_layout()
            } else {
                ChannelLayout::default(f.channels() as i32)
            };
            self.resampler = resampling::Context::get(
                f.format(),
                layout,
                f.rate(),
                Sample::F32(Planar::Packed),
                ChannelLayout::default(2),
                self.sample_rate,
            )
            .ok();
        }
        self.resampler.as_mut()
    }

    fn resample(&mut self, out: &mut [f32], written: usize) -> usize {
        let samples = self.frame.samples();
        let rate = self.sample_rate;
        // room for the most the resampler can give back, with headroom
        let room = samples * (rate as usize) / (self.frame.rate().max(1) as usize) * 2 + 256;
        let input = self.frame.clone();
        let Some(r) = self.resampler() else { return written };
        let mut converted = ff::frame::Audio::new(Sample::F32(Planar::Packed), room, ChannelLayout::default(2));
        if r.run(&input, &mut converted).is_err() {
            return written;
        }
        self.copy(out, written, &converted)
    }

    fn flush_resampler(&mut self, out: &mut [f32], mut written: usize) -> usize {
        while written < out.len() {
            let Some(r) = self.resampler.as_mut() else { break };
            if r.delay().is_none() {
                break;
            }
            let mut converted = ff::frame::Audio::new(Sample::F32(Planar::Packed), 4096, ChannelLayout::default(2));
            if r.flush(&mut converted).is_err() || converted.samples() == 0 {
                break;
            }
            written = self.copy(out, written, &converted);
        }
        written
    }

    /// Copies resampled samples out, keeping what doesn't fit for the next read.
    fn copy(&mut self, out: &mut [f32], written: usize, converted: &ff::frame::Audio) -> usize {
        let n = converted.samples() * 2;
        let data: &[f32] =
            &converted.plane::<(f32, f32)>(0).iter().flat_map(|(l, r)| [*l, *r]).collect::<Vec<_>>()[..n];
        let take = n.min(out.len() - written);
        out[written..written + take].copy_from_slice(&data[..take]);
        self.carry.extend_from_slice(&data[take..]);
        written + take
    }
}
