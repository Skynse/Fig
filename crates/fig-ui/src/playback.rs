//! Playback: the transport state and (next) the audio-device clock.
//!
//! The C# `PlaybackEngine` treats the audio device as the clock; the preview follows it. The port
//! keeps that split: [`TransportState`] is what the UI reads and writes, and the engine that ticks
//! it lives behind this module. The dev machine has no ALSA headers, so the default build uses a
//! monotonic timer clock; `--features audio` adds the cpal sink on a machine that has ALSA.

use crate::editor_state::EditorState;

/// `EditorViewModel` transport properties + the `PlaybackEngine` state the UI shows.
#[derive(Clone, Debug)]
pub struct TransportState {
    pub playing: bool,
    /// J/K/L shuttle: 0 stopped, ±1, ±2, ±4, ±8.
    pub shuttle: i32,
    pub loop_playback: bool,
    pub volume: f32,
    pub muted: bool,
    pub audio_scrub: bool,
    /// Where the last clock tick put the playhead (seconds).
    pub clock_sec: f64,
}

impl Default for TransportState {
    fn default() -> Self {
        TransportState {
            playing: false,
            shuttle: 0,
            loop_playback: false,
            volume: 1.0,
            muted: false,
            audio_scrub: false,
            clock_sec: 0.0,
        }
    }
}

impl TransportState {
    pub fn toggle_play(&mut self) {
        self.playing = !self.playing;
        if !self.playing {
            self.shuttle = 0;
        }
    }

    pub fn stop(&mut self) {
        self.playing = false;
        self.shuttle = 0;
    }

    /// Play forward/backward, one speed step per press (L/J).
    pub fn shuttle_towards(&mut self, forward: bool) {
        let next = match self.shuttle {
            0 => 1,
            1 => 2,
            2 => 4,
            4 => 8,
            8 => 8,
            -1 => if forward { 1 } else { -1 },
            -2 => if forward { -1 } else { -2 },
            -4 => if forward { -2 } else { -4 },
            _ => if forward { -4 } else { -8 },
        };
        self.shuttle = if forward { next } else { -next };
        self.playing = true;
    }
}

/// One transport tick. `dt` is wall-clock seconds since the previous tick. The real engine uses the
/// audio device's position instead; this keeps the UI honest until the sink lands.
pub fn tick(editor: &mut EditorState, dt: f64) {
    if !editor.transport.playing {
        return;
    }
    let speed = if editor.transport.shuttle != 0 {
        f64::from(editor.transport.shuttle)
    } else {
        1.0
    };
    let mut next = editor.playhead + dt * speed;
    let duration = editor.duration_sec();
    if next >= duration {
        if editor.transport.loop_playback && duration > 0.0 {
            next -= duration;
        } else {
            next = duration;
            editor.transport.playing = false;
            editor.transport.shuttle = 0;
        }
    }
    if next < 0.0 {
        next = 0.0;
    }
    editor.playhead = next;
    editor.transport.clock_sec = next;
    editor.ui_revision += 1;
}
