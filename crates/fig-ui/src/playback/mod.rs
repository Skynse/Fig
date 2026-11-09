//! Playing the timeline: one clock, the sound it is read from, and the picture that follows it.
//!
//! The audio device is the clock while playing forward at normal speed: the playhead is wherever the sound the
//! device has actually played has got to, so the picture can never drift from it. Shuttling (J/L at other speeds)
//! and machines without a sound device fall back to a wall clock. The picture is composited on its own thread
//! (`render`) and lands in a [`rune::NativeSurface`] without going through the view.

mod audio;
pub mod render;

use std::sync::{Arc, Mutex};
use std::time::Instant;

use fig_timeline::{MediaAsset, Timeline};

pub use render::Quality;

/// What the playback threads read: the timeline and its media as of the last edit.
pub struct Snapshot {
    pub timeline: Timeline,
    pub media: Vec<MediaAsset>,
    /// The editor's revision this was taken at; a new one is only sent when it changes.
    pub revision: u64,
}

#[derive(Clone, Copy, Debug)]
struct ClockState {
    playing: bool,
    /// Where the play started, on the timeline.
    anchor_sec: f64,
    /// 1 plays forward; shuttle speeds are ±2, ±4, ±8, and -1 backward.
    rate: f64,
    started: Instant,
    /// Device frames played when the play started (audio-driven clocks only).
    started_frames: u64,
    audio_driven: bool,
}

/// The playhead's source of truth while playing.
pub struct Clock {
    state: Mutex<ClockState>,
    audio: Arc<audio::Output>,
}

impl Clock {
    /// Where the playhead is now.
    pub fn now(&self) -> f64 {
        let s = *self.state.lock().unwrap();
        if !s.playing {
            return s.anchor_sec;
        }
        if s.audio_driven {
            let played = self.audio.played_frames().saturating_sub(s.started_frames);
            return s.anchor_sec + played as f64 / fig_media::mixer::SAMPLE_RATE as f64;
        }
        s.anchor_sec + s.started.elapsed().as_secs_f64() * s.rate
    }

    pub fn is_playing(&self) -> bool {
        self.state.lock().unwrap().playing
    }

    pub fn rate(&self) -> f64 {
        self.state.lock().unwrap().rate
    }
}

/// The UI's handle on playback. Dropping it stops the threads.
pub struct Playback {
    clock: Arc<Clock>,
    audio: Arc<audio::Output>,
    preview: render::Preview,
    snapshot: Arc<Mutex<Arc<Snapshot>>>,
    revision: u64,
}

impl Playback {
    pub fn new(surface: rune::NativeSurface) -> Playback {
        let snapshot = Arc::new(Mutex::new(Arc::new(Snapshot {
            timeline: Timeline::default(),
            media: Vec::new(),
            revision: u64::MAX,
        })));
        let audio = Arc::new(audio::Output::open(snapshot.clone()));
        let clock = Arc::new(Clock {
            state: Mutex::new(ClockState {
                playing: false,
                anchor_sec: 0.0,
                rate: 1.0,
                started: Instant::now(),
                started_frames: 0,
                audio_driven: false,
            }),
            audio: audio.clone(),
        });
        let preview = render::Preview::start(surface, snapshot.clone(), clock.clone());
        Playback { clock, audio, preview, snapshot, revision: u64::MAX }
    }

    /// Gives the threads the timeline as it is now. Cheap when nothing changed (`revision` is the editor's).
    pub fn update(&mut self, timeline: &Timeline, media: &[MediaAsset], revision: u64) {
        if revision == self.revision {
            return;
        }
        self.revision = revision;
        *self.snapshot.lock().unwrap() =
            Arc::new(Snapshot { timeline: timeline.clone(), media: media.to_vec(), revision });
        self.preview.redraw();
    }

    pub fn has_sound(&self) -> bool {
        self.audio.is_available()
    }

    pub fn is_playing(&self) -> bool {
        self.clock.is_playing()
    }

    pub fn rate(&self) -> f64 {
        self.clock.rate()
    }

    pub fn position(&self) -> f64 {
        self.clock.now()
    }

    /// Plays from `from_sec` at `rate` (1 normal; shuttle speeds are silent).
    pub fn play(&self, from_sec: f64, rate: f64) {
        let audio_driven = rate == 1.0 && self.audio.is_available();
        if audio_driven {
            self.audio.start(from_sec);
        } else {
            self.audio.stop();
        }
        *self.clock.state.lock().unwrap() = ClockState {
            playing: true,
            anchor_sec: from_sec,
            rate,
            started: Instant::now(),
            started_frames: self.audio.played_frames(),
            audio_driven,
        };
        self.preview.redraw();
    }

    /// Stops where the clock is, and returns that position.
    pub fn pause(&self) -> f64 {
        let at = self.clock.now();
        self.audio.stop();
        let mut s = self.clock.state.lock().unwrap();
        s.playing = false;
        s.anchor_sec = at;
        drop(s);
        self.preview.redraw();
        at
    }

    /// Shows `sec` while stopped. `scrubbing` takes the quick path (the nearest frame now, the exact one once the
    /// pointer rests) and, with `audio_scrub`, plays the sound swept over since the last seek.
    pub fn seek(&self, sec: f64, scrubbing: bool, audio_scrub: bool) {
        let previous = {
            let mut s = self.clock.state.lock().unwrap();
            let previous = s.anchor_sec;
            s.anchor_sec = sec;
            if s.playing {
                // a seek while playing restarts the play from there
                s.started = Instant::now();
                s.started_frames = self.audio.played_frames();
            }
            previous
        };
        if self.clock.is_playing() {
            if self.clock.state.lock().unwrap().audio_driven {
                self.audio.start(sec);
                self.clock.state.lock().unwrap().started_frames = self.audio.played_frames();
            }
        } else if scrubbing && audio_scrub {
            self.audio.scrub(previous, sec);
        }
        self.preview.show(sec, scrubbing);
    }

    pub fn set_volume(&self, volume: f32, muted: bool) {
        self.audio.set_volume(volume, muted);
    }

    pub fn set_quality(&self, quality: Quality) {
        self.preview.set_quality(quality);
    }

    pub fn quality(&self) -> Quality {
        self.preview.quality()
    }

    /// Drops the open decoders (a project closed, or media relinked).
    pub fn reset_sources(&self) {
        self.preview.reset();
        self.audio.reset();
    }
}
