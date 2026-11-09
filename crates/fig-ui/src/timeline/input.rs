//! What the pointer does on the timeline. A press decides the gesture from what's under it; moves change the
//! timeline live (`TimelineEditor::begin_live`), and letting go records the whole drag as one undo step.

use std::collections::{HashMap, HashSet};

use fig_edit::markers::MarkerEdit;
use fig_edit::SnapExclude;
use fig_timeline::fade::FadeOf;
use fig_timeline::{ClipKind, KeyframePoint, TrackKind, clip_time};
use indexmap::IndexMap;
use rune::{Drag, Mods, Phase, PointerButton, Tx};

use super::geometry::*;
use crate::app::AppState;
use crate::editor::{Editor, Tool};

#[derive(Default)]
pub enum Gesture {
    #[default]
    Idle,
    Scrub,
    Pan {
        scroll0: f64,
        x0: f32,
    },
    Marquee {
        x0: f32,
        y0: f32,
        x1: f32,
        y1: f32,
        additive: bool,
        base: Vec<String>,
        /// The clip pressed with Shift/Ctrl: a click (no box) still selects it.
        click_clip: Option<String>,
        /// The empty space clicked (for picking a gap).
        gap: Option<(usize, f64)>,
    },
    Clip(Box<ClipDrag>),
    Copy(Box<CopyDrag>),
    Marker {
        id: String,
        grab: RangeGrab,
        offset: f64,
    },
    Transition {
        key: String,
        cut: f64,
        max: f64,
    },
    Track {
        id: String,
        to: usize,
        moved: bool,
    },
    Keyframe {
        clip: String,
        from: f64,
        copy: bool,
    },
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Mode {
    Move,
    Start,
    End,
    FadeIn(FadeOf),
    FadeOut(FadeOf),
}

#[derive(Clone)]
struct Orig {
    start: f64,
    dur: f64,
    src_in: f64,
    src_out: f64,
    curve: Option<Vec<KeyframePoint>>,
    track: String,
}

struct Partner {
    id: String,
    start: f64,
    dur: f64,
    src_in: f64,
    src_out: f64,
    curve: Option<Vec<KeyframePoint>>,
}

pub struct ClipDrag {
    pub mode: Mode,
    pub clip: String,
    t0: f64,
    x0: f32,
    members: Vec<String>,
    orig: HashMap<String, Orig>,
    roll: HashMap<String, Partner>,
    roll_lo: f64,
    roll_hi: f64,
    snap_exclude: HashSet<String>,
    magnet: Option<Magnet>,
    /// A blocked move: where the clips wanted to go, and the track they wanted.
    blocked: Option<(IndexMap<String, f64>, String)>,
    /// Drawn while blocked: the track the ghost is on and how far it moved.
    pub ghost: Option<(String, f64)>,
    collapse_to: Option<String>,
}

struct Magnet {
    track: usize,
    /// A trim: the main track's clips in order, packed after each step.
    order: Option<Vec<String>>,
    /// A move: the dragged clips that can go on the main track, earliest first; and its other clips.
    block: Option<Vec<String>>,
    others: Vec<String>,
    origins: Vec<(String, f64)>,
    insert: Option<usize>,
}

impl ClipDrag {
    /// Where the moved clips will drop into the main track: (track index, cut time, length).
    pub fn insert_mark(&self, doc: &fig_timeline::Timeline) -> Option<(usize, f64, f64)> {
        let m = self.magnet.as_ref()?;
        let index = m.insert?;
        let block = m.block.as_ref()?;
        let cut = if index == 0 { 0.0 } else { doc.find_clip(&m.others[index - 1]).map_or(0.0, |c| c.end_sec()) };
        let len = block.iter().filter_map(|id| doc.find_clip(id)).map(|c| c.dur_sec).sum();
        Some((m.track, cut, len))
    }
}

pub struct CopyDrag {
    pub ids: Vec<String>,
    pub anchor: String,
    pub anchor_start: f64,
    pub t0: f64,
    pub x0: f32,
    pub y0: f32,
    pub delta: f64,
    pub track: Option<usize>,
    pub started: bool,
}

/// What's under a canvas point.
pub fn hit(e: &Editor, x: f32, y: f32) -> Hit {
    let doc = e.doc();
    let tl = &e.tl;
    let lanes = lanes(doc, e.media(), tl.scale);
    let t = tl.t_of(x);
    // markers in the top lane
    if y < MARKER_LANE_H && x > HEADER_W {
        let mut best: Option<(f32, String, RangeGrab)> = None;
        for m in &doc.markers {
            let mx = tl.x_of(m.start_sec);
            if m.is_range() {
                let ex = tl.x_of(m.end_sec());
                let grab = if (x - mx).abs() <= 5.0 {
                    RangeGrab::Start
                } else if (x - ex).abs() <= 5.0 {
                    RangeGrab::End
                } else if x > mx && x < ex {
                    RangeGrab::Body
                } else {
                    continue;
                };
                return Hit::Marker { id: m.id.clone(), grab };
            }
            let d = (x - (mx + 4.0)).abs();
            if d <= 8.0 && best.as_ref().is_none_or(|b| d < b.0) {
                best = Some((d, m.id.clone(), RangeGrab::None));
            }
        }
        if let Some((_, id, grab)) = best {
            return Hit::Marker { id, grab };
        }
    }
    if y < RULER_H {
        return if x > HEADER_W { Hit::Ruler } else { Hit::Empty { track: None, time: t } };
    }
    let cy = tl.content_y(y);
    let add = add_track_rect(&lanes);
    if x >= add[0] && x <= add[0] + add[2] && cy >= add[1] && cy <= add[1] + add[3] {
        return Hit::AddTrack;
    }
    let Some(ti) = tl.track_at(&lanes, y) else { return Hit::Empty { track: None, time: t } };
    let track = &doc.tracks[ti];
    let lane = lanes[ti];
    if x < HEADER_W {
        let mid = lane.top + lane.height / 2.0 - HEADER_BUTTON / 2.0;
        for (button, bx) in header_buttons(track) {
            if x >= bx && x <= bx + HEADER_BUTTON && cy >= mid && cy <= mid + HEADER_BUTTON {
                return Hit::Header { track: ti, button: Some(button) };
            }
        }
        return Hit::Header { track: ti, button: None };
    }
    // transition badges on cuts
    for cut in fig_edit::transitions::transitions_on(doc, &track.id) {
        let cx = tl.x_of(cut.cut_sec);
        let body_mid = lane.clip_top() + lane.label + lane.body / 2.0;
        if (x - cx).abs() <= 15.0 && (cy - body_mid).abs() <= 16.0 {
            return Hit::Transition { key: cut.key(), cut: cut.cut_sec };
        }
    }
    let top = lane.clip_top();
    if cy >= top && cy <= top + lane.clip_height() {
        // the latest clip drawn wins where they overlap
        for clip in track.clips.iter().rev() {
            let x0 = tl.x_of(clip.start_sec);
            let x1 = tl.x_of(clip.end_sec());
            if x < x0 - 1.0 || x > x1 + 1.0 {
                continue;
            }
            // keyframe diamonds on the body's top edge
            let body_top = top + lane.label;
            if (cy - body_top).abs() <= 6.0 {
                for k in clip_keyframe_times(clip) {
                    let kx = tl.x_of(clip.start_sec + k);
                    if (x - kx).abs() <= 6.0 {
                        return Hit::Keyframe { clip: clip.id.clone(), time: k };
                    }
                }
            }
            let w = x1 - x0;
            let in_strip = lane.strip > 0.0 && cy >= top + lane.clip_height() - lane.strip;
            let of = fade_of(clip, in_strip);
            let (fi, fo) = fig_timeline::fade::get(clip, of);
            let near_top = cy <= body_top + 10.0 || in_strip;
            let part = if w > 3.0 * TRIM_EDGE_PX && near_top && fi > 0.0 && (x - tl.x_of(clip.start_sec + fi)).abs() <= 5.0 {
                Part::FadeIn(of)
            } else if w > 3.0 * TRIM_EDGE_PX && near_top && fo > 0.0 && (x - tl.x_of(clip.end_sec() - fo)).abs() <= 5.0 {
                Part::FadeOut(of)
            } else if x - x0 <= TRIM_EDGE_PX.min(w / 3.0) {
                if near_top && w > 4.0 * FADE_ZONE_PX && x - x0 <= TRIM_EDGE_PX && cy <= body_top + 10.0 && false {
                    Part::FadeIn(of)
                } else {
                    Part::Left
                }
            } else if x1 - x <= TRIM_EDGE_PX.min(w / 3.0) {
                Part::Right
            } else if near_top && w > 4.0 * FADE_ZONE_PX && x - x0 <= FADE_ZONE_PX && cy <= body_top + 10.0 {
                Part::FadeIn(of)
            } else if near_top && w > 4.0 * FADE_ZONE_PX && x1 - x <= FADE_ZONE_PX && cy <= body_top + 10.0 {
                Part::FadeOut(of)
            } else {
                Part::Body
            };
            return Hit::Clip { track: ti, clip: clip.id.clone(), part };
        }
    }
    Hit::Empty { track: Some(ti), time: t }
}

/// The times (from the clip's start) its own keyframes are at, any property.
pub fn clip_keyframe_times(clip: &fig_timeline::Clip) -> Vec<f64> {
    let mut times: Vec<f64> = clip
        .keyframes
        .iter()
        .filter(|(k, _)| k.as_str() != fig_timeline::automation_keys::SPEED)
        .flat_map(|(_, pts)| pts.iter().map(|p| p.time_sec))
        .collect();
    times.sort_by(f64::total_cmp);
    times.dedup_by(|a, b| (*a - *b).abs() < 1e-6);
    times
}

fn snap(e: &Editor, t: f64, exclude: &SnapExclude, alt: bool) -> (f64, bool) {
    if alt {
        return (e.ed.snap_time(t), false);
    }
    let r = e.ed.snap_magnetic(t, exclude);
    (r.time, r.caught())
}

/// Keeps the snap settings in step with the zoom and the playhead.
fn prepare_snap(e: &mut Editor, prefs: &crate::settings::Preferences) {
    e.ed.snap.window_sec = prefs.editing.snap_reach_px / f64::from(e.tl.pps);
    e.ed.snap.playhead_sec = Some(e.playhead);
    e.ed.snap.grid_sec = Some(ruler_scale(f64::from(e.tl.pps), e.fps()).1);
}

/// The canvas's drag handler: press, move and release of the primary (or middle) button.
pub fn on_drag(app: &mut AppState, d: &mut Drag, tx: &mut Tx) {
    let mods = tx.mods();
    match d.phase {
        Phase::Down => {
            app.popup = None;
            if tx.button() == PointerButton::Middle {
                if let Some(e) = app.editor.as_mut() {
                    e.tl.gesture = Gesture::Pan { scroll0: e.tl.scroll, x0: d.x };
                }
                return;
            }
            press(app, d.x, d.y, d.clicks, mods, tx);
        }
        Phase::Move => moved(app, d.x, d.y, mods),
        Phase::Up => released(app, d.x, d.y, d.cancelled(), mods),
    }
    tx.instant();
}

fn press(app: &mut AppState, x: f32, y: f32, clicks: u32, mods: Mods, tx: &mut Tx) {
    let prefs = app.preferences.clone();
    let linked = prefs.editing.linked_selection;
    let Some(e) = app.editor.as_mut() else { return };
    prepare_snap(e, &prefs);
    e.tl.gap = None;
    let target = hit(e, x, y);
    let t = e.tl.t_of(x).max(0.0);
    match target {
        Hit::Marker { id, grab } => {
            if clicks >= 2
                && let Some(loc) = fig_edit::markers::find_marker(e.doc(), &id)
            {
                let at = loc.timeline_sec();
                crate::editor::seek(app, at, false);
                return;
            }
            let (toggle, extend) = (mods.cmd, mods.shift);
            e.ed.select_marker(&id, toggle, extend, None);
            if toggle || extend {
                return;
            }
            let start = fig_edit::markers::find_marker(e.doc(), &id).map_or(0.0, |l| l.marker.start_sec);
            e.ed.begin_live();
            e.tl.gesture = Gesture::Marker { id, grab, offset: t - start };
        }
        Hit::Ruler => {
            e.tl.gesture = Gesture::Scrub;
            let (st, _) = snap(e, t, &SnapExclude { no_playhead: true, ..Default::default() }, mods.alt);
            crate::editor::seek(app, st, true);
        }
        Hit::AddTrack => {
            use crate::menu::{Item, Popup};
            let items = vec![
                Item::new("Video track", |s, _| add_track(s, TrackKind::Video)),
                Item::new("Audio track", |s, _| add_track(s, TrackKind::Audio)),
            ];
            let origin = rune::memo::window_layout("timeline.canvas").unwrap_or([0.0; 4]);
            crate::menu::open(app, Popup::at(origin[0] + x, origin[1] + y, items));
        }
        Hit::Header { track, button } => {
            let tr = e.doc().tracks[track].clone();
            match button {
                Some(HeaderButton::Toggle) if tr.kind == TrackKind::Video => {
                    e.ed.edit(|ed| ed.set_track_visible(&tr.id, !tr.visible));
                }
                Some(HeaderButton::Toggle) | Some(HeaderButton::Mute) => {
                    e.ed.edit(|ed| ed.set_track_muted(&tr.id, !tr.muted));
                }
                Some(HeaderButton::Delete) => {
                    e.ed.edit(|ed| ed.remove_track(&tr.id));
                    e.tl.track_selected = None;
                }
                None => {
                    e.tl.track_selected = Some(tr.id.clone());
                    e.ed.selection.replace(fig_timeline::SelectionKey::new(fig_timeline::SelectionGroup::Track, &tr.id));
                    e.tl.gesture = Gesture::Track { id: tr.id.clone(), to: track, moved: false };
                }
            }
        }
        Hit::Transition { key, cut } => {
            if clicks >= 2 {
                crate::editor::seek(app, cut, false);
                return;
            }
            e.ed.selection.select_transition(&key);
            let max = fig_edit::transitions::find_cut(e.doc(), &key)
                .map_or(1.0, |c| e.ed.max_transition_duration(&c.left_clip_id, &c.right_clip_id))
                .max(0.05);
            e.ed.begin_live();
            e.tl.gesture = Gesture::Transition { key, cut, max };
        }
        Hit::Keyframe { clip, time } => {
            e.ed.selection.select_only(&clip);
            let at = e.ed.find_clip(&clip).map_or(0.0, |c| c.start_sec) + time;
            e.ed.begin_live();
            e.tl.gesture = Gesture::Keyframe { clip, from: time, copy: mods.cmd };
            crate::editor::seek(app, at, false);
        }
        Hit::Clip { track, clip, part } => {
            if e.tool == Tool::Blade {
                blade(app, &clip, t, mods.shift);
                return;
            }
            let roll = mods.cmd && !mods.shift && matches!(part, Part::Left | Part::Right);
            // Shift or Ctrl on a clip body draws a box from there (a plain click still adds the clip)
            if !roll && (mods.shift || (mods.cmd && part == Part::Body && !e.ed.selection.is_selected(&clip))) {
                if mods.cmd && part == Part::Body && e.ed.selection.is_selected(&clip) {
                    // fallthrough to copy drag below
                } else {
                    let base = e.ed.selection.selected_clip_ids();
                    let cy = e.tl.content_y(y);
                    e.tl.gesture = Gesture::Marquee { x0: x, y0: cy, x1: x, y1: cy, additive: true, base, click_clip: Some(clip), gap: None };
                    return;
                }
            }
            // Ctrl-drag a selected clip's body: a copy follows the pointer
            if mods.cmd && part == Part::Body && e.ed.selection.is_selected(&clip) {
                let ids = e.ed.selection.selected_clip_ids();
                let anchor_start = e.ed.find_clip(&clip).map_or(0.0, |c| c.start_sec);
                e.tl.gesture = Gesture::Copy(Box::new(CopyDrag {
                    ids,
                    anchor: clip,
                    anchor_start,
                    t0: t,
                    x0: x,
                    y0: y,
                    delta: 0.0,
                    track: Some(track),
                    started: false,
                }));
                return;
            }
            let collapse = click_select(e, &clip, if roll { Mods::default() } else { mods }, linked);
            begin_clip_drag(e, &clip, part, t, x, roll, mods.alt || !linked, collapse);
        }
        Hit::Empty { track, time } => {
            if e.tool == Tool::Blade {
                return;
            }
            let base = if mods.shift || mods.cmd { e.ed.selection.selected_clip_ids() } else { Vec::new() };
            let cy = e.tl.content_y(y);
            e.tl.gesture = Gesture::Marquee {
                x0: x,
                y0: cy,
                x1: x,
                y1: cy,
                additive: mods.shift || mods.cmd,
                base,
                click_clip: None,
                gap: track.map(|ti| (ti, time)),
            };
        }
    }
}

fn add_track(app: &mut AppState, kind: TrackKind) {
    if let Some(e) = app.editor.as_mut() {
        e.ed.edit(|ed| ed.add_track(kind));
    }
}

/// A click on a clip: plain picks it (with its linked partners), Ctrl toggles it, Shift adds it. A plain click
/// inside a multi-selection keeps the selection for a drag, and narrows to the clip if it was only a click.
fn click_select(e: &mut Editor, clip: &str, mods: Mods, linked: bool) -> Option<String> {
    let group = if linked && !mods.alt { e.ed.link_group(clip) } else { vec![clip.to_string()] };
    if mods.cmd {
        if e.ed.selection.is_selected(clip) {
            for id in &group {
                e.ed.selection.deselect(id);
            }
        } else {
            e.ed.selection.select_clips(group);
        }
        return None;
    }
    if mods.shift {
        e.ed.selection.select_clips(group);
        return None;
    }
    if e.ed.selection.is_selected(clip) && e.ed.selection.clip_count() > group.len() {
        return Some(clip.to_string());
    }
    e.ed.selection.set_clips(group);
    None
}

#[allow(clippy::too_many_arguments)]
fn begin_clip_drag(e: &mut Editor, clip_id: &str, part: Part, t: f64, x: f32, roll: bool, alone: bool, collapse: Option<String>) {
    let mode = match part {
        Part::Left => Mode::Start,
        Part::Right => Mode::End,
        Part::FadeIn(of) => Mode::FadeIn(of),
        Part::FadeOut(of) => Mode::FadeOut(of),
        Part::Body => Mode::Move,
    };
    let doc = e.doc();
    // who acts: a move carries everything selected; a trim or fade acts on the clip and its partners
    let mut members: Vec<String> = Vec::new();
    if mode == Mode::Move && e.ed.selection.is_selected(clip_id) {
        members = e.ed.selection.selected_clip_ids().into_iter().filter(|id| doc.find_clip(id).is_some()).collect();
    }
    if members.is_empty() {
        members = if alone { vec![clip_id.to_string()] } else { e.ed.link_group(clip_id) };
    }
    let mut orig = HashMap::new();
    for id in &members {
        let (Some(c), Some(track)) = (doc.find_clip(id), e.ed.find_clip_track_id(id)) else { continue };
        orig.insert(
            id.clone(),
            Orig {
                start: c.start_sec,
                dur: c.dur_sec,
                src_in: c.source_in(),
                src_out: c.source_out(),
                curve: clip_time::curve(c).map(<[_]>::to_vec),
                track,
            },
        );
    }
    let mut drag = ClipDrag {
        mode,
        clip: clip_id.to_string(),
        t0: t,
        x0: x,
        members,
        orig,
        roll: HashMap::new(),
        roll_lo: f64::NEG_INFINITY,
        roll_hi: f64::INFINITY,
        snap_exclude: HashSet::new(),
        magnet: None,
        blocked: None,
        ghost: None,
        collapse_to: collapse,
    };
    if matches!(mode, Mode::Start | Mode::End) {
        begin_roll(e, &mut drag, mode == Mode::End, roll);
    }
    begin_magnet(e, &mut drag, roll);
    e.ed.begin_live();
    e.tl.gesture = Gesture::Clip(Box::new(drag));
}

fn begin_roll(e: &Editor, d: &mut ClipDrag, at_end: bool, roll: bool) {
    let doc = e.doc();
    let members: HashSet<String> = d.members.iter().cloned().collect();
    d.snap_exclude = members.clone();
    for id in &d.members {
        let Some(c) = doc.find_clip(id) else { continue };
        let Some(track) = e.ed.find_clip_track(id) else { continue };
        if !roll && e.ed.is_magnetic(&track.id) {
            continue;
        }
        let others: Vec<_> = track.clips.iter().filter(|o| !members.contains(&o.id)).collect();
        let edge = if at_end { c.end_sec() } else { c.start_sec };
        let touching = if roll {
            others.iter().find(|o| ((if at_end { o.start_sec } else { o.end_sec() }) - edge).abs() < 1e-4).copied()
        } else {
            None
        };
        let Some(n) = touching else {
            if at_end {
                let wall = others.iter().filter(|o| o.start_sec >= edge - 1e-6).map(|o| o.start_sec - edge).fold(f64::INFINITY, f64::min);
                d.roll_hi = d.roll_hi.min(wall);
            } else {
                let wall = others.iter().filter(|o| o.end_sec() <= edge + 1e-6).map(|o| o.end_sec() - edge).fold(-edge, f64::max);
                d.roll_lo = d.roll_lo.max(wall);
            }
            continue;
        };
        let p = Partner {
            id: n.id.clone(),
            start: n.start_sec,
            dur: n.dur_sec,
            src_in: n.source_in(),
            src_out: n.source_out(),
            curve: clip_time::curve(n).map(<[_]>::to_vec),
        };
        d.snap_exclude.insert(n.id.clone());
        if at_end {
            d.roll_hi = d.roll_hi.min(p.dur - 0.1);
            if n.has_source_range() {
                d.roll_lo = d.roll_lo.max(clip_time::local_at(n, 0.0));
            }
        } else {
            d.roll_lo = d.roll_lo.max(-(p.dur - 0.1));
            if n.has_source_range()
                && let Some(len) = source_duration(e, n)
            {
                d.roll_hi = d.roll_hi.min((clip_time::local_at(n, len) - p.dur).max(0.0));
            }
        }
        d.roll.insert(id.clone(), p);
    }
}

fn begin_magnet(e: &Editor, d: &mut ClipDrag, roll: bool) {
    let doc = e.doc();
    let Some(main_id) = e.ed.main_track_id().map(str::to_string) else { return };
    if !e.ed.is_magnetic(&main_id) {
        return;
    }
    let Some(main_index) = doc.tracks.iter().position(|t| t.id == main_id) else { return };
    let main = &doc.tracks[main_index];
    let on_main = |id: &String| main.clips.iter().any(|c| &c.id == id);
    let mut m = Magnet { track: main_index, order: None, block: None, others: Vec::new(), origins: Vec::new(), insert: None };
    match d.mode {
        Mode::Start | Mode::End => {
            if roll || !d.members.iter().any(on_main) {
                return;
            }
            m.order = Some(fig_edit::magnet::in_time_order(main));
        }
        Mode::Move => {
            let mut block: Vec<String> =
                d.members.iter().filter(|id| doc.find_clip(id).is_some_and(|c| c.kind() != ClipKind::Audio)).cloned().collect();
            let homes: HashSet<&String> = block.iter().filter_map(|id| d.orig.get(id).map(|o| &o.track)).collect();
            if block.is_empty() || homes.len() != 1 {
                return;
            }
            block.sort_by(|a, b| d.orig[a].start.total_cmp(&d.orig[b].start));
            m.others = fig_edit::magnet::in_time_order(main).into_iter().filter(|id| !block.contains(id)).collect();
            m.block = Some(block);
        }
        _ => return,
    }
    for clip in &main.clips {
        for member in e.ed.link_group(&clip.id) {
            if let Some(c) = doc.find_clip(&member) {
                m.origins.push((member, c.start_sec));
            }
        }
    }
    d.magnet = Some(m);
}

fn source_duration(e: &Editor, clip: &fig_timeline::Clip) -> Option<f64> {
    let id = clip.media_id()?;
    e.find_media(id).map(|a| a.duration_sec).filter(|d| *d > 0.0)
}

fn moved(app: &mut AppState, x: f32, y: f32, mods: Mods) {
    let Some(e) = app.editor.as_mut() else { return };
    e.tl.pointer = Some((x, y));
    e.tl.snap_guide = None;
    let t = e.tl.t_of(x).max(0.0);
    let gesture = std::mem::take(&mut e.tl.gesture);
    let gesture = match gesture {
        Gesture::Idle => Gesture::Idle,
        Gesture::Pan { scroll0, x0 } => {
            e.tl.set_scroll(scroll0 - f64::from((x - x0) / e.tl.pps));
            Gesture::Pan { scroll0, x0 }
        }
        Gesture::Scrub => {
            let (st, caught) = snap(e, t, &SnapExclude { no_playhead: true, ..Default::default() }, mods.alt);
            if caught {
                e.tl.snap_guide = Some(st);
            }
            e.tl.gesture = Gesture::Scrub;
            crate::editor::seek(app, st, true);
            return;
        }
        Gesture::Marquee { x0, y0, additive, base, click_clip, gap, .. } => {
            let cy = e.tl.content_y(y.max(RULER_H));
            let g = Gesture::Marquee { x0, y0, x1: x, y1: cy, additive, base: base.clone(), click_clip, gap };
            if (x - x0).abs() >= 4.0 || (cy - y0).abs() >= 4.0 {
                let hits = marquee_hits(e, x0, y0, x, cy);
                let mut ids: Vec<String> = if additive { base } else { Vec::new() };
                for h in hits {
                    if !ids.contains(&h) {
                        ids.push(h);
                    }
                }
                e.ed.selection.set_clips(ids);
            }
            g
        }
        Gesture::Marker { id, grab, offset } => {
            drag_marker(e, &id, grab, offset, t, mods.alt);
            Gesture::Marker { id, grab, offset }
        }
        Gesture::Transition { key, cut, max } => {
            let dx = f64::from((x - e.tl.x_of(cut)).abs() / e.tl.pps.max(1.0));
            let dur = e.ed.snap_time((2.0 * dx).clamp(0.05, max));
            if let Some(c) = fig_edit::transitions::find_cut(e.doc(), &key) {
                e.ed.set_transition_duration(&c.left_clip_id, &c.right_clip_id, dur);
            }
            Gesture::Transition { key, cut, max }
        }
        Gesture::Keyframe { clip, from, copy } => {
            let start = e.ed.find_clip(&clip).map_or(0.0, |c| c.start_sec);
            let (st, _) = snap(e, t, &SnapExclude { clip_id: Some(&clip), ..Default::default() }, mods.alt);
            let local = (st - start).max(0.0);
            let host = fig_edit::KeyframeHostRef::of_clip(&clip);
            if (local - from).abs() > 1e-6 {
                let r = fig_edit::KeyframeRef { host, time_sec: from };
                if copy {
                    e.ed.duplicate_keyframes(&r, local);
                } else {
                    e.ed.move_keyframes(&r, local, "timeline-keyframe");
                }
            }
            Gesture::Keyframe { clip, from: if copy { from } else { local }, copy }
        }
        Gesture::Track { id, moved, .. } => {
            let lanes = lanes(e.doc(), e.media(), e.tl.scale);
            let cy = e.tl.content_y(y);
            let to = lanes.iter().position(|l| cy < l.top + l.height / 2.0).unwrap_or(lanes.len());
            Gesture::Track { id, to, moved: moved || (y - e.tl.pointer.map_or(y, |p| p.1)).abs() > 0.0 }
        }
        Gesture::Copy(mut c) => {
            if !c.started && (x - c.x0).abs() < 4.0 && (y - c.y0).abs() < 4.0 {
                e.tl.gesture = Gesture::Copy(c);
                return;
            }
            c.started = true;
            let ids: HashSet<String> = c.ids.iter().cloned().collect();
            let raw = (c.anchor_start + t - c.t0).max(0.0);
            let (st, caught) = snap(e, raw, &SnapExclude { clips: Some(&ids), ..Default::default() }, mods.alt);
            if caught {
                e.tl.snap_guide = Some(st);
            }
            c.delta = st - c.anchor_start;
            let lanes = lanes(e.doc(), e.media(), e.tl.scale);
            c.track = e.tl.track_at(&lanes, y).or(c.track);
            Gesture::Copy(c)
        }
        Gesture::Clip(mut d) => {
            drag_clip(e, &mut d, x, y, t, mods.alt);
            Gesture::Clip(d)
        }
    };
    e.tl.gesture = gesture;
}

fn marquee_hits(e: &Editor, x0: f32, y0: f32, x1: f32, y1: f32) -> Vec<String> {
    let (l, r) = (x0.min(x1), x0.max(x1));
    let (top, bottom) = (y0.min(y1), y0.max(y1));
    let doc = e.doc();
    let lanes = lanes(doc, e.media(), e.tl.scale);
    let mut out = Vec::new();
    for (i, track) in doc.tracks.iter().enumerate() {
        let lane = lanes[i];
        let (ct, cb) = (lane.clip_top(), lane.clip_top() + lane.clip_height());
        if cb < top || ct > bottom {
            continue;
        }
        for c in &track.clips {
            let (cx0, cx1) = (e.tl.x_of(c.start_sec), e.tl.x_of(c.end_sec()));
            if cx1 >= l && cx0 <= r {
                out.push(c.id.clone());
            }
        }
    }
    out
}

fn drag_marker(e: &mut Editor, id: &str, grab: RangeGrab, offset: f64, t: f64, alt: bool) {
    let Some(loc) = fig_edit::markers::find_marker(e.doc(), id) else { return };
    let ex = SnapExclude { marker_id: Some(id), ..Default::default() };
    if loc.marker.is_range() && grab != RangeGrab::None {
        let m = loc.marker.clone();
        const MIN: f64 = 0.1;
        match grab {
            RangeGrab::Start => {
                let (st, _) = snap(e, t, &ex, alt);
                let start = st.min(m.end_sec() - MIN);
                e.ed.edit_marker(id, MarkerEdit { start_sec: Some(start), dur_sec: Some(m.end_sec() - start), ..Default::default() });
            }
            RangeGrab::End => {
                let (st, _) = snap(e, t, &ex, alt);
                e.ed.edit_marker(id, MarkerEdit { dur_sec: Some((st - m.start_sec).max(MIN)), ..Default::default() });
            }
            _ => {
                let (st, _) = snap(e, (t - offset).max(0.0), &ex, alt);
                e.ed.edit_marker(id, MarkerEdit { start_sec: Some(st), ..Default::default() });
            }
        }
        return;
    }
    let raw = (t - loc.origin_sec).max(0.0);
    let (st, _) = snap(e, raw + loc.origin_sec, &ex, alt);
    e.ed.move_marker(id, (st - loc.origin_sec).max(0.0));
}

fn drag_clip(e: &mut Editor, d: &mut ClipDrag, x: f32, y: f32, t: f64, alt: bool) {
    let delta_t = t - d.t0;
    match d.mode {
        Mode::Move => {
            if (x - d.x0).abs() < 2.0 && d.ghost.is_none() && d.magnet.as_ref().is_none_or(|m| m.insert.is_none()) && (t - d.t0).abs() < 1e-9 {
                return;
            }
            let origin = d.orig[&d.clip].start;
            let members: HashSet<String> = d.members.iter().cloned().collect();
            let raw_start = (origin + delta_t).max(0.0);
            let (snapped, caught) = snap(e, raw_start, &SnapExclude { clip_id: Some(&d.clip), clips: Some(&members), ..Default::default() }, alt);
            if caught {
                e.tl.snap_guide = Some(snapped);
            }
            let earliest = d.orig.values().map(|o| o.start).fold(f64::INFINITY, f64::min);
            let delta = (snapped - origin).max(-earliest);
            let targets: IndexMap<String, f64> = d.members.iter().map(|id| (id.clone(), d.orig[id].start + delta)).collect();
            d.ghost = None;
            d.blocked = None;
            let lanes = lanes(e.doc(), e.media(), e.tl.scale);
            let hover = e.tl.track_at(&lanes, y);
            if magnet_move(e, d, hover, raw_start - origin) {
                return;
            }
            let current = e.ed.find_clip_track_id(&d.clip);
            let hover_track = hover.map(|i| e.doc().tracks[i].clone());
            if let (Some(ht), Some(cur)) = (hover_track.as_ref(), current.as_ref())
                && &ht.id != cur
                && e.ed.find_track(cur).is_some_and(|c| c.kind == ht.kind)
            {
                let ids: Vec<String> = targets.keys().cloned().collect();
                if !e.ed.move_clips_to_track_live(&ids, &ht.id, &targets) {
                    d.ghost = Some((ht.id.clone(), delta));
                    d.blocked = Some((targets, ht.id.clone()));
                }
                pack_after_move(e, d);
                return;
            }
            if e.ed.would_overlap_move(&targets) {
                d.ghost = current.clone().map(|c| (c, delta));
                d.blocked = current.map(|c| (targets, c));
            } else {
                let doc = e.ed.live_doc();
                for (id, start) in &targets {
                    if let Some(c) = doc.find_clip_mut(id) {
                        c.start_sec = *start;
                    }
                }
            }
            pack_after_move(e, d);
        }
        Mode::Start => {
            resize_start(e, d, delta_t, alt);
            let moved = e.ed.find_clip(&d.clip).map_or(0.0, |c| c.start_sec) - d.orig[&d.clip].start;
            let clamped = moved.clamp(d.roll_lo.min(0.0), d.roll_hi.max(0.0));
            if (clamped - moved).abs() > 1e-9 {
                resize_start(e, d, delta_t + clamped - moved, true);
            }
            apply_roll(e, d, false);
            pack_live(e, d);
        }
        Mode::End => {
            resize_end(e, d, delta_t, alt);
            let moved = e.ed.find_clip(&d.clip).map_or(0.0, |c| c.dur_sec) - d.orig[&d.clip].dur;
            let clamped = moved.clamp(d.roll_lo.min(0.0), d.roll_hi.max(0.0));
            if (clamped - moved).abs() > 1e-9 {
                resize_end(e, d, delta_t + clamped - moved, true);
            }
            apply_roll(e, d, true);
            pack_live(e, d);
        }
        Mode::FadeIn(of) => {
            let start = e.ed.find_clip(&d.clip).map_or(0.0, |c| c.start_sec);
            e.ed.set_fade_in(&d.clip, (t - start).max(0.0), of);
        }
        Mode::FadeOut(of) => {
            let end = e.ed.find_clip(&d.clip).map_or(0.0, |c| c.end_sec());
            e.ed.set_fade_out(&d.clip, (end - t).max(0.0), of);
        }
    }
}

fn resize_start(e: &mut Editor, d: &ClipDrag, delta_t: f64, no_snap: bool) {
    let o = &d.orig[&d.clip];
    let raw = (o.start + delta_t).max(0.0);
    let ex = SnapExclude { clip_id: Some(&d.clip), clips: Some(&d.snap_exclude), ..Default::default() };
    let (snapped, caught) = snap(e, raw, &ex, no_snap);
    if caught {
        e.tl.snap_guide = Some(snapped);
    }
    let snap_delta = snapped - o.start;
    let lengths: HashMap<String, Option<f64>> =
        d.members.iter().map(|id| (id.clone(), e.ed.find_clip(id).and_then(|c| source_duration(e, c)))).collect();
    let doc = e.ed.live_doc();
    for id in &d.members {
        let o = &d.orig[id];
        let Some(c) = doc.find_clip_mut(id) else { continue };
        let orig_end = o.start + o.dur;
        let start = (o.start + snap_delta).max(0.0);
        if c.has_source_range()
            && let Some(curve) = &o.curve
        {
            let mut before = c.clone();
            before.set_source_range(o.src_in, o.src_out);
            before.keyframes.insert(fig_timeline::automation_keys::SPEED.to_string(), curve.clone());
            let delta = (start - o.start).max(clip_time::local_at(&before, 0.0)).min(o.dur - 0.1);
            c.start_sec = o.start + delta;
            c.dur_sec = orig_end - c.start_sec;
            clip_time::trim_start(c, curve, o.src_in, delta);
            clip_time::sync_source_out(c);
            continue;
        }
        clip_time::move_start_edge(c, start);
        c.dur_sec = (orig_end - start).max(0.1);
        if c.has_source_range() {
            let speed = c.speed.max(1e-6);
            let mut src_in = (o.src_in + (start - o.start) * speed).max(0.0);
            if src_in >= o.src_out {
                src_in = (o.src_out - 0.1).max(0.0);
            }
            c.set_source_range(src_in, o.src_out);
            c.dur_sec = ((o.src_out - src_in) / speed).max(0.1);
            c.start_sec = orig_end - c.dur_sec;
        }
        let _ = lengths.get(id);
    }
}

fn resize_end(e: &mut Editor, d: &ClipDrag, delta_t: f64, no_snap: bool) {
    let o = &d.orig[&d.clip];
    let raw = (o.start + o.dur + delta_t).max(o.start + 0.1);
    let ex = SnapExclude { clip_id: Some(&d.clip), clips: Some(&d.snap_exclude), ..Default::default() };
    let (snapped, caught) = snap(e, raw, &ex, no_snap);
    if caught {
        e.tl.snap_guide = Some(snapped);
    }
    let snap_delta = snapped - (o.start + o.dur);
    let lengths: HashMap<String, Option<f64>> =
        d.members.iter().map(|id| (id.clone(), e.ed.find_clip(id).and_then(|c| source_duration(e, c)))).collect();
    let doc = e.ed.live_doc();
    for id in &d.members {
        let o = &d.orig[id];
        let Some(c) = doc.find_clip_mut(id) else { continue };
        let max_src = lengths.get(id).copied().flatten();
        let speed = c.speed.max(1e-6);
        let has_curve = o.curve.is_some();
        let max_dur = if !c.has_source_range() {
            f64::MAX
        } else if let Some(max) = max_src.filter(|m| *m > o.src_in) {
            if has_curve { clip_time::local_at(c, max) } else { (max - o.src_in) / speed }
        } else {
            o.dur
        };
        let new_end = (o.start + o.dur + snap_delta).max(o.start + 0.1);
        let mut dur = (new_end - o.start).max(0.1);
        if max_dur > 0.1 {
            dur = dur.min(max_dur);
        }
        c.dur_sec = dur;
        if c.has_source_range() {
            let mut out = if has_curve { clip_time::source_at(c, dur) } else { o.src_in + dur * speed };
            if let Some(m) = max_src {
                out = out.min(m);
            }
            c.set_source_range(o.src_in, out);
        }
    }
}

fn apply_roll(e: &mut Editor, d: &ClipDrag, at_end: bool) {
    if d.roll.is_empty() {
        return;
    }
    let now: HashMap<String, (f64, f64)> =
        d.members.iter().filter_map(|id| e.ed.find_clip(id).map(|c| (id.clone(), (c.start_sec, c.dur_sec)))).collect();
    let doc = e.ed.live_doc();
    for id in &d.members {
        let (Some(p), Some(o), Some((start, dur))) = (d.roll.get(id), d.orig.get(id), now.get(id)) else { continue };
        let Some(c) = doc.find_clip_mut(&p.id) else { continue };
        if at_end {
            let delta = start + dur - (o.start + o.dur);
            c.start_sec = p.start + delta;
            c.dur_sec = p.dur - delta;
            if !c.has_source_range() {
                continue;
            }
            if let Some(curve) = &p.curve {
                clip_time::trim_start(c, curve, p.src_in, delta);
                clip_time::sync_source_out(c);
            } else {
                let mut restore = c.clone();
                restore.set_source_range(p.src_in, p.src_out);
                let src = clip_time::source_at(&restore, delta);
                c.set_source_range(src, p.src_out);
            }
        } else {
            let delta = start - o.start;
            c.dur_sec = p.dur + delta;
            if c.has_source_range() {
                let out = clip_time::source_at(c, c.dur_sec);
                c.set_source_range(p.src_in, out);
            }
        }
    }
}

fn pack_live(e: &mut Editor, d: &ClipDrag) {
    if let Some(m) = &d.magnet
        && let Some(order) = &m.order
    {
        e.ed.pack_track_live(m.track, order, None, 0.0);
    }
}

fn pack_after_move(e: &mut Editor, d: &ClipDrag) {
    if let Some(m) = &d.magnet
        && m.block.is_some()
        && let Some(track) = e.doc().tracks.get(m.track)
    {
        let order = fig_edit::magnet::in_time_order(track);
        e.ed.pack_track_live(m.track, &order, None, 0.0);
    }
}

/// A move step that could involve the magnetic main track: over it, the clips float under the pointer and the track
/// opens a hole at the nearest cut. True then; otherwise everything goes back and the ordinary move takes over.
fn magnet_move(e: &mut Editor, d: &mut ClipDrag, hover: Option<usize>, delta: f64) -> bool {
    let Some(m) = d.magnet.as_mut() else { return false };
    let Some(block) = m.block.clone() else { return false };
    {
        let doc = e.ed.live_doc();
        for (id, start) in &m.origins {
            if let Some(c) = doc.find_clip_mut(id) {
                c.start_sec = *start;
            }
        }
        for id in &d.members {
            if let Some(c) = doc.find_clip_mut(id) {
                c.start_sec = d.orig[id].start;
            }
        }
    }
    let home = d.orig[&block[0]].track.clone();
    let main_id = e.doc().tracks[m.track].id.clone();
    let from_main = home == main_id;
    let hover_kind = hover.map(|i| e.doc().tracks[i].kind);
    let over_main = hover == Some(m.track) || (from_main && hover_kind != Some(TrackKind::Video));
    if !over_main {
        m.insert = None;
        for id in &block {
            e.ed.put_on_track_live(id, &home);
        }
        let order = fig_edit::magnet::in_time_order(&e.doc().tracks[m.track]);
        e.ed.pack_track_live(m.track, &order, None, 0.0);
        return false;
    }
    for id in &block {
        e.ed.put_on_track_live(id, &main_id);
    }
    let block_dur: f64 = block.iter().map(|id| d.orig[id].dur).sum();
    let left = (d.orig[&block[0]].start + delta).max(0.0);
    let durations: Vec<f64> = m.others.iter().filter_map(|id| e.ed.find_clip(id).map(|c| c.dur_sec)).collect();
    let index = fig_edit::magnet::insert_index(durations, left + block_dur / 2.0);
    let others = m.others.clone();
    e.ed.pack_track_live(m.track, &others, Some(index), block_dur);
    let doc = e.ed.live_doc();
    let mut at = left;
    for id in &block {
        if let Some(c) = doc.find_clip_mut(id) {
            c.start_sec = at;
            at += c.dur_sec;
        }
    }
    let moved = left - d.orig[&block[0]].start;
    for id in d.members.iter().filter(|id| !block.contains(id)) {
        if let Some(c) = doc.find_clip_mut(id) {
            c.start_sec = (d.orig[id].start + moved).max(0.0);
        }
    }
    m.insert = Some(index);
    d.ghost = None;
    d.blocked = None;
    true
}

fn released(app: &mut AppState, x: f32, y: f32, cancelled: bool, mods: Mods) {
    let Some(e) = app.editor.as_mut() else { return };
    e.tl.snap_guide = None;
    let gesture = std::mem::take(&mut e.tl.gesture);
    if cancelled {
        if e.ed.is_live() {
            e.ed.cancel_live();
        }
        return;
    }
    match gesture {
        Gesture::Idle | Gesture::Pan { .. } => {}
        Gesture::Scrub => {
            let t = e.playhead;
            crate::editor::seek(app, t, false);
        }
        Gesture::Marquee { x0, y0, x1, y1, click_clip, gap, .. } => {
            let moved = (x1 - x0).abs() >= 4.0 || (y1 - y0).abs() >= 4.0;
            if !moved {
                if let Some(clip) = click_clip {
                    let group = e.ed.link_group(&clip);
                    if mods.cmd && e.ed.selection.is_selected(&clip) {
                        for id in group {
                            e.ed.selection.deselect(&id);
                        }
                    } else {
                        e.ed.selection.select_clips(group);
                    }
                } else if !mods.shift && !mods.cmd {
                    e.ed.selection.clear();
                    if let Some((ti, time)) = gap
                        && let Some(track) = e.doc().tracks.get(ti)
                        && let Some((a, b)) = e.ed.gap_at(&track.id, time)
                    {
                        e.tl.gap = Some((track.id.clone(), a, b));
                    }
                }
            }
        }
        Gesture::Marker { .. } | Gesture::Transition { .. } | Gesture::Keyframe { .. } => {
            e.ed.commit_live();
        }
        Gesture::Track { id, to, moved } => {
            if moved && let Some(from) = e.doc().tracks.iter().position(|t| t.id == id) {
                let to = if to > from { to - 1 } else { to };
                if to != from {
                    e.ed.edit(|ed| ed.move_track(&id, to));
                }
            }
        }
        Gesture::Copy(c) => {
            if c.started
                && let Some(track) = c.track.and_then(|i| e.doc().tracks.get(i)).map(|t| t.id.clone())
            {
                let copies = e.ed.duplicate_clips_at(&c.ids, &c.anchor, c.anchor_start + c.delta, &track);
                if copies.is_empty() {
                    e.say("No room there for the copy");
                }
            }
        }
        Gesture::Clip(d) => {
            finish_magnet(e, &d);
            if let Some((targets, near)) = &d.blocked {
                let ids: Vec<String> = targets.keys().cloned().collect();
                e.ed.keep_main_track_closed(|ed| ed.move_clips_to_free_track(&ids, near, targets));
            }
            e.ed.commit_live();
            // clicking (not dragging) a clip in a multi-selection narrows the selection to it
            if let Some(only) = &d.collapse_to
                && (x - d.x0).abs() < 4.0
            {
                let group = if app.preferences.editing.linked_selection && !mods.alt {
                    e.ed.link_group(only)
                } else {
                    vec![only.clone()]
                };
                e.ed.selection.set_clips(group);
            }
            let _ = y;
        }
    }
}

fn finish_magnet(e: &mut Editor, d: &ClipDrag) {
    let Some(m) = &d.magnet else { return };
    if let (Some(block), Some(index)) = (&m.block, m.insert) {
        let mut order = m.others.clone();
        let at = index.min(order.len());
        for (i, id) in block.iter().enumerate() {
            order.insert(at + i, id.clone());
        }
        e.ed.pack_track_live(m.track, &order, None, 0.0);
    } else if let Some(order) = &m.order {
        e.ed.pack_track_live(m.track, order, None, 0.0);
    } else if d.blocked.is_none()
        && let Some(track) = e.doc().tracks.get(m.track)
    {
        let order = fig_edit::magnet::in_time_order(track);
        e.ed.pack_track_live(m.track, &order, None, 0.0);
    }
}

/// The blade: cuts the clip at the pointer (Shift: every track there).
fn blade(app: &mut AppState, clip: &str, t: f64, all: bool) {
    let Some(e) = app.editor.as_mut() else { return };
    let at = e.ed.snap_time(t);
    let cut = if all { e.ed.split_all_at_playhead(at) } else { e.ed.edit(|ed| ed.cut(clip, at)) };
    if cut.is_empty() {
        e.say("Nothing to cut there");
    }
}

/// Hover: remembers the pointer (the blade line and hover highlights follow it).
pub fn hover(app: &mut AppState, x: f32, y: f32, tx: &mut Tx) {
    let Some(e) = app.editor.as_mut() else { return };
    let before = e.tl.pointer;
    e.tl.pointer = Some((x, y));
    if e.tool != Tool::Blade && before.is_some() && matches!(e.tl.gesture, Gesture::Idle) {
        // only the blade line and edge cursors depend on the pointer while idle
        tx.live_only();
    }
}

/// The wheel: plain scrolls the lanes, Shift scrolls sideways, Ctrl zooms about the pointer, Alt changes lane height.
pub fn wheel(app: &mut AppState, w: rune::WheelEvent) -> bool {
    let Some(e) = app.editor.as_mut() else { return false };
    let dy = if w.dy.abs() > w.dx.abs() { w.dy } else { w.dx };
    if w.mods.cmd {
        let factor = if dy > 0.0 { ZOOM_STEP } else { 1.0 / ZOOM_STEP };
        if dy.abs() > 0.0 {
            e.tl.zoom_at(w.x.max(HEADER_W), factor);
        }
        return true;
    }
    if w.mods.alt {
        e.tl.taller(if dy > 0.0 { 1.2 } else { 1.0 / 1.2 });
        return true;
    }
    if w.mods.shift || w.dx.abs() > w.dy.abs() {
        e.tl.set_scroll(e.tl.scroll - f64::from(dy / e.tl.pps));
        return true;
    }
    let lanes = lanes(e.doc(), e.media(), e.tl.scale);
    let max = (content_height(&lanes) - e.tl.height).max(0.0);
    e.tl.scroll_y = (e.tl.scroll_y - w.dy).clamp(0.0, max);
    true
}

/// While a drag is near the left or right edge, the view scrolls (faster the closer it is). Called each frame.
pub fn auto_scroll(e: &mut Editor, tx: &mut Tx) -> bool {
    let active = matches!(e.tl.gesture, Gesture::Clip(_) | Gesture::Copy(_) | Gesture::Marquee { .. } | Gesture::Scrub | Gesture::Marker { .. });
    let Some((x, _)) = e.tl.pointer else { return false };
    if !active {
        return false;
    }
    let right = e.tl.width - x;
    let left = x - HEADER_W;
    let speed = if right < AUTO_SCROLL_ZONE {
        (AUTO_SCROLL_ZONE - right) / AUTO_SCROLL_ZONE * 22.0
    } else if left < AUTO_SCROLL_ZONE && e.tl.scroll > 0.0 {
        -(AUTO_SCROLL_ZONE - left.max(0.0)) / AUTO_SCROLL_ZONE * 22.0
    } else {
        return false;
    };
    e.tl.set_scroll(e.tl.scroll + f64::from(speed / e.tl.pps));
    tx.wake_after(std::time::Duration::from_millis(16));
    true
}

/// Escape during a drag puts everything back.
pub fn cancel(e: &mut Editor) -> bool {
    if matches!(e.tl.gesture, Gesture::Idle) {
        return false;
    }
    e.tl.gesture = Gesture::Idle;
    if e.ed.is_live() {
        e.ed.cancel_live();
    }
    true
}
