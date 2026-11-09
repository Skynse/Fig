//! Timeline hit testing: pointer position to the thing under it.
//!
//! Port of `TimelineView.HitTesting.cs` (inventory §6.1). Pure geometry over the viewport and lane
//! layout, so it unit-tests without a window. P2 grows this with clip edges/handles, marker tabs,
//! effect bars, keyframe diamonds and transition pills; the base (ruler, header, clip body) is here.

use fig_timeline::{Clip, TrackKind};

use crate::editor_state::EditorState;
use crate::timeline::{lane_height, track_top, CLIP_INSET, HEADER_W, LANE_PAD_BOTTOM, RULER_H};

#[derive(Clone, Debug, PartialEq)]
pub enum HitTarget {
    /// The ruler strip: a pointer down here scrubs the playhead.
    Ruler,
    /// A track header button (`eye`/`volume`), by column.
    Header { track_id: String, button: HeaderButton },
    /// The empty part of a track lane.
    Lane { track_id: String },
    /// A clip body, and whether the pointer is on its left/right trim edge.
    Clip { track_id: String, clip_id: String, edge: ClipEdge },
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum HeaderButton {
    Visibility,
    Mute,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ClipEdge {
    None,
    Left,
    Right,
}

/// How close to a clip's edge counts as the trim handle (C# `EdgeGrabWidth`).
pub const EDGE_GRAB: f32 = 6.0;

pub fn hit(editor: &EditorState, x: f32, y: f32) -> Option<HitTarget> {
    if y < RULER_H {
        return Some(HitTarget::Ruler);
    }
    // the lanes scroll under the pinned ruler, so hit testing works in content coordinates
    let y = y + editor.timeline.scroll_y;
    let doc = editor.doc();
    let mut top = RULER_H;
    for track in &doc.tracks {
        let h = lane_height(track);
        if y >= top && y < top + h {
            if x < HEADER_W {
                let mid = top + h / 2.0;
                if (y - mid).abs() < 9.0 {
                    if track.kind == TrackKind::Video && (20.0..40.0).contains(&x) {
                        return Some(HitTarget::Header { track_id: track.id.clone(), button: HeaderButton::Visibility });
                    }
                    if (46.0..66.0).contains(&x) {
                        return Some(HitTarget::Header { track_id: track.id.clone(), button: HeaderButton::Mute });
                    }
                }
                return Some(HitTarget::Lane { track_id: track.id.clone() });
            }
            let clip_top = top + CLIP_INSET;
            let clip_h = h - CLIP_INSET - LANE_PAD_BOTTOM;
            if y >= clip_top && y <= clip_top + clip_h {
                let time = editor.timeline.x_to_time(x);
                for clip in &track.clips {
                    if time >= clip.start_sec && time <= clip.end_sec() {
                        let x0 = editor.timeline.time_to_x(clip.start_sec);
                        let x1 = editor.timeline.time_to_x(clip.end_sec());
                        let edge = if x - x0 <= EDGE_GRAB {
                            ClipEdge::Left
                        } else if x1 - x <= EDGE_GRAB {
                            ClipEdge::Right
                        } else {
                            ClipEdge::None
                        };
                        return Some(HitTarget::Clip {
                            track_id: track.id.clone(),
                            clip_id: clip.id.clone(),
                            edge,
                        });
                    }
                }
            }
            return Some(HitTarget::Lane { track_id: track.id.clone() });
        }
        top += h;
    }
    None
}

/// The clip at a pointer position, if any (drag feedback uses this).
pub fn clip_at(editor: &EditorState, x: f32, y: f32) -> Option<&Clip> {
    let HitTarget::Clip { clip_id, .. } = hit(editor, x, y)? else { return None };
    editor.doc().tracks.iter().flat_map(|t| t.clips.iter()).find(|c| c.id == clip_id)
}

/// Pixel top of a track lane (helper for the gesture layer).
pub fn lane_top(editor: &EditorState, track_id: &str) -> Option<f32> {
    let doc = editor.doc();
    let index = doc.tracks.iter().position(|t| t.id == track_id)?;
    Some(track_top(doc, index))
}
