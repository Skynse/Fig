//! The timeline's sound: which clips are heard at a moment (audio clips, and video clips playing their own sound),
//! decoded and mixed into interleaved stereo. Pure logic over the decoders; playback hands the result to the device.

use std::collections::HashMap;
use std::path::Path;

use fig_timeline::{Clip, ClipBody, MediaAsset, Timeline, TrackKind, clip_time, fade};

use crate::AudioSource;

pub const SAMPLE_RATE: u32 = 48_000;

/// Mixes a timeline's sound, keeping a decoder open per file so consecutive windows follow on without seeking.
#[derive(Default)]
pub struct AudioMixer {
    sources: HashMap<String, Option<AudioSource>>,
}

impl AudioMixer {
    pub fn new() -> AudioMixer {
        AudioMixer::default()
    }

    /// Drops the open decoders (after stopping), so the next play starts fresh.
    pub fn reset(&mut self) {
        self.sources.clear();
    }

    /// Every audible clip overlapping `start`..`start + duration`, summed into interleaved L/R at 48 kHz: track mute,
    /// clip volume × its fades, and its source range and speed (or speed curve) respected. Always exactly the
    /// requested length (silence-padded).
    pub fn mix(&mut self, timeline: &Timeline, media: &[MediaAsset], start: f64, duration: f64) -> Vec<f32> {
        let frames = (duration * SAMPLE_RATE as f64).round().max(0.0) as usize;
        let mut mixed = vec![0f32; frames * 2];
        if frames == 0 {
            return mixed;
        }
        let end = start + frames as f64 / SAMPLE_RATE as f64;
        for track in timeline.tracks.iter().filter(|t| !t.muted) {
            for clip in track.clips.iter().filter(|c| c.enabled && c.end_sec() > start && c.start_sec < end) {
                let source_id = match &clip.body {
                    ClipBody::Audio(a) if track.kind == TrackKind::Audio => &a.source_id,
                    ClipBody::Video(v) if v.plays_audio => &v.source_id,
                    _ => continue,
                };
                let Some(asset) = media.iter().find(|m| &m.id == source_id) else { continue };
                if asset.url.is_empty() || asset.offline || (clip.as_video().is_some() && !asset.has_audio) {
                    continue;
                }
                self.mix_clip(&mut mixed, start, frames, clip, &asset.url);
            }
        }
        mixed
    }

    fn mix_clip(&mut self, mixed: &mut [f32], win_start: f64, win_frames: usize, clip: &Clip, url: &str) {
        let rate = SAMPLE_RATE as f64;
        let win_end = win_start + win_frames as f64 / rate;
        let (from, to) = (win_start.max(clip.start_sec), win_end.min(clip.end_sec()));
        if to <= from {
            return;
        }
        let src_in = clip_time::source_at(clip, from - clip.start_sec);
        let src_dur = clip_time::source_at(clip, to - clip.start_sec) - src_in;
        if src_dur <= 0.0 {
            return;
        }
        let source =
            self.sources.entry(url.to_string()).or_insert_with(|| AudioSource::open(Path::new(url), SAMPLE_RATE).ok());
        let Some(source) = source else { return };
        let mut decoded = source.read(src_in, src_dur);
        if decoded.is_empty() {
            return;
        }
        // a speed other than 1 changes how fast the file plays: resample the block to fill the overlap
        let src_frames = decoded.len() / 2;
        let out_frames = ((to - from) * rate).round() as usize;
        let speed = if clip.speed <= 0.0 { 1.0 } else { clip.speed };
        if clip_time::has_curve(clip) && out_frames > 0 {
            // varispeed: each output sample reads the file where the curve is at that moment
            let t0 = from - clip.start_sec;
            decoded = resample(
                &decoded,
                src_frames,
                out_frames,
                Some(&|i| (clip_time::source_at(clip, t0 + i as f64 / rate) - src_in) * rate),
            );
        } else if (speed - 1.0).abs() > 1e-6 && out_frames > 0 {
            decoded = resample(&decoded, src_frames, out_frames, None);
        }
        let offset = ((from - win_start) * rate).round() as usize * 2;
        let local = from - clip.start_sec;
        let n = decoded.len().min(mixed.len().saturating_sub(offset)) / 2;
        for f in 0..n {
            let gain = fade::effective_volume(clip, local + f as f64 / rate).clamp(0.0, 1.0) as f32;
            mixed[offset + f * 2] += decoded[f * 2] * gain;
            mixed[offset + f * 2 + 1] += decoded[f * 2 + 1] * gain;
        }
    }
}

/// Stereo resampled to `out_frames` by linear interpolation; `position(i)` says where in the source output frame i
/// reads (evenly spread when none).
fn resample(src: &[f32], src_frames: usize, out_frames: usize, position: Option<&dyn Fn(usize) -> f64>) -> Vec<f32> {
    let mut out = vec![0f32; out_frames * 2];
    if src_frames <= 1 {
        if src.len() >= 2 {
            for i in 0..out_frames {
                out[i * 2] = src[0];
                out[i * 2 + 1] = src[1];
            }
        }
        return out;
    }
    let step = src_frames as f64 / out_frames as f64;
    let max_lo = src_frames - 1;
    for i in 0..out_frames {
        let pos = match position {
            Some(p) => p(i).clamp(0.0, (src_frames - 1) as f64),
            None => i as f64 * step,
        };
        let lo = (pos as usize).min(max_lo);
        let hi = (lo + 1).min(max_lo);
        let frac = (pos - lo as f64) as f32;
        out[i * 2] = src[lo * 2] + (src[hi * 2] - src[lo * 2]) * frac;
        out[i * 2 + 1] = src[lo * 2 + 1] + (src[hi * 2 + 1] - src[lo * 2 + 1]) * frac;
    }
    out
}

/// Turns "the playhead moved from A to B" into sound, the way a jog wheel does: the mix between the two times,
/// stretched to fit the output, so it plays faster (and higher) as you scrub faster and backwards when you scrub back.
#[derive(Default)]
pub struct ScrubSynth {
    window: Vec<f32>,
    window_start: f64,
    window_end: f64,
}

impl ScrubSynth {
    /// Soften block edges so starts and stops don't click (3 ms).
    const FADE_FRAMES: usize = 144;
    const WINDOW_SEC: f64 = 0.5;

    pub fn new() -> ScrubSynth {
        ScrubSynth::default()
    }

    /// Stereo for `out_frames` frames sweeping the timeline from `from` to `to`. `mix` gives the timeline's sound for
    /// (start, duration).
    pub fn render(
        &mut self,
        from: f64,
        to: f64,
        out_frames: usize,
        fade_in: bool,
        fade_out: bool,
        mix: &mut dyn FnMut(f64, f64) -> Vec<f32>,
    ) -> Vec<f32> {
        let mut out = vec![0f32; out_frames * 2];
        if out_frames == 0 {
            return out;
        }
        let lo = from.min(to).max(0.0);
        let hi = (lo + 1.0 / SAMPLE_RATE as f64).max(from.max(to));
        // a half-second window around the sweep, so most blocks need no new decode
        if !(lo >= self.window_start && hi <= self.window_end - 0.01 && !self.window.is_empty()) {
            let length = Self::WINDOW_SEC.max(hi - lo + 0.1);
            let start = if to < from { (hi - length + 0.05).max(0.0) } else { (lo - 0.05).max(0.0) };
            self.window = mix(start, length);
            self.window_start = start;
            self.window_end = start + self.window.len() as f64 / 2.0 / SAMPLE_RATE as f64;
            if self.window.is_empty() {
                self.window = vec![0.0; 2];
            }
        }
        let frames = self.window.len() / 2;
        let w = &self.window;
        for i in 0..out_frames {
            let pos = from + (to - from) * (i as f64 / out_frames as f64);
            let f = (pos - self.window_start) * SAMPLE_RATE as f64;
            let i0 = f.floor() as i64;
            let frac = (f - i0 as f64) as f32;
            let a = i0.clamp(0, frames as i64 - 1) as usize;
            let b = (i0 + 1).clamp(0, frames as i64 - 1) as usize;
            let mut gain = 1f32;
            if fade_in && i < Self::FADE_FRAMES {
                gain = i as f32 / Self::FADE_FRAMES as f32;
            }
            if fade_out && out_frames - 1 - i < Self::FADE_FRAMES {
                gain = gain.min((out_frames - 1 - i) as f32 / Self::FADE_FRAMES as f32);
            }
            out[i * 2] = (w[a * 2] + (w[b * 2] - w[a * 2]) * frac) * gain;
            out[i * 2 + 1] = (w[a * 2 + 1] + (w[b * 2 + 1] - w[a * 2 + 1]) * frac) * gain;
        }
        out
    }
}
