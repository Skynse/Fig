//! Shared linear fade-in / fade-out envelope for any clip kind. Video uses it as an opacity multiplier; audio uses
//! it as a volume multiplier. A video clip's own sound has fades of its own (`VideoBody::audio_fade_in_sec`), so
//! picture and sound fade independently.

use super::{Clip, ClipBody, properties};

/// Which of a clip's fades: its picture's, or its sound's. They differ only on a video clip playing its own sound.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum FadeOf {
    Picture,
    Sound,
}

/// The fade-in and fade-out of the clip's picture or sound.
pub fn get(clip: &Clip, of: FadeOf) -> (f64, f64) {
    match (&clip.body, of) {
        (ClipBody::Video(v), FadeOf::Sound) => (v.audio_fade_in_sec, v.audio_fade_out_sec),
        _ => (clip.fade_in_sec, clip.fade_out_sec),
    }
}

/// Writes the fade-in and fade-out of the clip's picture or sound, as given.
pub fn set(clip: &mut Clip, of: FadeOf, fade_in: f64, fade_out: f64) {
    match (&mut clip.body, of) {
        (ClipBody::Video(v), FadeOf::Sound) => {
            v.audio_fade_in_sec = fade_in;
            v.audio_fade_out_sec = fade_out;
        }
        _ => {
            clip.fade_in_sec = fade_in;
            clip.fade_out_sec = fade_out;
        }
    }
}

/// Clamps fade durations so both are >= 0 and fade_in + fade_out <= dur_sec. When the sum exceeds the duration,
/// both are scaled proportionally.
pub fn clamp(fade_in: f64, fade_out: f64, dur_sec: f64) -> (f64, f64) {
    let (mut fade_in, mut fade_out) = (fade_in.max(0.0), fade_out.max(0.0));
    if dur_sec <= 1e-9 {
        return (0.0, 0.0);
    }
    let sum = fade_in + fade_out;
    if sum > dur_sec {
        let scale = dur_sec / sum;
        fade_in *= scale;
        fade_out *= scale;
    }
    (fade_in, fade_out)
}

/// Linear fade multiplier in [0,1] at local time `local_t` (0 at clip start). Kind-agnostic: callers multiply by
/// opacity or volume as appropriate.
pub fn envelope(local_t: f64, dur_sec: f64, fade_in_sec: f64, fade_out_sec: f64) -> f64 {
    let fin = if fade_in_sec <= 1e-9 { 1.0 } else { (local_t / fade_in_sec).clamp(0.0, 1.0) };
    let fout = if fade_out_sec <= 1e-9 { 1.0 } else { ((dur_sec - local_t) / fade_out_sec).clamp(0.0, 1.0) };
    fin * fout
}

/// The picture's envelope for a clip at local time (does not include opacity).
pub fn clip_envelope(clip: &Clip, local_t: f64) -> f64 {
    envelope(local_t, clip.dur_sec, clip.fade_in_sec, clip.fade_out_sec)
}

/// Opacity × fade envelope (video / compositing). Opacity may be keyframed.
pub fn effective_opacity(clip: &Clip, local_t: f64) -> f64 {
    properties::OPACITY.number_at(clip, local_t).clamp(0.0, 1.0) * clip_envelope(clip, local_t)
}

/// Volume × the sound's fade envelope (audio mixing). Volume may be keyframed.
pub fn effective_volume(clip: &Clip, local_t: f64) -> f64 {
    let volume = properties::VOLUME.number_at(clip, local_t);
    let (fade_in, fade_out) = get(clip, FadeOf::Sound);
    volume.clamp(0.0, 1.0) * envelope(local_t, clip.dur_sec, fade_in, fade_out)
}

/// Writes clamped fade durations onto the clip.
pub fn apply(clip: &mut Clip, fade_in_sec: f64, fade_out_sec: f64) {
    let (i, o) = clamp(fade_in_sec, fade_out_sec, clip.dur_sec);
    clip.fade_in_sec = i;
    clip.fade_out_sec = o;
}

/// Sets fade-in without shrinking an existing fade-out (caps against remaining room).
pub fn apply_fade_in(clip: &mut Clip, fade_in_sec: f64, of: FadeOf) {
    let (_, fade_out) = get(clip, of);
    let max = (clip.dur_sec - fade_out.max(0.0)).max(0.0);
    set(clip, of, fade_in_sec.max(0.0).min(max), fade_out);
}

/// Sets fade-out without shrinking an existing fade-in (caps against remaining room).
pub fn apply_fade_out(clip: &mut Clip, fade_out_sec: f64, of: FadeOf) {
    let (fade_in, _) = get(clip, of);
    let max = (clip.dur_sec - fade_in.max(0.0)).max(0.0);
    set(clip, of, fade_in, fade_out_sec.max(0.0).min(max));
}

/// The fades a clip has: a video clip's picture and sound, or one set for any other clip.
pub fn channels(clip: &Clip) -> &'static [FadeOf] {
    match clip.body {
        ClipBody::Video(_) => &[FadeOf::Picture, FadeOf::Sound],
        _ => &[FadeOf::Picture],
    }
}

/// After a split, the left half keeps fade-in and clears fade-out (then clamps).
pub fn apply_split_left(left: &mut Clip) {
    for &of in channels(left) {
        let (fade_in, _) = get(left, of);
        set(left, of, fade_in, 0.0);
        apply_fade_in(left, fade_in, of);
    }
}

/// After a split, the right half keeps fade-out and clears fade-in (then clamps).
pub fn apply_split_right(right: &mut Clip) {
    for &of in channels(right) {
        let (_, fade_out) = get(right, of);
        set(right, of, 0.0, fade_out);
        apply_fade_out(right, fade_out, of);
    }
}
