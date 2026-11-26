//! Transitions across a cut: the catalog, and blending the outgoing and incoming pictures.

mod basic;
mod creative;

use std::sync::LazyLock;

use super::{Frame, ParamSet};
use fig_timeline::{ParamDef, ParamMap, TransitionRef};

/// Built-in transition type ids the code refers to by name.
pub mod ids {
    pub const CROSS_DISSOLVE: &str = "cross-dissolve";
    pub const WIPE: &str = "wipe";
    pub const SLIDE: &str = "slide";
    pub const PUSH: &str = "push";
    pub const FADE_TO_BLACK: &str = "fade-to-black";
    pub const IRIS: &str = "iris";
    pub const CURTAIN: &str = "curtain";
    pub const SWIRL: &str = "swirl";
}

/// Blends two frames of the same size, `t` in 0..1 (0 all outgoing, 1 all incoming).
pub type Blend = fn(&Frame, &Frame, f64, &ParamSet) -> Frame;

pub struct TransitionEntry {
    pub type_id: &'static str,
    pub display_name: &'static str,
    pub category: &'static str,
    pub icon: &'static str,
    pub description: &'static str,
    pub default_duration_sec: f64,
    /// Progress at which catalog thumbnails sample this transition (0..1).
    pub preview_progress: f64,
    pub params: Vec<ParamDef>,
    blend: Blend,
}

impl TransitionEntry {
    pub(crate) fn new(type_id: &'static str, display_name: &'static str, blend: Blend) -> TransitionEntry {
        TransitionEntry {
            type_id,
            display_name,
            category: "Basic",
            icon: "blend",
            description: "",
            default_duration_sec: 0.5,
            preview_progress: 0.5,
            params: Vec::new(),
            blend,
        }
    }

    pub(crate) fn category(mut self, c: &'static str) -> Self {
        self.category = c;
        self
    }
    pub(crate) fn icon(mut self, i: &'static str) -> Self {
        self.icon = i;
        self
    }
    pub(crate) fn describe(mut self, d: &'static str) -> Self {
        self.description = d;
        self
    }
    pub(crate) fn duration(mut self, d: f64) -> Self {
        self.default_duration_sec = d;
        self
    }
    pub(crate) fn preview_at(mut self, p: f64) -> Self {
        self.preview_progress = p;
        self
    }
    pub(crate) fn params(mut self, p: Vec<ParamDef>) -> Self {
        self.params = p;
        self
    }

    pub fn default_params(&self) -> ParamMap {
        self.params.iter().map(|p| (p.key.clone(), p.default_value())).collect()
    }

    /// A new transition of this type, at its default length unless one is given.
    pub fn create_ref(&self, duration_sec: Option<f64>) -> TransitionRef {
        TransitionRef {
            type_id: self.type_id.into(),
            duration_sec: duration_sec.unwrap_or(self.default_duration_sec),
            params: self.default_params(),
        }
    }

    /// The two pictures mixed at `t`. Pictures of different sizes cut at the midpoint.
    pub fn blend(&self, outgoing: &Frame, incoming: &Frame, t: f64, params: &ParamMap) -> Frame {
        let t = if t.is_finite() { t.clamp(0.0, 1.0) } else { 0.0 };
        if !outgoing.same_size(incoming) {
            return if t < 0.5 { outgoing.clone() } else { incoming.clone() };
        }
        (self.blend)(outgoing, incoming, t, &ParamSet::new(&self.params, params))
    }
}

static ALL: LazyLock<Vec<TransitionEntry>> = LazyLock::new(|| {
    let mut all: Vec<TransitionEntry> = basic::entries().into_iter().chain(creative::entries()).collect();
    all.sort_by(|a, b| a.type_id.cmp(b.type_id));
    all
});

pub fn all() -> &'static [TransitionEntry] {
    &ALL
}

pub fn find(type_id: &str) -> Option<&'static TransitionEntry> {
    ALL.iter().find(|e| e.type_id == type_id)
}

// ---- finding the transition in force -----------------------------------------------------------

use fig_timeline::{Clip, Timeline, Track, TrackKind};

const ABUT_EPS: f64 = 1e-3;

/// A transition in force at a moment: the two clips (by index on their track) and how far through it is.
#[derive(Clone, Debug)]
pub struct ActiveTransition {
    pub track: usize,
    pub outgoing: usize,
    pub incoming: usize,
    pub type_id: String,
    pub progress: f64,
    pub duration_sec: f64,
    pub params: ParamMap,
    pub cut_sec: f64,
}

/// The first transition in force at `time_sec` on a visible video track.
pub fn find_active(timeline: &Timeline, time_sec: f64) -> Option<ActiveTransition> {
    (0..timeline.tracks.len()).find_map(|t| find_active_on(timeline, t, time_sec))
}

/// The transition in force on one track: abutting clips whose edges carry a transition, over the window centred on
/// the cut, [cut − D/2, cut + D/2).
pub fn find_active_on(timeline: &Timeline, track_index: usize, time_sec: f64) -> Option<ActiveTransition> {
    let track: &Track = &timeline.tracks[track_index];
    if track.kind != TrackKind::Video || !track.visible {
        return None;
    }
    let clips = &track.clips;
    // a pair's window is never longer than its longer edge, so only cuts that close can be active
    let reach = clips
        .iter()
        .map(|c| {
            let o = c.transition_out.as_ref().map_or(0.0, |t| t.duration_sec);
            let i = c.transition_in.as_ref().map_or(0.0, |t| t.duration_sec);
            o.max(i)
        })
        .fold(0.0, f64::max);
    if reach <= 1e-6 {
        return None;
    }
    let reach = reach / 2.0 + ABUT_EPS;
    for (i, a) in clips.iter().enumerate() {
        let cut = a.end_sec();
        if !a.enabled || (time_sec - cut).abs() > reach {
            continue;
        }
        for (j, b) in clips.iter().enumerate() {
            if i == j || !b.enabled || (b.start_sec - cut).abs() > ABUT_EPS {
                continue;
            }
            let Some(tx) = resolve_pair(a, b).filter(|t| t.duration_sec > 1e-6) else { continue };
            let d = tx.duration_sec;
            let start = cut - d / 2.0;
            if time_sec < start || time_sec >= cut + d / 2.0 {
                continue;
            }
            return Some(ActiveTransition {
                track: track_index,
                outgoing: i,
                incoming: j,
                type_id: tx.type_id,
                progress: ((time_sec - start) / d).clamp(0.0, 1.0),
                duration_sec: d,
                params: tx.params,
                cut_sec: cut,
            });
        }
    }
    None
}

/// The transition across a cut: a shared type when both edges declare one (the longer of the two), otherwise
/// whichever is set; the outgoing edge wins when they differ.
pub fn resolve_pair(a: &Clip, b: &Clip) -> Option<TransitionRef> {
    match (&a.transition_out, &b.transition_in) {
        (None, None) => None,
        (Some(o), Some(i)) if o.type_id == i.type_id => {
            Some(TransitionRef { duration_sec: o.duration_sec.max(i.duration_sec), ..o.clone() })
        }
        (Some(o), _) => Some(o.clone()),
        (None, Some(i)) => Some(i.clone()),
    }
}
