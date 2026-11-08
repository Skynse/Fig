//! Making clips from parts of others: the halves of a cut.

use super::{Clip, clip_time, fade};

/// A copy of the clip with a new id, placed at `start_sec` for `dur_sec` and playing `src_in_sec`..`src_out_sec`.
pub fn clone_with_range(source: &Clip, start_sec: f64, dur_sec: f64, src_in_sec: f64, src_out_sec: f64) -> Clip {
    let mut clone = source.duplicate();
    clone.start_sec = start_sec;
    clone.dur_sec = dur_sec;
    clone.set_source_range(src_in_sec, src_out_sec);
    clone
}

/// A new clip of the part of `clip` before `offset` (seconds into it).
pub fn before(clip: &Clip, offset: f64) -> Clip {
    let mut left = clone_with_range(clip, clip.start_sec, offset, clip.source_in(), clip_time::source_at(clip, offset));
    clip_time::keep_before(&mut left, offset);
    fade::apply_split_left(&mut left);
    left
}

/// A new clip of the part of `clip` from `offset` (seconds into it) on.
pub fn after(clip: &Clip, offset: f64) -> Clip {
    let mut right = clone_with_range(
        clip,
        clip.start_sec + offset,
        clip.dur_sec - offset,
        clip_time::source_at(clip, offset),
        clip.source_out(),
    );
    clip_time::keep_after(&mut right, offset);
    fade::apply_split_right(&mut right);
    right
}
