//! Sampling of keyframed properties. A property keeps its constant value until a keyframe track exists for it;
//! once one exists the value is interpolated across the clip-relative times.

use super::{Clip, CropSide, KeyframePoint, properties};

/// Stable keys for clip automation tracks stored on `Clip::keyframes`.
pub mod keys {
    pub const OPACITY: &str = "opacity";
    pub const VOLUME: &str = "volume";
    /// The speed curve: see `clip_time`.
    pub const SPEED: &str = "speed";
    pub const CROP_LEFT: &str = "cropL";
    pub const CROP_TOP: &str = "cropT";
    pub const CROP_RIGHT: &str = "cropR";
    pub const CROP_BOTTOM: &str = "cropB";
    pub const TEXT_SIZE: &str = "textSize";
}

/// Samples an automation track at a clip-relative time, or the fallback when there is no track.
pub fn evaluate_clip(clip: &Clip, key: &str, local_t: f64, fallback: f64) -> f64 {
    match clip.keyframes.get(key) {
        Some(track) if !track.is_empty() => evaluate(track, local_t),
        _ => fallback,
    }
}

/// Eased interpolation over a keyframe track (held outside its range).
pub fn evaluate(track: &[KeyframePoint], t: f64) -> f64 {
    let (Some(first), Some(last)) = (track.first(), track.last()) else {
        return 0.0;
    };
    if t <= first.time_sec {
        return first.value.as_number();
    }
    if t >= last.time_sec {
        return last.value.as_number();
    }
    for pair in track.windows(2) {
        let (a, b) = (&pair[0], &pair[1]);
        if t >= a.time_sec && t <= b.time_sec {
            let span = b.time_sec - a.time_sec;
            let u = if span <= 1e-9 { 0.0 } else { (t - a.time_sec) / span };
            let u = a.easing.apply(u);
            return a.value.as_number() + (b.value.as_number() - a.value.as_number()) * u;
        }
    }
    last.value.as_number()
}

/// True when the track has a keyframe at (near) the given clip-relative time.
pub fn has_keyframe_at(clip: &Clip, key: &str, local_t: f64) -> bool {
    clip.keyframes.get(key).is_some_and(|track| track.iter().any(|p| (p.time_sec - local_t).abs() < 1e-3))
}

/// The four crop insets (left, top, right, bottom) at a clip-relative time: base values when there is no track;
/// none for clips that can't be cropped.
pub fn sample_crop(clip: &Clip, local_t: f64) -> (f64, f64, f64, f64) {
    if !clip.is_croppable() {
        return (0.0, 0.0, 0.0, 0.0);
    }
    let at = |side| properties::crop(side).number_at(clip, local_t);
    (at(CropSide::Left), at(CropSide::Top), at(CropSide::Right), at(CropSide::Bottom))
}
