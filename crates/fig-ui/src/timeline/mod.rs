//! The timeline panel: its tabs and toolbar, the lanes with their clips, the ruler, markers and playhead, and the
//! gestures that edit them.
//!
//! It is drawn as plain elements, rebuilt from the document each time it changes; only clips in view are built.
//! Horizontal position is the view's own scroll time (there is no horizontal scrollbar), and the ruler stays pinned
//! while the lanes scroll under it.

pub mod draw;
pub mod geometry;
pub mod input;
pub mod menu;
pub mod rack;
pub mod toolbar;

use std::collections::HashMap;
use std::time::Instant;

use fig_timeline::{Timeline, Track};

use self::geometry::*;
use crate::settings::Preferences;

/// The timeline's own view state: zoom, scroll, lane height, and the gesture under way.
pub struct TimelineView {
    /// Pixels per second (1..2000, 100 by default).
    pub pps: f32,
    /// The time at the left edge of the lanes.
    pub scroll: f64,
    /// How far the lanes are scrolled down (the ruler stays).
    pub scroll_y: f32,
    /// Lane height scale (0.4..2).
    pub scale: f32,
    /// The canvas's size, as last laid out.
    pub width: f32,
    pub height: f32,
    pub gesture: input::Gesture,
    /// Where the pointer is over the canvas (for hover effects and the blade line).
    pub pointer: Option<(f32, f32)>,
    /// The snap guide's time while a drag caught on something.
    pub snap_guide: Option<f64>,
    /// A gap picked by clicking empty space (track, start, end): Delete closes it.
    pub gap: Option<(String, f64, f64)>,
    /// Where library drags or dropped files would land: (track index or none, time).
    pub drop_preview: Option<(Option<usize>, f64)>,
    /// The track whose header was clicked.
    pub track_selected: Option<String>,
    /// Which tracks' effect racks are folded.
    pub folds: rack::Folds,
    /// Clips sliding into place after a ripple edit: how far from their new start they began, and when.
    pub slides: HashMap<String, (f64, Instant)>,
}

impl TimelineView {
    pub fn new(_prefs: &Preferences) -> TimelineView {
        TimelineView {
            pps: 100.0,
            scroll: 0.0,
            scroll_y: 0.0,
            scale: 1.0,
            width: 800.0,
            height: 300.0,
            gesture: input::Gesture::Idle,
            pointer: None,
            snap_guide: None,
            gap: None,
            drop_preview: None,
            track_selected: None,
            folds: rack::Folds::default(),
            slides: HashMap::new(),
        }
    }

    pub fn x_of(&self, t: f64) -> f32 {
        HEADER_W + ((t - self.scroll) as f32) * self.pps
    }

    pub fn t_of(&self, x: f32) -> f64 {
        self.scroll + f64::from((x - HEADER_W) / self.pps)
    }

    /// The seconds the lanes show.
    pub fn visible_secs(&self) -> f64 {
        f64::from(((self.width - HEADER_W).max(1.0)) / self.pps)
    }

    pub fn set_scroll(&mut self, t: f64) {
        self.scroll = t.max(0.0);
    }

    /// Zooms keeping the time under `x` where it is.
    pub fn zoom_at(&mut self, x: f32, factor: f32) {
        let t = self.t_of(x);
        self.pps = (self.pps * factor).clamp(MIN_PPS, MAX_PPS);
        self.scroll = (t - f64::from((x - HEADER_W) / self.pps)).max(0.0);
    }

    /// Zooms about the playhead when it's in view, else about the middle.
    pub fn zoom(&mut self, factor: f32, playhead: f64) {
        let x = self.x_of(playhead);
        let x = if x > HEADER_W && x < self.width { x } else { HEADER_W + (self.width - HEADER_W) / 2.0 };
        self.zoom_at(x, factor);
    }

    pub fn zoom_to_fit(&mut self, end: f64) {
        self.pps = (((self.width - HEADER_W - 20.0).max(50.0)) / end.max(0.5) as f32).clamp(MIN_PPS, MAX_PPS);
        self.scroll = 0.0;
    }

    /// The zoom slider's 0..1000 position (log-mapped from 1 to 2000 px/s).
    pub fn zoom_slider(&self) -> f64 {
        (f64::from(self.pps / MIN_PPS).ln() / f64::from(MAX_PPS / MIN_PPS).ln() * 1000.0).clamp(0.0, 1000.0)
    }

    pub fn set_zoom_slider(&mut self, v: f64, playhead: f64) {
        let pps = MIN_PPS * (MAX_PPS / MIN_PPS).powf((v / 1000.0) as f32);
        self.zoom(pps / self.pps, playhead);
    }

    /// While playing: when the playhead runs off the visible page, turn to the next one (10% lead).
    pub fn keep_in_view(&mut self, playhead: f64, forward: bool) {
        let visible = self.visible_secs();
        let lead = visible * 0.10;
        if forward && playhead > self.scroll + visible {
            self.scroll = (playhead - lead).max(0.0);
        } else if playhead < self.scroll {
            self.scroll = (playhead - visible + lead).max(0.0);
        }
    }

    pub fn taller(&mut self, step: f32) {
        let s = (self.scale * step).clamp(0.4, 2.0);
        self.scale = if (s - 1.0).abs() < 0.02 { 1.0 } else { s };
    }

    /// Scales lanes so every track fits the panel's height.
    pub fn fit_tracks(&mut self, doc: &Timeline, media: &[fig_timeline::MediaAsset]) {
        let (mut lo, mut hi) = (0.4f32, 2.0f32);
        let room = self.height;
        for _ in 0..24 {
            let mid = (lo + hi) / 2.0;
            if content_height(&lanes(doc, media, mid, &self.folds)) <= room { lo = mid } else { hi = mid }
        }
        self.scale = lo;
        self.scroll_y = 0.0;
    }

    /// Starts the slide of clips a ripple edit moved: each is drawn from where it was and eases into place.
    pub fn slide_from(&mut self, before: &HashMap<String, f64>, doc: &Timeline) {
        let now = Instant::now();
        for clip in doc.tracks.iter().flat_map(|t| &t.clips) {
            if let Some(was) = before.get(&clip.id)
                && (was - clip.start_sec).abs() > 1e-6
            {
                self.slides.insert(clip.id.clone(), (was - clip.start_sec, now));
            }
        }
    }

    /// How far right of its start a clip is drawn while it slides (ease-out cubic over 220 ms).
    pub fn slide_offset(&self, clip_id: &str) -> f64 {
        let Some((from, at)) = self.slides.get(clip_id) else { return 0.0 };
        let t = (at.elapsed().as_secs_f64() / SLIDE_SECS).clamp(0.0, 1.0);
        from * (1.0 - t).powi(3)
    }

    /// Drops finished slides. True while any is still moving.
    pub fn tick_slides(&mut self) -> bool {
        self.slides.retain(|_, (_, at)| at.elapsed().as_secs_f64() < SLIDE_SECS);
        !self.slides.is_empty()
    }

    /// The content y of a canvas y (lanes scroll; the ruler doesn't).
    pub fn content_y(&self, y: f32) -> f32 {
        if y < RULER_H { y } else { y + self.scroll_y }
    }

    /// The track under a canvas y.
    pub fn track_at(&self, lanes: &[Lane], y: f32) -> Option<usize> {
        if y < RULER_H {
            return None;
        }
        let cy = self.content_y(y);
        lanes.iter().position(|l| cy >= l.top && cy < l.top + l.height)
    }
}

const SLIDE_SECS: f64 = 0.22;

/// Every clip's start, to slide the ones an edit moves.
pub fn starts(doc: &Timeline) -> HashMap<String, f64> {
    doc.tracks.iter().flat_map(|t| &t.clips).map(|c| (c.id.clone(), c.start_sec)).collect()
}

/// The lane a track has, by its index.
pub fn track_lane<'a>(lanes: &'a [Lane], index: usize) -> &'a Lane {
    &lanes[index.min(lanes.len().saturating_sub(1))]
}

/// The track's display name ("V1", "A2", or what it was called).
pub fn track_name(doc: &Timeline, track: &Track) -> String {
    fig_edit::track_label(doc, &track.id)
}
