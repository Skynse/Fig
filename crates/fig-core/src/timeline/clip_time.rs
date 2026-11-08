//! Where in its file a clip is at a moment of the timeline: the one place timeline time becomes file time.
//!
//! A clip plays at `Clip::speed`, or, when it has a speed curve (keyframes under `keys::SPEED`, in seconds from the
//! clip start), at the speed the curve gives at each moment. The clip keeps its length on the timeline; the curve
//! decides how much of the file it uses.

use super::automation::{self, keys};
use super::{Clip, KeyframeEasing, KeyframeMap, KeyframePoint, ParamValue, text_words};

pub const MIN_SPEED: f64 = 0.1;
pub const MAX_SPEED: f64 = 8.0;

/// The clip's speed curve, if it has one.
pub fn curve(clip: &Clip) -> Option<&[KeyframePoint]> {
    clip.keyframes.get(keys::SPEED).filter(|k| !k.is_empty()).map(Vec::as_slice)
}

pub fn has_curve(clip: &Clip) -> bool {
    curve(clip).is_some()
}

fn constant(clip: &Clip) -> f64 {
    if clip.speed <= 0.0 { 1.0 } else { clip.speed }
}

fn value(key: &KeyframePoint) -> f64 {
    key.value.as_number().clamp(MIN_SPEED, MAX_SPEED)
}

/// How fast the clip plays at `local_t` seconds from its start.
pub fn speed_at(clip: &Clip, local_t: f64) -> f64 {
    match curve(clip) {
        Some(keys) => curve_speed_at(keys, local_t),
        None => constant(clip),
    }
}

fn curve_speed_at(keys: &[KeyframePoint], t: f64) -> f64 {
    if t <= keys[0].time_sec {
        return value(&keys[0]);
    }
    for pair in keys.windows(2) {
        let (a, b) = (&pair[0], &pair[1]);
        if t <= b.time_sec {
            let span = b.time_sec - a.time_sec;
            let u = if span <= 1e-9 { 1.0 } else { (t - a.time_sec) / span };
            return value(a) + (value(b) - value(a)) * a.easing.apply(u);
        }
    }
    value(keys.last().unwrap())
}

/// The file time shown `local_t` seconds from the clip start (may be before or past its ends).
pub fn source_at(clip: &Clip, local_t: f64) -> f64 {
    clip.source_in() + source_offset(clip, local_t)
}

/// Seconds of the file played from the clip start to `local_t`: the area under the speed curve.
pub fn source_offset(clip: &Clip, local_t: f64) -> f64 {
    match curve(clip) {
        Some(keys) => area(keys, local_t),
        None => local_t * constant(clip),
    }
}

fn area(keys: &[KeyframePoint], t: f64) -> f64 {
    // before the first key (and before the clip start) the speed holds the first value
    if t <= keys[0].time_sec {
        return t * value(&keys[0]);
    }
    let mut area = keys[0].time_sec * value(&keys[0]);
    for pair in keys.windows(2) {
        let (a, b) = (&pair[0], &pair[1]);
        let span = b.time_sec - a.time_sec;
        if span <= 1e-9 {
            continue;
        }
        let u = ((t - a.time_sec) / span).min(1.0);
        area += span * (value(a) * u + (value(b) - value(a)) * eased_area(a.easing, u));
        if t <= b.time_sec {
            return area;
        }
    }
    let last = keys.last().unwrap();
    area + (t - last.time_sec) * value(last)
}

/// The integral of the easing curve from 0 to `u` (0..1).
fn eased_area(easing: KeyframeEasing, u: f64) -> f64 {
    let u = u.clamp(0.0, 1.0);
    match easing {
        KeyframeEasing::EaseIn => u * u * u / 3.0,
        KeyframeEasing::EaseOut => u - (1.0 - (1.0 - u).powi(3)) / 3.0,
        KeyframeEasing::EaseInOut => {
            if u <= 0.5 {
                2.0 * u * u * u / 3.0
            } else {
                1.0 / 12.0 + (u - 0.5) - 2.0 / 3.0 * (0.125 - (1.0 - u).powi(3))
            }
        }
        KeyframeEasing::Hold => 0.0,
        KeyframeEasing::Linear => u * u / 2.0,
    }
}

/// The time from the clip start when it shows `source_sec` of its file: the inverse of [`source_at`]. The speed is
/// never 0, so there is one answer.
pub fn local_at(clip: &Clip, source_sec: f64) -> f64 {
    let offset = source_sec - clip.source_in();
    let Some(keys) = curve(clip) else {
        return offset / constant(clip);
    };
    // the area grows with t: bracket it, then bisect
    let (mut lo, mut hi) = (0.0f64, 0.0f64);
    if offset >= 0.0 {
        hi = (offset / MAX_SPEED).max(1.0);
        while area(keys, hi) < offset {
            hi *= 2.0;
        }
    } else {
        lo = (offset / MAX_SPEED).min(-1.0);
        while area(keys, lo) > offset {
            lo *= 2.0;
        }
    }
    let mut i = 0;
    while i < 60 && hi - lo > 1e-7 {
        let mid = (lo + hi) / 2.0;
        if area(keys, mid) < offset {
            lo = mid;
        } else {
            hi = mid;
        }
        i += 1;
    }
    (lo + hi) / 2.0
}

/// How much of its file the clip plays during the project frame ending at `local_t`: one project frame times the
/// speed there. Below one of the file's frames it is slow motion, above it fast.
pub fn span_at(clip: &Clip, local_t: f64, project_frame_sec: f64) -> f64 {
    (source_at(clip, local_t) - source_at(clip, local_t - project_frame_sec)).abs()
}

/// How long on the timeline the clip takes to play `source_span` seconds of its file from its in point.
pub fn duration_for(clip: &Clip, source_span: f64) -> f64 {
    local_at(clip, clip.source_in() + source_span)
}

/// Sets the clip's out point to where the curve leaves it at its end. For a clip with a curve.
pub fn sync_source_out(clip: &mut Clip) {
    if clip.has_source_range() && has_curve(clip) {
        let out = source_at(clip, clip.dur_sec);
        clip.set_source_range(clip.source_in(), out);
    }
}

/// The left half of a cut: keyframes before `offset`, and one at it with the value there.
pub fn keep_before(clip: &mut Clip, offset: f64) {
    for (key, whole) in clip.keyframes.iter_mut() {
        if whole.is_empty() {
            continue;
        }
        let at = KeyframePoint::new(offset, ParamValue::Double(sample(key, whole, offset)));
        whole.retain(|k| k.time_sec < offset - 1e-9);
        whole.push(at);
    }
}

/// The right half of a cut: keyframes (and a caption's word timings) after `offset`, moved to start at 0.
pub fn keep_after(clip: &mut Clip, offset: f64) {
    if let Some(text) = clip.as_text_mut() {
        text_words::shift(&mut text.words, offset);
    }
    for (key, keys) in clip.keyframes.iter_mut() {
        if !keys.is_empty() {
            *keys = shift(key, keys, offset);
        }
    }
}

/// Moves a clip's start edge to `start_sec` (a left trim) without moving what is said in it: a caption's words,
/// timed from its start, are shifted so they stay where they are spoken. Duration is the caller's.
pub fn move_start_edge(clip: &mut Clip, start_sec: f64) {
    let delta = start_sec - clip.start_sec;
    if let Some(text) = clip.as_text_mut() {
        text_words::shift(&mut text.words, delta);
    }
    clip.start_sec = start_sec;
}

/// A deep copy of a clip's keyframes, for undo.
pub fn copy_keyframes(clip: &Clip) -> KeyframeMap {
    clip.keyframes.clone()
}

/// A left trim of a clip with a curve: the start moved `delta` seconds later (earlier when negative). The curve
/// stays where it was on the timeline, so the picture doesn't shift, and the in point is where the old curve was
/// at the new start. `original_keys` and `original_src_in` are the clip's before the trim began.
pub fn trim_start(clip: &mut Clip, original_keys: &[KeyframePoint], original_src_in: f64, delta: f64) {
    let src_in = original_src_in + area(original_keys, delta);
    clip.keyframes.insert(keys::SPEED.into(), shift(keys::SPEED, original_keys, delta));
    let out = clip.source_out().max(src_in);
    clip.set_source_range(src_in, out);
}

/// Keys moved `offset` seconds earlier, the part before 0 replaced by a key at 0 with the value there.
fn shift(key: &str, keys: &[KeyframePoint], offset: f64) -> Vec<KeyframePoint> {
    let mut shifted: Vec<KeyframePoint> = keys
        .iter()
        .filter(|k| k.time_sec > offset + 1e-9)
        .map(|k| KeyframePoint { time_sec: k.time_sec - offset, ..*k })
        .collect();
    if offset > 0.0 || shifted.is_empty() || shifted[0].time_sec > 1e-9 {
        let first = KeyframePoint::eased(0.0, ParamValue::Double(sample(key, keys, offset)), easing_at(keys, offset));
        shifted.insert(0, first);
    }
    shifted
}

fn sample(key: &str, keys: &[KeyframePoint], t: f64) -> f64 {
    if key == keys::SPEED { curve_speed_at(keys, t) } else { automation::evaluate(keys, t) }
}

/// The easing of the segment that contains `t`, so a key put there carries on into the next.
fn easing_at(keys: &[KeyframePoint], t: f64) -> KeyframeEasing {
    keys.iter().rev().find(|k| k.time_sec <= t).map_or(KeyframeEasing::Linear, |k| k.easing)
}
