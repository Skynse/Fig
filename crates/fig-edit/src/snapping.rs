//! Snapping times to frames, and magnetic snapping to clip edges, markers, the playhead and the grid.

use std::collections::HashSet;

use super::TimelineEditor;

/// What a time snapped to.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum SnapKind {
    /// Nothing nearby: just the nearest frame.
    Frame,
    /// A grid line of the timeline.
    Grid,
    /// The edge of a clip (or the start of the timeline).
    ClipEdge,
    /// A marker, or an end of a range marker.
    Marker,
    /// The playhead.
    Playhead,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct SnapResult {
    pub time: f64,
    pub kind: SnapKind,
}

impl SnapResult {
    /// True when it caught on something (a grid line, a clip edge, a marker, the playhead).
    pub fn caught(&self) -> bool {
        self.kind != SnapKind::Frame
    }
}

/// How positions snap, set by the timeline view.
#[derive(Clone, Debug, PartialEq)]
pub struct SnapSettings {
    /// Drag and drop positions also snap to nearby clip edges, markers and the playhead, not only to frames.
    pub magnetic: bool,
    /// How far (seconds) something may be from a target and still snap to it. The view sets it from the zoom so the
    /// reach stays the same number of pixels.
    pub window_sec: f64,
    /// Spacing (seconds) of the snap grid (the visible gridlines), or none: what is snapped to when no clip edge,
    /// marker or playhead is closer.
    pub grid_sec: Option<f64>,
    /// The playhead position, offered as a target.
    pub playhead_sec: Option<f64>,
}

impl SnapSettings {
    pub const DEFAULT_WINDOW_SEC: f64 = 0.25;
}

impl Default for SnapSettings {
    fn default() -> Self {
        SnapSettings { magnetic: false, window_sec: Self::DEFAULT_WINDOW_SEC, grid_sec: None, playhead_sec: None }
    }
}

/// What a snap leaves out: the thing being dragged, so it snaps to other things, not itself.
#[derive(Clone, Debug, Default)]
pub struct SnapExclude<'a> {
    /// A clip, and the clips linked to it.
    pub clip_id: Option<&'a str>,
    pub marker_id: Option<&'a str>,
    pub clips: Option<&'a HashSet<String>>,
    /// Leave the playhead out as a target.
    pub no_playhead: bool,
}

impl TimelineEditor {
    pub fn snap_time_magnetic(&self, sec: f64) -> f64 {
        self.snap_magnetic(sec, &SnapExclude::default()).time
    }

    /// Snapping with a reason. When magnetic snapping is on, the closest of these within the window wins: clip edges,
    /// markers (and range edges), the playhead, the start of the timeline; failing those, the nearest grid line;
    /// failing that, the nearest frame.
    pub fn snap_magnetic(&self, sec: f64, exclude: &SnapExclude) -> SnapResult {
        let frame = self.snap_time(sec);
        let snap = &self.snap;
        if !snap.magnetic {
            return SnapResult { time: frame, kind: SnapKind::Frame };
        }
        let doc = self.doc();
        let exclude_group = exclude.clip_id.and_then(|id| doc.find_clip(id)).and_then(|c| c.link_group_id.as_deref());
        let mut best: Option<SnapResult> = None;
        let mut best_delta = f64::MAX;
        let mut consider = |target: f64, kind: SnapKind| {
            let delta = (target - sec).abs();
            if delta <= snap.window_sec && delta < best_delta {
                best_delta = delta;
                best = Some(SnapResult { time: target, kind });
            }
        };
        for clip in doc.clips() {
            if Some(clip.id.as_str()) == exclude.clip_id
                || (exclude_group.is_some() && clip.link_group_id.as_deref() == exclude_group)
                || exclude.clips.is_some_and(|s| s.contains(&clip.id))
            {
                continue;
            }
            consider(clip.start_sec, SnapKind::ClipEdge);
            consider(clip.end_sec(), SnapKind::ClipEdge);
        }
        for marker in doc.markers.iter().filter(|m| Some(m.id.as_str()) != exclude.marker_id) {
            consider(marker.start_sec, SnapKind::Marker);
            if marker.is_range() {
                consider(marker.end_sec(), SnapKind::Marker);
            }
        }
        if !exclude.no_playhead
            && let Some(playhead) = snap.playhead_sec
        {
            consider(playhead, SnapKind::Playhead);
        }
        // the start of the timeline is something to catch on, but only once there is something to line up with
        if doc.clips().next().is_some() {
            consider(0.0, SnapKind::ClipEdge);
        }
        if let Some(hit) = best {
            return hit;
        }
        if let Some(grid) = snap.grid_sec.filter(|g| *g > 1e-6) {
            let line = (sec / grid).round_ties_even() * grid;
            if (line - sec).abs() <= snap.window_sec {
                return SnapResult { time: self.snap_time(line), kind: SnapKind::Grid };
            }
        }
        SnapResult { time: frame, kind: SnapKind::Frame }
    }
}
