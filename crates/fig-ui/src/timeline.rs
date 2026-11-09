//! The timeline canvas: ruler, track headers, lanes, clips, markers and the playhead.
//!
//! Metrics are the C# constants (`Fig.Core/Timeline/Viewport/TimelineGeometry.cs` +
//! `TimelineView.Theme.cs`, inventory §5.1). Drawing order follows `TimelineView.Rendering.cs`
//! (inventory §5.2). Clips are culled to the visible time window and drawn at two levels of
//! detail: below `CLIP_DETAIL_WIDTH` a flat sliver, otherwise the full bar with its label strip.
//!
//! Interaction lives in `timeline_hit`, `timeline_gesture` and `timeline_menu`; this module only
//! lays out and draws.

use fig_timeline::{Clip, ClipKind, Timeline, Track, TrackKind};
use rune::{canvas, el, icon, text, Color, El, Shape};

use crate::app::AppState;
use crate::editor_state::EditorState;
use crate::icons;
use crate::theme::{font, mono, Tokens, RADIUS_CLIP};

// ---- geometry (inventory §5.1) ----
pub const HEADER_W: f32 = 140.0;
pub const RULER_H: f32 = 42.0;
pub const MARKER_LANE_H: f32 = 20.0;
pub const TICK_RULER_H: f32 = 22.0;
pub const CLIP_INSET: f32 = 6.0;
pub const CLIP_LABEL_H: f32 = 14.0;
pub const VIDEO_LANE_H: f32 = 100.0;
pub const AUDIO_LANE_H: f32 = 80.0;
pub const EMPTY_LANE_H: f32 = 34.0;
pub const LANE_PAD_BOTTOM: f32 = 8.0;
pub const SPLIT_GAP: f32 = 1.0;
/// Narrower than this, a clip is a flat colour sliver (C# `ClipDetailWidth`).
pub const CLIP_DETAIL_WIDTH: f32 = 6.0;

/// The canvas is clipped by its panel, so a wide fill can overhang without affecting layout.
const WIDE: f32 = 8192.0;

/// `TimelineViewport` (inventory §5.8).
#[derive(Clone, Debug)]
pub struct TimelineState {
    /// `PixelsPerSecond`, default 100, clamped 1..=2000.
    pub pps: f32,
    /// `ScrollTime` in seconds.
    pub scroll: f64,
    /// Vertical track scroll in points (plain wheel, from `gestures.json`).
    pub scroll_y: f32,
    /// Key of the element under the pointer, for hover styling.
    pub hover: Option<String>,
    /// The drag in progress.
    pub gesture: crate::timeline_gesture::Gesture,
}

impl Default for TimelineState {
    fn default() -> Self {
        TimelineState { pps: 100.0, scroll: 0.0, scroll_y: 0.0, hover: None, gesture: crate::timeline_gesture::Gesture::Idle }
    }
}

impl TimelineState {
    pub fn time_to_x(&self, sec: f64) -> f32 {
        HEADER_W + ((sec - self.scroll) as f32) * self.pps
    }

    pub fn x_to_time(&self, x: f32) -> f64 {
        self.scroll + f64::from((x - HEADER_W) / self.pps)
    }

    pub fn zoom(&mut self, factor: f32) {
        self.pps = (self.pps * factor).clamp(1.0, 2000.0);
    }

    /// Zoom about a time, keeping it where it is on screen (Ctrl+wheel).
    pub fn zoom_about(&mut self, factor: f32, time_sec: f64, anchor_x: f32) {
        self.pps = (self.pps * factor).clamp(1.0, 2000.0);
        self.scroll = time_sec - f64::from((anchor_x - HEADER_W) / self.pps);
        if self.scroll < 0.0 {
            self.scroll = 0.0;
        }
    }

    pub fn scroll_by(&mut self, seconds: f64) {
        self.scroll = (self.scroll + seconds).max(0.0);
    }

    /// Fit the whole sequence into `lane_width` points (`Fit sequence`).
    pub fn fit(&mut self, duration_sec: f64, lane_width: f32) {
        let dur = duration_sec.max(0.1);
        self.pps = (lane_width / dur as f32).clamp(1.0, 2000.0);
        self.scroll = 0.0;
    }
}

pub fn lane_height(track: &Track) -> f32 {
    if track.clips.is_empty() && track.markers.is_empty() && track.effects.is_empty() {
        EMPTY_LANE_H
    } else if track.kind == TrackKind::Audio {
        AUDIO_LANE_H
    } else {
        VIDEO_LANE_H
    }
}

pub fn track_top(doc: &Timeline, index: usize) -> f32 {
    RULER_H + doc.tracks.iter().take(index).map(lane_height).sum::<f32>()
}

pub fn content_height(doc: &Timeline) -> f32 {
    RULER_H + doc.tracks.iter().map(lane_height).sum::<f32>()
}

pub fn clip_fill(clip: &Clip, t: &Tokens) -> (Color, Color) {
    match clip.kind() {
        ClipKind::Video => (t.video_clip, t.video_border),
        ClipKind::Audio => (t.audio_clip, t.audio_border),
        ClipKind::Text => (t.text_clip, t.text_border),
        ClipKind::Image => (t.image_clip, t.image_border),
    }
}

pub fn view(state: &EditorState, t: &Tokens) -> El {
    let doc = state.doc();
    let oy = state.timeline.scroll_y;
    let mut canvas_el = el().w_full().h_full();

    // lane bands first; the header column then paints over their left edge
    for (i, track) in doc.tracks.iter().enumerate() {
        let y = track_top(doc, i) - oy;
        let h = lane_height(track);
        let lane = if i % 2 == 0 { t.track_lane } else { t.track_lane_alt };
        canvas_el = canvas_el.child(el().absolute(0.0, y).w(WIDE).h(h).fill(lane));
    }

    // ruler background across the lanes (the header column covers its left part)
    canvas_el = canvas_el.child(el().absolute(0.0, 0.0).w(WIDE).h(RULER_H).fill(t.ruler_bg));

    // clips
    for (i, track) in doc.tracks.iter().enumerate() {
        let y = track_top(doc, i) - oy;
        let lane_h = lane_height(track);
        let clip_h = lane_h - CLIP_INSET - LANE_PAD_BOTTOM;
        for clip in &track.clips {
            let x = state.timeline.time_to_x(clip.start_sec);
            let w = (clip.dur_sec as f32) * state.timeline.pps;
            if x > WIDE || x + w < 0.0 {
                continue;
            }
            let selected = state
                .editor
                .selection
                .contains(&fig_timeline::SelectionKey::new(fig_timeline::SelectionGroup::Clip, &clip.id));
            canvas_el = canvas_el.child(clip_view(state, clip, x, y + CLIP_INSET, (w - SPLIT_GAP).max(1.0), clip_h, selected, t));
        }
    }

    // track header column over the lanes
    canvas_el = canvas_el
        .child(el().absolute(0.0, 0.0).w(HEADER_W).h(content_height(doc)).fill(t.track_header_bg))
        .child(el().absolute(HEADER_W - 1.0, 0.0).w(1.0).h(content_height(doc)).fill(t.border));

    for (i, track) in doc.tracks.iter().enumerate() {
        let y = track_top(doc, i) - oy;
        let h = lane_height(track);
        let mid = y + h / 2.0 - 8.0;
        // `TimelineEditor` drops saved auto labels ("V1"/"A1") and works them out again from the track index.
        let label = state.editor.track_label(&track.id);
        canvas_el = canvas_el
            .child(el().absolute(0.0, y + h - 1.0).w(HEADER_W).h(1.0).fill(t.border))
            .child(
                if track.kind == TrackKind::Audio {
                    icon(&icons::VOLUME, 13.0).color(t.text_faint).absolute(26.0, mid)
                } else {
                    icon(&icons::EYE, 13.0).color(t.text_faint).absolute(26.0, mid)
                },
            )
            .child(icon(&icons::VOLUME, 13.0).color(t.text_faint).absolute(52.0, mid))
            .child(text(label).font(font(11.0, 500.0)).color(t.text_secondary).absolute(100.0, mid + 1.0));
    }

    // ruler: ticks and labels
    canvas_el = canvas_el.child(ruler_view(state, t));

    // markers (timeline-level)
    for marker in &doc.markers {
        let x = state.timeline.time_to_x(marker.start_sec);
        canvas_el = canvas_el
            .child(icon(&icons::FLAG, 11.0).color(t.marker_default).absolute(x, 3.0))
            .child(text(marker.name.clone()).font(font(10.0, 500.0)).color(t.marker_default).absolute(x + 13.0, 1.0));
    }

    // playhead: triangle head in the marker lane, 1 px line to the bottom
    let px = state.timeline.time_to_x(state.playhead);
    canvas_el = canvas_el
        .child(
            canvas(Shape::new().fill(vec![[0.0, 0.0], [10.0, 0.0], [5.0, 8.0]], t.playhead))
                .absolute(px - 5.0, MARKER_LANE_H - 2.0)
                .w(10.0)
                .h(8.0),
        )
        .child(
            el().absolute(px, MARKER_LANE_H + 6.0)
                .w(1.0)
                .h(4096.0)
                .fill(t.playhead),
        );

    canvas_el
        .key("timeline.canvas")
        .on_drag(crate::timeline_gesture::on_drag)
        .on_secondary_press_local(|s: &mut AppState, event: rune::PointerEvent, _tx: &mut rune::Tx| {
            let origin = rune::memo::window_layout("timeline.canvas").map_or((0.0, 0.0), |rect| (rect[0], rect[1]));
            crate::timeline_menu::open(s, event.x, event.y, origin.0 + event.x, origin.1 + event.y);
            s.editor().ui_revision += 1;
        })
        .on_wheel(|s: &mut AppState, wheel: rune::WheelEvent, _tx: &mut rune::Tx| -> bool {
            crate::timeline_gesture::wheel(s, &wheel);
            true
        })
}

fn clip_view(state: &EditorState, clip: &Clip, x: f32, y: f32, w: f32, h: f32, selected: bool, t: &Tokens) -> El {
    let (fill, border) = clip_fill(clip, t);

    // zoomed out, a clip a few pixels wide shows nothing but its colour
    if w < CLIP_DETAIL_WIDTH {
        return el().absolute(x, y).w(w).h(h).fill(if selected { t.selection } else { fill });
    }

    let mut body = el()
        .key(format!("clip.{}", clip.id))
        .absolute(x, y)
        .w(w)
        .h(h)
        .fill(fill)
        .radius(RADIUS_CLIP)
        .clip_rounded(RADIUS_CLIP);
    if selected {
        body = body.border(2.0, t.selection);
    }

    // filmstrip: tile the source's thumbnail across the clip body, under the label strip
    if clip.kind() == ClipKind::Video {
        if let Some(source_id) = clip.media_id() {
            if let Some(thumb) = state.preview.thumbnail(source_id) {
                let body_h = (h - CLIP_LABEL_H).max(1.0);
                let tile_w = (body_h * 16.0 / 9.0).max(1.0);
                let mut tx = 0.0_f32;
                while tx < w {
                    body = body.child(el().absolute(tx, CLIP_LABEL_H).w(tile_w).h(body_h).pixels(&thumb));
                    tx += tile_w;
                }
            }
        }
    }

    match clip.kind() {
        ClipKind::Text => {
            // no label strip: a T badge then the words, drawn inside the bar
            body = body
                .child(
                    el().absolute(6.0, 4.0)
                        .w(12.0)
                        .h(12.0)
                        .radius(3.0)
                        .border(1.0, border)
                        .center()
                        .child(text("T").font(font(8.0, 600.0)).color(Color::WHITE)),
                )
                .child(
                    text(clip_text(clip))
                        .font(font(10.0, 500.0))
                        .color(Color::WHITE)
                        .absolute(22.0, 4.0),
                );
        }
        kind => {
            let label = match kind {
                ClipKind::Video => "Video clip",
                ClipKind::Audio => "Audio clip",
                ClipKind::Image => "Image",
                ClipKind::Text => unreachable!(),
            };
            body = body
                .child(el().absolute(0.0, 0.0).w(w).h(CLIP_LABEL_H).fill(border).opacity(0.22))
                .child(text(label).font(font(10.0, 400.0)).color(Color::WHITE).absolute(6.0, 1.0));
        }
    }
    body
}

fn clip_text(clip: &Clip) -> String {
    match &clip.body {
        fig_timeline::ClipBody::Text(t) => t.text.clone(),
        _ => String::new(),
    }
}

fn ruler_view(state: &EditorState, t: &Tokens) -> El {
    let mut view = el().key("timeline.ruler").absolute(HEADER_W, 0.0).w(WIDE - HEADER_W).h(RULER_H);
    let first = state.timeline.scroll as f32;
    let last = first + (WIDE - HEADER_W) / state.timeline.pps;

    // label every 2 s, minor tick every 0.4 s: the scale RulerCalculator picks at 100 px/s
    let label_step = 2.0_f32;
    let minor_step = 0.4_f32;

    let mut minor = (first / minor_step).ceil() * minor_step;
    while minor <= last {
        let major = (minor / label_step - (minor / label_step).round()).abs() < 0.001;
        if !major {
            let x = state.timeline.time_to_x(minor as f64) - HEADER_W;
            view = view.child(el().absolute(x, RULER_H - 5.0).w(1.0).h(4.0).fill(t.text_faint));
        }
        minor += minor_step;
    }

    let mut label_at = (first / label_step).ceil() * label_step;
    while label_at <= last {
        let x = state.timeline.time_to_x(label_at as f64) - HEADER_W;
        let secs = label_at as i64;
        let label = format!("{}:{:02}", secs / 60, secs % 60);
        view = view
            .child(el().absolute(x, MARKER_LANE_H + 4.0).w(1.0).h(RULER_H - MARKER_LANE_H - 4.0).fill(t.text_faint))
            .child(text(label).font(mono(10.0, 400.0)).color(t.text_muted).absolute(x + 4.0, MARKER_LANE_H + 2.0));
        label_at += label_step;
    }
    view
}
