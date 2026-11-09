//! Where things are on the timeline: lane heights, the ruler's scale, and what is under a point. The numbers are the
//! C# build's (`TimelineGeometry.cs`, `TimelineView.Theme.cs`).

use fig_timeline::fade::FadeOf;
use fig_timeline::{Clip, ClipBody, ClipKind, MediaAsset, Timeline, Track, TrackKind};

use super::rack::{self, Folds};

pub const HEADER_W: f32 = 140.0;
pub const MARKER_LANE_H: f32 = 20.0;
pub const TICK_RULER_H: f32 = 22.0;
pub const RULER_H: f32 = MARKER_LANE_H + TICK_RULER_H;
pub const CLIP_INSET: f32 = 6.0;
pub const LABEL_H: f32 = 14.0;
pub const CLIP_H: f32 = 72.0;
pub const AUDIO_CLIP_H: f32 = 52.0;
pub const AUDIO_STRIP_H: f32 = 22.0;
pub const TEXT_CLIP_H: f32 = 26.0;
pub const MIN_TEXT_CLIP_H: f32 = 18.0;
pub const LANE_PAD: f32 = 8.0;
pub const EMPTY_LANE_H: f32 = 34.0;
pub const RADIUS: f32 = 6.0;
pub const TRIM_EDGE_PX: f32 = 6.0;
pub const FADE_ZONE_PX: f32 = 14.0;
pub const MIN_LABEL_W: f32 = 24.0;
pub const DETAIL_W: f32 = 6.0;
pub const HEADER_BUTTON: f32 = 22.0;
pub const AUTO_SCROLL_ZONE: f32 = 48.0;
pub const MIN_PPS: f32 = 1.0;
pub const MAX_PPS: f32 = 2000.0;
pub const ZOOM_STEP: f32 = 1.25;

/// A text-only lane: a video track holding nothing but titles.
pub fn is_text_only(track: &Track) -> bool {
    track.kind == TrackKind::Video && !track.clips.is_empty() && track.clips.iter().all(|c| c.kind() == ClipKind::Text)
}

/// A video lane with a clip that plays its own sound (it gets a sound strip under the picture).
pub fn has_sound_strip(track: &Track, media: &[MediaAsset]) -> bool {
    track.kind == TrackKind::Video
        && track.clips.iter().any(|c| match &c.body {
            ClipBody::Video(v) => v.plays_audio && media.iter().any(|m| m.id == v.source_id && m.has_audio),
            _ => false,
        })
}

#[derive(Clone, Copy, Debug)]
pub struct Lane {
    pub top: f32,
    pub height: f32,
    /// Label strip above the clip body (0 on text lanes).
    pub label: f32,
    /// The clip body, including any sound strip.
    pub body: f32,
    /// The sound strip at the bottom of the body (0 when none).
    pub strip: f32,
    /// The racks are open (labelled rows) rather than folded strips.
    pub rack_open: bool,
    /// The tallest of the clips' racks, under the clips; the track's own rack is under that.
    pub clip_racks: f32,
}

impl Lane {
    pub fn clip_top(&self) -> f32 {
        self.top + CLIP_INSET
    }

    pub fn clip_height(&self) -> f32 {
        self.label + self.body
    }

    /// The top of a clip's first row of bars.
    pub fn clip_rack_top(&self) -> f32 {
        self.clip_top() + self.clip_height() + super::rack::PAD
    }

    /// The top of the track's own first row of bars.
    pub fn track_rack_top(&self) -> f32 {
        self.clip_top() + self.clip_height() + self.clip_racks + super::rack::PAD
    }
}

pub fn lane(track: &Track, media: &[MediaAsset], scale: f32, top: f32, folds: &Folds) -> Lane {
    let rack_open = folds.is_open(&track.id, scale);
    if track.clips.is_empty() && track.markers.is_empty() && track.effects.is_empty() {
        return Lane {
            top,
            height: EMPTY_LANE_H,
            label: 0.0,
            body: EMPTY_LANE_H - CLIP_INSET - LANE_PAD,
            strip: 0.0,
            rack_open,
            clip_racks: 0.0,
        };
    }
    let (label, body, strip) = if track.kind == TrackKind::Audio {
        (LABEL_H, AUDIO_CLIP_H * scale, 0.0)
    } else if is_text_only(track) {
        (0.0, (TEXT_CLIP_H * scale).max(MIN_TEXT_CLIP_H), 0.0)
    } else {
        let strip = if has_sound_strip(track, media) { AUDIO_STRIP_H * scale } else { 0.0 };
        (LABEL_H, CLIP_H * scale + strip, strip)
    };
    let clip_racks = rack::clip_racks_height(track, rack_open);
    let racks = clip_racks + rack::height(rack::Owner::Track(track), rack_open);
    Lane { top, height: CLIP_INSET + label + body + racks + LANE_PAD, label, body, strip, rack_open, clip_racks }
}

/// Every lane, top to bottom, in content coordinates (the ruler is above the first).
pub fn lanes(doc: &Timeline, media: &[MediaAsset], scale: f32, folds: &Folds) -> Vec<Lane> {
    let mut top = RULER_H;
    doc.tracks
        .iter()
        .map(|t| {
            let l = lane(t, media, scale, top, folds);
            top += l.height;
            l
        })
        .collect()
}

pub fn content_height(lanes: &[Lane]) -> f32 {
    lanes.last().map_or(RULER_H, |l| l.top + l.height) + HEADER_BUTTON + 12.0
}

/// The ruler's ticks at a zoom: a labelled major tick, and minor ones between (`RulerCalculator.PickScale`).
pub fn ruler_scale(pps: f64, fps: f64) -> (f64, f64, u32) {
    let frame = 1.0 / fps.max(1.0);
    let steps: [(f64, u32); 17] = [
        (frame, 1),
        (2.0 * frame, 2),
        (5.0 * frame, 5),
        (10.0 * frame, 5),
        (15.0 * frame, 3),
        (1.0, 5),
        (2.0, 4),
        (5.0, 5),
        (10.0, 5),
        (15.0, 3),
        (30.0, 6),
        (60.0, 6),
        (120.0, 4),
        (300.0, 5),
        (600.0, 10),
        (1800.0, 6),
        (3600.0, 6),
    ];
    for (major, div) in steps {
        if major * pps >= 104.0 {
            return (major, major / f64::from(div), div);
        }
    }
    (3600.0, 600.0, 6)
}

/// A ruler label: `M:SS` (or `H:MM:SS`) at everyday zoom, `MM:SS:FF` when ticks are under a second apart.
pub fn ruler_label(sec: f64, major: f64, fps: f64) -> String {
    let total = sec.max(0.0);
    if major < 1.0 - 1e-9 {
        let whole = total.floor();
        let frame = ((total - whole) * fps).round() as i64;
        let s = whole as i64;
        return format!("{:02}:{:02}:{:02}", s / 60, s % 60, frame);
    }
    let s = total.round() as i64;
    if s >= 3600 { format!("{}:{:02}:{:02}", s / 3600, (s / 60) % 60, s % 60) } else { format!("{}:{:02}", s / 60, s % 60) }
}

/// The part of a clip under the pointer.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Part {
    Body,
    Left,
    Right,
    FadeIn(FadeOf),
    FadeOut(FadeOf),
}

/// What's under a point of the timeline.
#[derive(Clone, Debug)]
pub enum Hit {
    Ruler,
    Marker { id: String, grab: RangeGrab },
    Header { track: usize, button: Option<HeaderButton> },
    AddTrack,
    Transition { key: String, cut: f64 },
    /// A keyframe diamond of any kind: `time` is from its host's start; `value` when it also drags up and down (a speed
    /// point).
    Keyframe { host: fig_edit::KeyframeHostRef, time: f64, value: bool },
    /// An effect, level or fade bar under the clips.
    Rack { track: usize, owner: String, item: rack::Item, part: RackPart },
    Clip { track: usize, clip: String, part: Part },
    Empty { track: Option<usize>, time: f64 },
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RangeGrab {
    None,
    Start,
    End,
    Body,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum HeaderButton {
    Toggle,
    Mute,
    Delete,
    /// Folds or opens the track's racks.
    Rack,
}

/// The part of a rack bar under the pointer.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RackPart {
    Body,
    Left,
    Right,
    /// The stopwatch: records a keyframe at the playhead.
    Animate,
    Power,
    Remove,
    /// The keyframe lane under an open bar's label.
    Keys,
}

/// The rack fold toggle: on the right of the header, level with the clips' label strip.
pub fn rack_toggle_rect(lane: &Lane) -> [f32; 4] {
    [HEADER_W - 28.0, lane.clip_top() + 1.0, 20.0, LABEL_H.max(14.0) - 2.0]
}

/// A speed line's band: a video clip's picture, a little inset. Up is faster, on a log scale with 1× in the middle and
/// 8× and ⅛× at the edges.
pub fn speed_band(lane: &Lane) -> (f32, f32) {
    let top = lane.clip_top() + lane.label + SPEED_PAD;
    (top, (lane.body - lane.strip - 2.0 * SPEED_PAD).max(4.0))
}

const SPEED_PAD: f32 = 5.0;
pub const SPEED_GRAB: f32 = 7.0;

pub fn speed_to_y((top, h): (f32, f32), speed: f64) -> f32 {
    let s = speed.clamp(fig_timeline::clip_time::MIN_SPEED, fig_timeline::clip_time::MAX_SPEED);
    top + h / 2.0 - (s.ln() / 8f64.ln()) as f32 * h / 2.0
}

pub fn y_to_speed((top, h): (f32, f32), y: f32) -> f64 {
    let s = (f64::from((top + h / 2.0 - y) / (h / 2.0)) * 8f64.ln()).exp();
    s.clamp(fig_timeline::clip_time::MIN_SPEED, fig_timeline::clip_time::MAX_SPEED)
}

/// The header's buttons: a video lane has visibility, sound and delete; an audio lane sound and delete.
pub fn header_buttons(track: &Track) -> Vec<(HeaderButton, f32)> {
    let x = |col: f32| 7.0 + col * (HEADER_BUTTON + 4.0);
    if track.kind == TrackKind::Video {
        vec![(HeaderButton::Toggle, x(0.0)), (HeaderButton::Mute, x(1.0)), (HeaderButton::Delete, x(2.0))]
    } else {
        vec![(HeaderButton::Toggle, x(0.0)), (HeaderButton::Delete, x(1.0))]
    }
}

/// The add-track button under the last lane's header.
pub fn add_track_rect(lanes: &[Lane]) -> [f32; 4] {
    let bottom = lanes.last().map_or(RULER_H, |l| l.top + l.height);
    [8.0, bottom + 4.0, HEADER_BUTTON, HEADER_BUTTON]
}

/// The fade a clip has a handle for, by which part of it the pointer is over: a video playing its own sound has
/// separate fades for that sound, in its strip.
pub fn fade_of(clip: &Clip, in_strip: bool) -> FadeOf {
    if in_strip && clip.as_video().is_some_and(|v| v.plays_audio) { FadeOf::Sound } else { FadeOf::Picture }
}

pub fn clip_color(kind: ClipKind) -> (&'static str, &'static str) {
    match kind {
        ClipKind::Video => ("#1679db", "#55a2b2"),
        ClipKind::Audio => ("#2d674a", "#589b74"),
        ClipKind::Text => ("#a84f7d", "#d17aa6"),
        ClipKind::Image => ("#5e57a8", "#8b84d6"),
    }
}
