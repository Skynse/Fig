//! The sound: a feeder thread mixes the timeline ahead of the device into a queue, and the device callback drains it
//! and counts what it played, which is what the clock reads.

use std::collections::VecDeque;
use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, Instant};

use fig_media::mixer::{AudioMixer, SAMPLE_RATE, ScrubSynth};

use super::Snapshot;

/// About 85 ms a mix.
const CHUNK_FRAMES: usize = 4096;
/// Kept queued ahead of the device while playing.
const TARGET_FRAMES: usize = 24_000;
/// The device's period in frames.
#[cfg(feature = "audio")]
const PERIOD_FRAMES: u32 = 1024;
/// Queued before the device starts, so the first moments don't stutter.
const PREBUFFER_FRAMES: usize = 12_000;

#[derive(Default)]
struct Feed {
    playing: bool,
    next_sec: f64,
    /// Bumped on every start and stop, so a mix begun before one isn't queued after it.
    generation: u64,
    scrub: Option<(f64, f64)>,
    reset: bool,
}

struct Inner {
    queue: Mutex<VecDeque<f32>>,
    played: AtomicU64,
    /// Frames the device wanted while running and found nothing queued (each one is a click).
    starved: AtomicU64,
    /// Device callbacks, and the most frames one asked for.
    callbacks: AtomicU64,
    largest: AtomicU64,
    /// The device takes from the queue (otherwise it plays silence and the clock stands still).
    running: AtomicBool,
    volume: AtomicU32,
    muted: AtomicBool,
    available: AtomicBool,
    feed: Mutex<Feed>,
    wake: Condvar,
}

pub struct Output {
    inner: Arc<Inner>,
}

impl Output {
    pub fn open(snapshot: Arc<Mutex<Arc<Snapshot>>>) -> Output {
        let inner = Arc::new(Inner {
            queue: Mutex::new(VecDeque::new()),
            played: AtomicU64::new(0),
            starved: AtomicU64::new(0),
            callbacks: AtomicU64::new(0),
            largest: AtomicU64::new(0),
            running: AtomicBool::new(false),
            volume: AtomicU32::new(1f32.to_bits()),
            muted: AtomicBool::new(false),
            available: AtomicBool::new(false),
            feed: Mutex::new(Feed::default()),
            wake: Condvar::new(),
        });
        device::start(inner.clone());
        // FIG_AUDIO_TRACE=1: silent output and a line a second about how the device is fed
        if std::env::var_os("FIG_AUDIO_TRACE").is_some() {
            inner.muted.store(true, Ordering::Relaxed);
            let tracer = inner.clone();
            std::thread::spawn(move || {
                let t0 = Instant::now();
                let (mut last_played, mut last_starved) = (0, 0);
                loop {
                    std::thread::sleep(Duration::from_secs(1));
                    tracer.muted.store(true, Ordering::Relaxed);
                    let (played, starved) = (tracer.played.load(Ordering::Relaxed), tracer.starved.load(Ordering::Relaxed));
                    eprintln!(
                        "audio {:5.1}s played {:6} starved {:6} queued {:6} callbacks {:5} largest {:5} running {}",
                        t0.elapsed().as_secs_f64(),
                        played - last_played,
                        starved - last_starved,
                        tracer.queue.lock().unwrap().len() / 2,
                        tracer.callbacks.swap(0, Ordering::Relaxed),
                        tracer.largest.swap(0, Ordering::Relaxed),
                        tracer.running.load(Ordering::Relaxed),
                    );
                    (last_played, last_starved) = (played, starved);
                }
            });
        }
        let feeder = inner.clone();
        std::thread::Builder::new()
            .name("fig-audio-feed".into())
            .spawn(move || feed_loop(&feeder, &snapshot))
            .expect("audio feeder thread");
        Output { inner }
    }

    pub fn is_available(&self) -> bool {
        self.inner.available.load(Ordering::Relaxed)
    }

    pub fn played_frames(&self) -> u64 {
        self.inner.played.load(Ordering::Relaxed)
    }

    /// Starts (or restarts) the sound at `from_sec`; the device begins once enough is queued.
    pub fn start(&self, from_sec: f64) {
        self.inner.running.store(false, Ordering::Relaxed);
        self.inner.queue.lock().unwrap().clear();
        let mut feed = self.inner.feed.lock().unwrap();
        feed.playing = true;
        feed.next_sec = from_sec;
        feed.generation += 1;
        feed.scrub = None;
        drop(feed);
        self.inner.wake.notify_all();
    }

    pub fn stop(&self) {
        self.inner.running.store(false, Ordering::Relaxed);
        self.inner.queue.lock().unwrap().clear();
        let mut feed = self.inner.feed.lock().unwrap();
        feed.playing = false;
        feed.generation += 1;
        drop(feed);
        self.inner.wake.notify_all();
    }

    /// Plays the sound swept over from `from` to `to`, like a jog wheel.
    pub fn scrub(&self, from: f64, to: f64) {
        if !self.is_available() || (from - to).abs() < 1e-6 {
            return;
        }
        let mut feed = self.inner.feed.lock().unwrap();
        if feed.playing {
            return;
        }
        let start = feed.scrub.map_or(from, |(f, _)| f);
        feed.scrub = Some((start, to));
        drop(feed);
        self.inner.wake.notify_all();
    }

    pub fn set_volume(&self, volume: f32, muted: bool) {
        self.inner.volume.store(volume.clamp(0.0, 1.0).to_bits(), Ordering::Relaxed);
        self.inner.muted.store(muted, Ordering::Relaxed);
    }

    /// Closes the open decoders.
    pub fn reset(&self) {
        self.inner.feed.lock().unwrap().reset = true;
        self.inner.wake.notify_all();
    }
}

fn feed_loop(inner: &Inner, snapshot: &Mutex<Arc<Snapshot>>) {
    let mut mixer = AudioMixer::new();
    let mut synth = ScrubSynth::new();
    let mut last_scrub = Instant::now();
    loop {
        let (playing, next_sec, generation, scrub) = {
            let mut feed = inner.feed.lock().unwrap();
            if feed.reset {
                feed.reset = false;
                mixer.reset();
            }
            if !feed.playing && feed.scrub.is_none() {
                feed = inner.wake.wait_timeout(feed, Duration::from_millis(250)).unwrap().0;
            }
            (feed.playing, feed.next_sec, feed.generation, feed.scrub.take())
        };
        let snap = snapshot.lock().unwrap().clone();
        if playing {
            let queued = inner.queue.lock().unwrap().len() / 2;
            if queued >= TARGET_FRAMES {
                if !inner.running.load(Ordering::Relaxed) {
                    inner.running.store(true, Ordering::Relaxed);
                }
                std::thread::sleep(Duration::from_millis(15));
                continue;
            }
            let duration = CHUNK_FRAMES as f64 / SAMPLE_RATE as f64;
            let chunk = mixer.mix(&snap.timeline, &snap.media, next_sec, duration);
            let mut feed = inner.feed.lock().unwrap();
            if feed.generation != generation || !feed.playing {
                continue;
            }
            feed.next_sec = next_sec + duration;
            drop(feed);
            let mut queue = inner.queue.lock().unwrap();
            queue.extend(chunk);
            if queue.len() / 2 >= PREBUFFER_FRAMES {
                inner.running.store(true, Ordering::Relaxed);
            }
        } else if let Some((from, to)) = scrub {
            // as much sound as wall time passed since the last sweep, within reason
            let elapsed = last_scrub.elapsed().as_secs_f64().clamp(0.02, 0.12);
            last_scrub = Instant::now();
            let frames = (elapsed * SAMPLE_RATE as f64) as usize;
            let mut mix = |start: f64, length: f64| mixer.mix(&snap.timeline, &snap.media, start, length);
            let block = synth.render(from, to, frames, true, true, &mut mix);
            let mut queue = inner.queue.lock().unwrap();
            // never let sweeps pile up behind the pointer
            let keep = (SAMPLE_RATE as usize / 10) * 2;
            if queue.len() > keep {
                let excess = queue.len() - keep;
                queue.drain(..excess);
            }
            queue.extend(block);
            inner.running.store(true, Ordering::Relaxed);
        }
    }
}

/// Takes `out_frames` frames of interleaved stereo for the device: from the queue while running (counted as played),
/// silence otherwise.
fn pull(inner: &Inner, out: &mut [f32], channels: usize, step: f64, phase: &mut f64, held: &mut [f32; 4]) {
    let gain = if inner.muted.load(Ordering::Relaxed) { 0.0 } else { f32::from_bits(inner.volume.load(Ordering::Relaxed)) };
    let running = inner.running.load(Ordering::Relaxed);
    let mut queue = inner.queue.lock().unwrap();
    let mut played = 0u64;
    let mut starved = 0u64;
    inner.callbacks.fetch_add(1, Ordering::Relaxed);
    inner.largest.fetch_max((out.len() / channels) as u64, Ordering::Relaxed);
    for frame in out.chunks_mut(channels) {
        // linear resampling from the mixer's 48 kHz to the device's rate (step = 48k / device rate)
        while running && *phase >= 1.0 {
            if queue.len() < 2 {
                starved += 1;
                break;
            }
            held[0] = held[2];
            held[1] = held[3];
            held[2] = queue.pop_front().unwrap_or(0.0);
            held[3] = queue.pop_front().unwrap_or(0.0);
            *phase -= 1.0;
            played += 1;
        }
        let (l, r) = if running && *phase < 1.0 {
            let f = *phase as f32;
            (held[0] + (held[2] - held[0]) * f, held[1] + (held[3] - held[1]) * f)
        } else {
            (0.0, 0.0)
        };
        if running && *phase < 1.0 {
            *phase += step;
        }
        frame[0] = l * gain;
        if channels > 1 {
            frame[1] = r * gain;
        }
        for c in frame.iter_mut().skip(2) {
            *c = 0.0;
        }
    }
    drop(queue);
    inner.played.fetch_add(played, Ordering::Relaxed);
    inner.starved.fetch_add(starved, Ordering::Relaxed);
}

#[cfg(feature = "audio")]
mod device {
    use super::*;
    use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};

    /// Opens the default output on a thread that owns the stream (cpal streams aren't Send everywhere).
    pub fn start(inner: Arc<Inner>) {
        std::thread::Builder::new()
            .name("fig-audio-device".into())
            .spawn(move || {
                let Some(stream) = open(inner.clone()) else { return };
                if stream.play().is_err() {
                    return;
                }
                inner.available.store(true, Ordering::Relaxed);
                loop {
                    std::thread::park();
                }
            })
            .expect("audio device thread");
    }

    fn open(inner: Arc<Inner>) -> Option<cpal::Stream> {
        let host = cpal::default_host();
        let device = host.default_output_device()?;
        let default = device.default_output_config().ok()?;
        let channels = default.channels().max(1) as usize;
        // a steady period: PipeWire's ALSA device allows 16 frames, and a period that small underruns constantly
        // (which sounds like crackle). About 21 ms is safe and still feels immediate.
        let buffer = match default.buffer_size() {
            cpal::SupportedBufferSize::Range { min, max } => cpal::BufferSize::Fixed(PERIOD_FRAMES.clamp(*min, *max)),
            cpal::SupportedBufferSize::Unknown => cpal::BufferSize::Default,
        };
        // ask for the mixer's rate; fall back to the device's own and resample
        let try_rate = |rate: u32, buffer_size: cpal::BufferSize| cpal::StreamConfig { channels: channels as u16, sample_rate: rate, buffer_size };
        let attempts = [(SAMPLE_RATE, buffer), (default.sample_rate(), buffer), (SAMPLE_RATE, cpal::BufferSize::Default), (default.sample_rate(), cpal::BufferSize::Default)];
        for (rate, buffer_size) in attempts {
            let step = SAMPLE_RATE as f64 / rate as f64;
            let inner = inner.clone();
            let mut phase = 1.0f64;
            let mut held = [0f32; 4];
            let stream = device.build_output_stream::<f32, _, _>(
                try_rate(rate, buffer_size),
                move |out: &mut [f32], _| pull(&inner, out, channels, step, &mut phase, &mut held),
                |_| {},
                None,
            );
            if let Ok(stream) = stream {
                return Some(stream);
            }
        }
        None
    }
}

#[cfg(not(feature = "audio"))]
mod device {
    use super::*;

    /// Without a sound device the clock runs on wall time; `pull` stays unused.
    pub fn start(_inner: Arc<Inner>) {
        let _ = pull;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn inner_with(samples: &[f32]) -> Inner {
        Inner {
            queue: Mutex::new(samples.iter().copied().collect()),
            played: AtomicU64::new(0),
            starved: AtomicU64::new(0),
            callbacks: AtomicU64::new(0),
            largest: AtomicU64::new(0),
            running: AtomicBool::new(true),
            volume: AtomicU32::new(1f32.to_bits()),
            muted: AtomicBool::new(false),
            available: AtomicBool::new(true),
            feed: Mutex::new(Feed::default()),
            wake: Condvar::new(),
        }
    }

    #[test]
    fn same_rate_output_is_the_input() {
        let input: Vec<f32> = (0..4800).flat_map(|i| {
            let s = (i as f32 * 0.05).sin() * 0.5;
            [s, -s]
        }).collect();
        let inner = inner_with(&input);
        let (mut phase, mut held) = (1.0, [0f32; 4]);
        let mut out = Vec::new();
        for _ in 0..8 {
            let mut buf = vec![0f32; 512 * 2];
            pull(&inner, &mut buf, 2, 1.0, &mut phase, &mut held);
            out.extend(buf);
        }
        // one frame of delay from the interpolation, otherwise exact
        for f in 1..4000 {
            assert!((out[f * 2] - input[(f - 1) * 2]).abs() < 1e-6, "frame {f}: {} vs {}", out[f * 2], input[(f - 1) * 2]);
        }
    }
}
