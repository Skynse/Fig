//! Timeline pointer gestures: the drag state machines (`TimelineView.DragDrop.cs`,
//! `DragFeedback.cs`, `Magnet.cs`, `Resize.cs`, `Ripple.cs`, `Roll.cs`, `Viewport.cs`).
//!
//! Drags mutate the document through `TimelineEditor::begin_live` / `live_doc` / `commit_live`, so
//! the whole drag is ONE undo step and the canvas draws the live document each frame. Escape (or a
//! cancelled drag) puts it back with `cancel_live`.
//!
//! Implemented here: select (click/shift), move clips with snapping, trim either edge, ruler scrub,
//! marquee select, header visibility/mute toggles and wheel pan/zoom. Ripple/roll/slip/slide, the
//! main-track magnet packing and auto-scroll are the next pass; they join the same enum.

use std::collections::BTreeMap;

use fig_timeline::{Clip, ClipBody, TrackKind};
use rune::{Drag, Phase, Tx, WheelEvent};

use crate::app::AppState;
use crate::editor_state::EditorState;
use crate::timeline::HEADER_W;
use crate::timeline_hit::{ClipEdge, HitTarget};

#[derive(Clone, Debug, Default)]
pub enum Gesture {
    #[default]
    Idle,
    /// Ruler scrub or timeline background scrub.
    Scrub { from_ruler: bool },
    /// Moving one or more clips; `origins` are their starts when the drag began.
    MoveClips { clips: Vec<String>, origins: BTreeMap<String, f64>, grab_time: f64 },
    /// Dragging one clip's left/right edge.
    TrimEdge { clip: String, edge: ClipEdge, orig: Clip },
    /// Rubber-band selection over the lanes.
    Marquee { start_x: f32, start_y: f32, current_x: f32, current_y: f32 },
}

pub fn pointer_down(editor: &mut EditorState, x: f32, y: f32, shift: bool, _alt: bool) {
    let hit = crate::timeline_hit::hit(editor, x, y);
    match hit {
        Some(HitTarget::Ruler) => {
            editor.timeline.gesture = Gesture::Scrub { from_ruler: true };
            scrub_to(editor, x);
        }
        Some(HitTarget::Clip { clip_id, edge, .. }) => {
            if !shift {
                editor.editor.selection.select_only(&clip_id);
            } else {
                editor.editor.selection.add(fig_timeline::SelectionKey::new(fig_timeline::SelectionGroup::Clip, &clip_id));
            }
            if edge != ClipEdge::None {
                if let Some(clip) = find_clip(editor, &clip_id).cloned() {
                    editor.timeline.gesture = Gesture::TrimEdge { clip: clip_id, edge, orig: clip };
                    editor.editor.begin_live();
                }
            } else {
                let selected = editor.editor.selection.selected_clip_ids();
                let mut origins = BTreeMap::new();
                for id in &selected {
                    if let Some(clip) = find_clip(editor, id) {
                        origins.insert(id.clone(), clip.start_sec);
                    }
                }
                let grab_time = editor.timeline.x_to_time(x);
                editor.timeline.gesture = Gesture::MoveClips { clips: selected, origins, grab_time };
                editor.editor.begin_live();
            }
        }
        Some(HitTarget::Header { track_id, button }) => {
            toggle_header(editor, &track_id, button);
            editor.timeline.gesture = Gesture::Idle;
        }
        Some(HitTarget::Lane { .. }) | None => {
            if !shift {
                editor.editor.selection.clear();
            }
            editor.timeline.gesture =
                Gesture::Marquee { start_x: x, start_y: y, current_x: x, current_y: y };
        }
    }
    editor.ui_revision += 1;
}

pub fn pointer_move(editor: &mut EditorState, x: f32, y: f32, shift: bool) {
    match editor.timeline.gesture.clone() {
        Gesture::Scrub { .. } => scrub_to(editor, x),
        Gesture::MoveClips { clips, origins, grab_time } => {
            let mut delta = editor.timeline.x_to_time(x) - grab_time;
            let leader = origins.values().copied().next().unwrap_or(0.0);
            let snapped = editor.editor.snap_time(leader + delta) - leader;
            if (snapped - delta).abs() < 12.0 / f64::from(editor.timeline.pps) {
                delta = snapped;
            }
            let doc = editor.editor.live_doc();
            for id in &clips {
                let Some(origin) = origins.get(id).copied() else { continue };
                if let Some(clip) = doc.find_clip_mut(id) {
                    clip.start_sec = (origin + delta).max(0.0);
                }
            }
            editor.save = crate::editor_state::SaveState::Dirty;
        }
        Gesture::TrimEdge { clip, edge, orig } => {
            let at = editor.timeline.x_to_time(x);
            let at = editor.editor.snap_time(at);
            let delta = at - match edge {
                ClipEdge::Left => orig.start_sec,
                ClipEdge::Right => orig.end_sec(),
                ClipEdge::None => return,
            };
            let min_dur = 1.0 / 30.0;
            let doc = editor.editor.live_doc();
            if let Some(live) = doc.find_clip_mut(&clip) {
                match &orig.body {
                    ClipBody::Video(v) => {
                        let (in0, out0) = (v.src_in_sec, v.src_out_sec);
                        match edge {
                            ClipEdge::Left => live.set_source_range((in0 + delta).max(0.0), out0),
                            ClipEdge::Right => live.set_source_range(in0, (out0 + delta).max(in0 + min_dur)),
                            ClipEdge::None => {}
                        }
                        let span = match edge {
                            ClipEdge::Left => out0 - (in0 + delta).max(0.0),
                            _ => (out0 + delta).max(in0 + min_dur) - in0,
                        };
                        live.dur_sec = fig_timeline::clip_time::duration_for(live, span);
                        if edge == ClipEdge::Left {
                            live.start_sec = (orig.start_sec + delta).max(0.0);
                        }
                    }
                    ClipBody::Audio(a) => {
                        let (in0, out0) = (a.src_in_sec, a.src_out_sec);
                        match edge {
                            ClipEdge::Left => live.set_source_range((in0 + delta).max(0.0), out0),
                            ClipEdge::Right => live.set_source_range(in0, (out0 + delta).max(in0 + min_dur)),
                            ClipEdge::None => {}
                        }
                        let span = match edge {
                            ClipEdge::Left => out0 - (in0 + delta).max(0.0),
                            _ => (out0 + delta).max(in0 + min_dur) - in0,
                        };
                        live.dur_sec = fig_timeline::clip_time::duration_for(live, span);
                        if edge == ClipEdge::Left {
                            live.start_sec = (orig.start_sec + delta).max(0.0);
                        }
                    }
                    _ => match edge {
                        ClipEdge::Left => {
                            let new_start = (orig.start_sec + delta).clamp(0.0, orig.end_sec() - min_dur);
                            live.start_sec = new_start;
                            live.dur_sec = orig.end_sec() - new_start;
                        }
                        ClipEdge::Right => {
                            live.dur_sec = (orig.dur_sec + delta).max(min_dur);
                        }
                        ClipEdge::None => {}
                    },
                }
            }
            editor.save = crate::editor_state::SaveState::Dirty;
        }
        Gesture::Marquee { start_x, start_y, .. } => {
            editor.timeline.gesture = Gesture::Marquee { start_x, start_y, current_x: x, current_y: y };
        }
        Gesture::Idle => {}
    }
    editor.ui_revision += 1;
}

pub fn pointer_up(editor: &mut EditorState) {
    if let Gesture::Marquee { start_x, start_y, current_x, current_y } = editor.timeline.gesture.clone() {
        select_in_rect(editor, start_x, start_y, current_x, current_y);
    }
    if editor.editor.is_live() {
        editor.editor.commit_live();
    }
    editor.timeline.gesture = Gesture::Idle;
    editor.ui_revision += 1;
}

/// Escape mid-drag: put the document back and drop the gesture.
pub fn cancel(editor: &mut EditorState) {
    if editor.editor.is_live() {
        editor.editor.cancel_live();
    }
    editor.timeline.gesture = Gesture::Idle;
    editor.ui_revision += 1;
}

/// Wheel entry point: resolves the pattern against `gestures.json` and applies the bound gesture
/// (`GestureRegistry`: Ctrl+Wheel zooms, plain wheel scrolls tracks, Shift+Wheel scrolls time).
pub fn wheel(app: &mut AppState, wheel: &WheelEvent) {
    let direction = if wheel.dy < 0.0 { "WheelUp" } else { "WheelDown" };
    let prefix = if wheel.mods.cmd || wheel.mods.ctrl {
        "Ctrl+"
    } else if wheel.mods.shift {
        "Shift+"
    } else if wheel.mods.alt {
        "Alt+"
    } else {
        ""
    };
    let pattern = format!("{prefix}{direction}");
    let gesture = app.gestures.resolve(&pattern);
    let editor = app.editor();

    let content = crate::timeline::content_height(editor.doc());
    match gesture {
        crate::services::TimelineGesture::ZoomIn | crate::services::TimelineGesture::ZoomOut => {
            let factor = if wheel.dy < 0.0 { 1.1 } else { 0.9 };
            let time = editor.timeline.x_to_time(wheel.x);
            editor.timeline.zoom_about(factor, time, wheel.x);
        }
        crate::services::TimelineGesture::ScrollHorizontal => {
            let seconds = -f64::from(wheel.dy) / f64::from(editor.timeline.pps);
            editor.timeline.scroll_by(seconds);
        }
        crate::services::TimelineGesture::ScrollVertical => {
            let max = (content - 120.0).max(0.0);
            editor.timeline.scroll_y = (editor.timeline.scroll_y + wheel.dy).clamp(0.0, max);
        }
        _ => {}
    }
    editor.ui_revision += 1;
}

fn scrub_to(editor: &mut EditorState, x: f32) {
    let time = editor.timeline.x_to_time(x).max(0.0);
    editor.playhead = time;
    editor.transport.clock_sec = time;
}

fn find_clip<'a>(editor: &'a EditorState, id: &str) -> Option<&'a Clip> {
    editor.doc().tracks.iter().flat_map(|t| t.clips.iter()).find(|c| c.id == id)
}

fn toggle_header(editor: &mut EditorState, track_id: &str, button: crate::timeline_hit::HeaderButton) {
    use crate::timeline_hit::HeaderButton;
    let Some(track) = editor.editor.find_track(track_id).cloned() else { return };
    match button {
        HeaderButton::Visibility => {
            editor.editor.set_track_visible(track_id, !track.visible);
        }
        HeaderButton::Mute => {
            editor.editor.set_track_muted(track_id, !track.muted);
        }
    }
    editor.save = crate::editor_state::SaveState::Dirty;
}

fn select_in_rect(editor: &mut EditorState, x0: f32, y0: f32, x1: f32, y1: f32) {
    let (left, right) = (x0.min(x1), x0.max(x1));
    let (top, bottom) = (y0.min(y1), y0.max(y1));
    let t0 = editor.timeline.x_to_time(left);
    let t1 = editor.timeline.x_to_time(right);
    let doc = editor.doc();
    let mut hits = Vec::new();
    for (i, track) in doc.tracks.iter().enumerate() {
        let y = crate::timeline::track_top(doc, i);
        let h = crate::timeline::lane_height(track);
        if y + h < top || y > bottom {
            continue;
        }
        for clip in &track.clips {
            if clip.end_sec() >= t0 && clip.start_sec <= t1 {
                hits.push(clip.id.clone());
            }
        }
    }
    editor.editor.selection.clear();
    for id in hits {
        editor.editor.selection.add(fig_timeline::SelectionKey::new(fig_timeline::SelectionGroup::Clip, id));
    }
}

/// Event plumbing: Rune drag to gesture phases, used by the timeline canvas.
pub fn on_drag(app: &mut AppState, drag: &mut Drag, _tx: &mut Tx) {
    let (shift, alt) = (app.mods.shift, app.mods.alt);
    let editor = app.editor();
    match drag.phase {
        Phase::Down => pointer_down(editor, drag.x, drag.y, shift, alt),
        Phase::Move => pointer_move(editor, drag.x, drag.y, shift),
        Phase::Up => pointer_up(editor),
    }
}

/// The canvas x of a track header button (for hit testing).
pub fn header_button_x(kind: TrackKind, button: crate::timeline_hit::HeaderButton) -> f32 {
    use crate::timeline_hit::HeaderButton;
    match (kind, button) {
        (_, HeaderButton::Visibility) => 26.0,
        (_, HeaderButton::Mute) => 52.0,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::editor_state::EditorState;

    fn point_at_clip(editor: &EditorState, clip_index: usize) -> (f32, f32) {
        let doc = editor.doc();
        let clip = &doc.tracks[0].clips[clip_index];
        let x = crate::timeline::HEADER_W + (clip.start_sec as f32 + clip.dur_sec as f32 / 2.0) * editor.timeline.pps;
        let y = crate::timeline::track_top(doc, 0) + 40.0;
        (x, y)
    }

    #[test]
    fn dragging_a_clip_moves_it_and_is_one_undo_step() {
        let mut editor = EditorState::demo();
        let id = editor.doc().tracks[0].clips[0].id.clone();
        let before = editor.doc().tracks[0].clips[0].start_sec;
        let (x, y) = point_at_clip(&editor, 0);

        pointer_down(&mut editor, x, y, false, false);
        pointer_move(&mut editor, x + 100.0, y, false);
        pointer_up(&mut editor);

        let after = find_clip(&editor, &id).unwrap().start_sec;
        assert!(after > before, "the clip moved: {before} -> {after}");
        assert!(editor.editor.undo(), "the drag is one undo step");
        let restored = find_clip(&editor, &id).unwrap().start_sec;
        assert!((restored - before).abs() < 1e-9, "undo restores the start");
    }

    #[test]
    fn scrub_sets_the_playhead() {
        let mut editor = EditorState::demo();
        let x = HEADER_W + 250.0;
        pointer_down(&mut editor, x, 10.0, false, false);
        pointer_move(&mut editor, HEADER_W + 320.0, 10.0, false);
        pointer_up(&mut editor);
        assert!((editor.playhead - 3.2).abs() < 0.2, "playhead scrubbed: {}", editor.playhead);
    }

    #[test]
    fn marquee_selects_overlapping_clips() {
        let mut editor = EditorState::demo();
        pointer_down(&mut editor, HEADER_W + 10.0, 60.0, false, false);
        pointer_move(&mut editor, HEADER_W + 400.0, 130.0, false);
        pointer_up(&mut editor);
        assert!(!editor.editor.selection.is_empty(), "marquee selected clips");
    }
}
