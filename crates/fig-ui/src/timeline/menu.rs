//! Right-click menus on the timeline: what is under the pointer picks the menu (a marker, a transition, a track
//! header, the ruler, a clip, or empty space). Labels follow `TimelineView.ContextMenus.cs`.

use fig_timeline::TrackKind;

use super::geometry::*;
use super::input::hit;
use crate::app::AppState;
use crate::commands::Command;
use crate::ui::menu::{Item, Popup};

pub fn open(app: &mut AppState, x: f32, y: f32, wx: f32, wy: f32) {
    let Some(e) = app.editor.as_mut() else { return };
    let target = hit(e, x, y);
    let t = e.tl.t_of(x).max(0.0);
    let items = match target {
        Hit::Marker { id, .. } => {
            if !e.ed.selection.is_marker_selected(&id) {
                e.ed.selection.select_marker(&id);
            }
            marker_menu(app, &id)
        }
        Hit::Transition { key, .. } => {
            e.ed.selection.select_transition(&key);
            transition_menu(&key)
        }
        Hit::Header { track, .. } | Hit::Empty { track: Some(track), .. } if x < HEADER_W => track_menu(app, track),
        Hit::Ruler => ruler_menu(app, t),
        Hit::Keyframe { host, time, .. } => keyframe_menu(app, host, time),
        Hit::Rack { owner, item, .. } => {
            let Some(fx) = item.effect else { return };
            // right-clicking a bar that is part of a selection keeps it (to copy them all)
            if !e.ed.selection.is_effect_selected(&fx) {
                e.ed.selection.select_effect(&owner, &fx);
            }
            rack_menu(app, &owner, &fx, t)
        }
        Hit::Clip { clip, .. } => {
            if !e.ed.selection.is_selected(&clip) {
                let group = e.ed.link_group(&clip);
                e.ed.selection.set_clips(group);
            }
            clip_menu(app, &clip, t)
        }
        Hit::Empty { track, time } => empty_menu(app, track, time),
        _ => return,
    };
    crate::ui::menu::open(app, Popup::at(wx, wy, items));
}

fn clip_menu(app: &AppState, clip_id: &str, t: f64) -> Vec<Item> {
    let e = app.editor.as_ref().unwrap();
    let n = e.ed.selection.clip_count().max(1);
    let noun = if n > 1 { format!("{n} clips") } else { "clip".to_string() };
    let clip = e.ed.find_clip(clip_id).cloned();
    let enabled = clip.as_ref().is_some_and(|c| c.enabled);
    let id = clip_id.to_string();
    let mut items = vec![
        Item::new("Split here", {
            let id = id.clone();
            move |s, _| {
                if let Some(e) = s.editor.as_mut() {
                    let at = e.ed.snap_time(t);
                    e.ed.edit(|ed| ed.cut(&id, at));
                }
            }
        }),
        Item::command(app, "Split at playhead", Command::Split),
        Item::Separator,
        Item::command(app, format!("Cut {noun}"), Command::Cut),
        Item::command(app, format!("Copy {noun}"), Command::Copy),
        Item::command(app, "Paste at playhead", Command::Paste),
        Item::command(app, format!("Duplicate {noun}"), Command::Duplicate),
        Item::Separator,
        Item::command(app, format!("Delete {noun}"), Command::Delete),
        Item::command(app, format!("Ripple delete {noun}"), Command::RippleDelete),
        Item::command(app, if enabled { "Disable clip" } else { "Enable clip" }, Command::ToggleEnabled),
        Item::Separator,
    ];
    if e.ed.can_have_speed_curve(clip_id) {
        let has = clip.as_ref().is_some_and(fig_timeline::clip_time::has_curve);
        let id = id.clone();
        items.push(Item::new(if has { "Remove speed curve" } else { "Add speed curve" }, move |s, _| {
            if let Some(e) = s.editor.as_mut() {
                if has {
                    e.ed.remove_speed_curve(&id);
                } else {
                    e.ed.add_speed_curve(&id);
                }
            }
        }));
    }
    if clip.as_ref().is_some_and(|c| c.as_video().is_some()) {
        items.push(Item::command(app, "Freeze frame at playhead", Command::Freeze));
    }
    items.push(Item::command(app, "Reverse", Command::Reverse));
    if e.ed.can_detach_audio(std::slice::from_ref(&id)) {
        items.push(Item::command(app, "Detach audio", Command::DetachAudio));
    }
    if clip.as_ref().is_some_and(|c| c.link_group_id.is_some()) {
        items.push(Item::command(app, "Unlink from its audio / video", Command::Ungroup));
    } else if e.ed.selection.clip_count() > 1 {
        items.push(Item::command(app, "Link selected clips", Command::Group));
    }
    items.push(Item::Separator);
    let track_id = e.ed.find_clip_track_id(clip_id);
    items.push(Item::new("Select all on this track", move |s, _| {
        if let (Some(e), Some(tid)) = (s.editor.as_mut(), track_id.as_ref()) {
            let ids: Vec<String> = e.ed.find_track(tid).map(|t| t.clips.iter().map(|c| c.id.clone()).collect()).unwrap_or_default();
            e.ed.selection.set_clips(ids);
        }
    }));
    {
        let id = id.clone();
        items.push(Item::new("Add marker here", move |s, _| {
            if let Some(e) = s.editor.as_mut() {
                let start = e.ed.find_clip(&id).map_or(0.0, |c| c.start_sec);
                let local = e.ed.snap_time(t) - start;
                e.ed.add_clip_marker(&id, local.max(0.0), "Marker", "#ffd60a");
            }
        }));
    }
    if clip.as_ref().is_some_and(|c| !c.markers.is_empty()) {
        let (a, b) = (id.clone(), id.clone());
        items.push(Item::new("Clear generated beats on this clip", move |s, _| {
            if let Some(e) = s.editor.as_mut() {
                e.ed.clear_clip_markers(&a, true);
            }
        }));
        items.push(Item::new("Clear all markers on this clip", move |s, _| {
            if let Some(e) = s.editor.as_mut() {
                e.ed.clear_clip_markers(&b, false);
            }
        }));
    }
    items.push(Item::Separator);
    items.push(Item::new("Show properties", |s, _| {
        if let Some(e) = s.editor.as_mut() {
            e.panels.right_collapsed = false;
        }
    }));
    items
}

fn track_menu(app: &AppState, index: usize) -> Vec<Item> {
    let e = app.editor.as_ref().unwrap();
    let track = e.doc().tracks[index].clone();
    let id = track.id.clone();
    let count = e.doc().tracks.len();
    let mut items = Vec::new();
    {
        let id = id.clone();
        items.push(Item::new("Rename track…", move |s, _| {
            let current = s.editor.as_ref().and_then(|e| e.ed.find_track(&id)).and_then(|t| t.name.clone()).unwrap_or_default();
            let id = id.clone();
            crate::dialogs::prompt(s, "Rename track", "Track name", &current, "Rename", move |s, name| {
                if let Some(e) = s.editor.as_mut() {
                    let name = name.trim();
                    e.ed.edit(|ed| ed.rename_track(&id, (!name.is_empty()).then_some(name)));
                }
            });
        }));
    }
    items.push(Item::Separator);
    if track.kind == TrackKind::Video {
        let id = id.clone();
        let visible = track.visible;
        items.push(Item::new(if visible { "Hide track" } else { "Show track" }, move |s, _| {
            if let Some(e) = s.editor.as_mut() {
                e.ed.edit(|ed| ed.set_track_visible(&id, !visible));
            }
        }));
    }
    {
        let id = id.clone();
        let muted = track.muted;
        items.push(Item::new(if muted { "Unmute track" } else { "Mute track" }, move |s, _| {
            if let Some(e) = s.editor.as_mut() {
                e.ed.edit(|ed| ed.set_track_muted(&id, !muted));
            }
        }));
    }
    {
        let id = id.clone();
        items.push(Item::new("Select all clips on this track", move |s, _| {
            if let Some(e) = s.editor.as_mut() {
                let ids: Vec<String> = e.ed.find_track(&id).map(|t| t.clips.iter().map(|c| c.id.clone()).collect()).unwrap_or_default();
                e.ed.selection.set_clips(ids);
            }
        }));
    }
    items.push(Item::Separator);
    {
        let id = id.clone();
        items.push(
            Item::new("Move track up", move |s, _| {
                if let Some(e) = s.editor.as_mut() {
                    e.ed.edit(|ed| ed.move_track(&id, index.saturating_sub(1)));
                }
            })
            .enabled(index > 0),
        );
    }
    {
        let id = id.clone();
        items.push(
            Item::new("Move track down", move |s, _| {
                if let Some(e) = s.editor.as_mut() {
                    // before the track after the next one: move_track's index is where it's inserted
                    e.ed.edit(|ed| ed.move_track(&id, index + 2));
                }
            })
            .enabled(index + 1 < count),
        );
    }
    items.push(Item::Separator);
    items.push(Item::new("Add video track", |s, _| add_track(s, TrackKind::Video)));
    items.push(Item::new("Add audio track", |s, _| add_track(s, TrackKind::Audio)));
    items.push(Item::Separator);
    let label = if track.clips.is_empty() { "Delete track".to_string() } else { format!("Delete track and its {} clip(s)", track.clips.len()) };
    items.push(
        Item::new(label, move |s, _| {
            if let Some(e) = s.editor.as_mut() {
                e.ed.edit(|ed| ed.remove_track(&id));
            }
        })
        .danger(),
    );
    items
}

fn add_track(s: &mut AppState, kind: TrackKind) {
    if let Some(e) = s.editor.as_mut() {
        e.ed.edit(|ed| ed.add_track(kind));
    }
}

fn ruler_menu(app: &AppState, t: f64) -> Vec<Item> {
    let e = app.editor.as_ref().unwrap();
    let points = e.doc().markers.iter().filter(|m| !m.is_range()).count() + 1;
    let ranges = e.doc().markers.iter().filter(|m| m.is_range()).count() + 1;
    vec![
        Item::new("Add marker here", move |s, _| {
            if let Some(e) = s.editor.as_mut() {
                let at = e.ed.snap_time(t);
                e.ed.add_timeline_marker(at, &format!("Marker {points}"), "#eab308");
            }
        }),
        Item::new("Add range here", move |s, _| {
            if let Some(e) = s.editor.as_mut() {
                let at = e.ed.snap_time(t);
                e.ed.add_range_marker(at, 2.0, &format!("Range {ranges}"), "#5f9cf8");
            }
        }),
        Item::Separator,
        Item::new("Move playhead here", move |s, _| crate::editor::seek(s, t, false)),
        Item::command(app, "Go to start", Command::GoStart),
        Item::command(app, "Go to end", Command::GoEnd),
        Item::command(app, "Zoom to fit sequence", Command::ZoomFit),
    ]
}

fn empty_menu(app: &AppState, track: Option<usize>, t: f64) -> Vec<Item> {
    let e = app.editor.as_ref().unwrap();
    let mut items = Vec::new();
    if let Some(ti) = track
        && let Some(tr) = e.doc().tracks.get(ti)
        && e.ed.gap_at(&tr.id, t).is_some()
    {
        let id = tr.id.clone();
        items.push(Item::new("Delete this gap", move |s, _| {
            if let Some(e) = s.editor.as_mut() {
                e.ed.close_gap(&id, t);
                e.tl.gap = None;
            }
        }));
        items.push(Item::Separator);
    }
    let track_id = track.and_then(|i| e.doc().tracks.get(i)).map(|t| t.id.clone());
    let points = e.doc().markers.iter().filter(|m| !m.is_range()).count() + 1;
    items.extend([
        Item::new("Move playhead here", move |s, _| crate::editor::seek(s, t, false)),
        Item::new("Paste here", move |s, _| {
            if let Some(e) = s.editor.as_mut() {
                let at = e.ed.snap_time(t);
                e.ed.paste(at, track_id.as_deref());
            }
        })
        .enabled(e.ed.has_clipboard()),
        Item::new("Add marker here", move |s, _| {
            if let Some(e) = s.editor.as_mut() {
                let at = e.ed.snap_time(t);
                e.ed.add_timeline_marker(at, &format!("Marker {points}"), "#eab308");
            }
        }),
        Item::Separator,
        Item::command(app, "Select all", Command::SelectAll),
        Item::command(app, "Zoom to fit sequence", Command::ZoomFit),
        Item::Separator,
        Item::new("Add video track", |s, _| add_track(s, TrackKind::Video)),
        Item::new("Add audio track", |s, _| add_track(s, TrackKind::Audio)),
    ]);
    items
}

fn transition_menu(key: &str) -> Vec<Item> {
    let (a, b) = (key.to_string(), key.to_string());
    vec![
        Item::new("Preview transition", move |s, _| {
            if let Some(cut) = s.editor.as_ref().and_then(|e| fig_edit::transitions::find_cut(e.doc(), &a)) {
                let half = cut.duration_sec / 2.0;
                crate::editor::seek(s, (cut.cut_sec - half - 0.5).max(0.0), false);
                crate::editor::play(s, 1.0);
            }
        }),
        Item::new("Replace…", |s, _| {
            if let Some(e) = s.editor.as_mut() {
                e.panels.left_collapsed = false;
                e.library.tab = crate::editor::library::Tab::Transitions;
            }
        }),
        Item::Separator,
        Item::new("Remove transition", move |s, _| {
            if let Some(e) = s.editor.as_mut()
                && let Some(cut) = fig_edit::transitions::find_cut(e.doc(), &b)
            {
                e.ed.remove_transition(&cut.left_clip_id, &cut.right_clip_id);
            }
        })
        .danger(),
    ]
}

fn marker_menu(app: &AppState, id: &str) -> Vec<Item> {
    let e = app.editor.as_ref().unwrap();
    let n = e.ed.selection.selected_marker_ids().len().max(1);
    let (a, b, c, d) = (id.to_string(), id.to_string(), id.to_string(), id.to_string());
    let is_point = fig_edit::markers::find_marker(e.doc(), id).is_some_and(|l| !l.marker.is_range());
    let mut items = vec![
        Item::new("Jump to marker", move |s, _| {
            if let Some(at) = s.editor.as_ref().and_then(|e| fig_edit::markers::find_marker(e.doc(), &a)).map(|l| l.timeline_sec()) {
                crate::editor::seek(s, at, false);
            }
        }),
        Item::new("Edit marker…", move |s, _| {
            if let Some(e) = s.editor.as_mut() {
                e.ed.selection.select_marker(&b);
                e.panels.right_collapsed = false;
            }
        }),
        Item::new("Split video at selected markers", |s, _| {
            if let Some(e) = s.editor.as_mut() {
                let n = e.ed.split_video_at_selected_markers();
                e.say(format!("{n} cut(s) made"));
            }
        }),
    ];
    if is_point {
        items.push(Item::new("Use as range start", move |s, _| {
            if let Some(e) = s.editor.as_mut() {
                e.ed.add_range_from_bookmark(&c, true);
            }
        }));
        items.push(Item::new("Use as range end", move |s, _| {
            if let Some(e) = s.editor.as_mut() {
                e.ed.add_range_from_bookmark(&d, false);
            }
        }));
    }
    items.push(Item::Separator);
    items.push(
        Item::new(format!("Delete selected markers ({n})"), |s, _| {
            if let Some(e) = s.editor.as_mut() {
                let ids = e.ed.selection.selected_marker_ids();
                e.ed.delete_markers(&ids);
            }
        })
        .danger(),
    );
    items
}

pub const EASINGS: [(fig_timeline::KeyframeEasing, &str); 5] = [
    (fig_timeline::KeyframeEasing::Linear, "Linear"),
    (fig_timeline::KeyframeEasing::EaseIn, "Ease in"),
    (fig_timeline::KeyframeEasing::EaseOut, "Ease out"),
    (fig_timeline::KeyframeEasing::EaseInOut, "Ease in & out"),
    (fig_timeline::KeyframeEasing::Hold, "Hold"),
];

/// The one menu for a keyframe, of any kind (`KeyframeMenu.cs`): jump to it, duplicate it to the playhead, choose how
/// it moves on to the next one, delete it.
pub fn keyframe_menu(app: &AppState, host: fig_edit::KeyframeHostRef, time: f64) -> Vec<Item> {
    let e = app.editor.as_ref().unwrap();
    let found = e.ed.find_keyframe_host(&host);
    let origin = found.as_ref().map_or(0.0, |h| h.origin_sec);
    let current = found.as_ref().and_then(|h| h.easing_at(e.doc(), time));
    let k = fig_edit::KeyframeRef { host, time_sec: time };
    let easing = EASINGS
        .iter()
        .map(|&(easing, name)| {
            let k = k.clone();
            Item::new(name, move |s, _| {
                if let Some(e) = s.editor.as_mut() {
                    e.ed.set_keyframe_easing(&k, easing);
                }
            })
            .checked(current == Some(easing))
        })
        .collect();
    let (k1, k2) = (k.clone(), k.clone());
    vec![
        Item::new("Jump to keyframe", move |s, _| crate::editor::seek(s, origin + time, false)),
        Item::new("Duplicate to the playhead", move |s, _| {
            if let Some(e) = s.editor.as_mut() {
                let to = e.playhead - origin;
                e.ed.duplicate_keyframes(&k1, to);
            }
        }),
        Item::submenu("Movement to the next keyframe", easing),
        Item::Separator,
        Item::new("Delete keyframe", move |s, _| {
            if let Some(e) = s.editor.as_mut() {
                e.ed.remove_keyframes(&k2);
                e.ed.selection.clear_keyframe();
            }
        })
        .danger(),
    ]
}

/// The menu for an effect bar (`TimelineView.Racks.cs` `TryOpenRackMenu`).
fn rack_menu(app: &AppState, owner: &str, fx: &str, t: f64) -> Vec<Item> {
    let e = app.editor.as_ref().unwrap();
    let Some(effect) = e.ed.find_effect(owner, fx).cloned() else { return Vec::new() };
    let on_track = e.doc().find_track(owner).is_some();
    let origin = e.ed.find_clip(owner).map_or(0.0, |c| c.start_sec);
    let span = e.ed.find_clip(owner).map_or(f64::INFINITY, |c| c.dur_sec);
    let count = e.ed.selection.selected_effects().len().max(1);
    let (o, f) = (owner.to_string(), fx.to_string());
    let with = move |act: fn(&mut crate::editor::Editor, &str, &str, f64)| {
        let (o, f) = (o.clone(), f.clone());
        move |s: &mut AppState, _: &mut rune::Tx| {
            if let Some(e) = s.editor.as_mut() {
                let at = e.playhead;
                act(e, &o, &f, at);
            }
        }
    };
    let mut items = vec![Item::new("Add keyframe here", {
        let (o, f) = (owner.to_string(), fx.to_string());
        move |s, _| {
            if let Some(e) = s.editor.as_mut() {
                e.ed.add_effect_keyframe(&o, &f, (t - origin).max(0.0));
            }
        }
    })];
    if !effect.keyframes.is_empty() {
        items.push(Item::new(
            "Clear all keyframes (keep the look at the playhead)",
            with(|e, o, f, at| {
                let origin = e.ed.find_clip(o).map_or(0.0, |c| c.start_sec);
                e.ed.set_effect_animated(o, f, false, (at - origin).max(0.0));
                e.ed.selection.clear_keyframe();
            }),
        ));
    }
    items.extend([
        Item::new(if count == 1 { "Copy effect".to_string() } else { format!("Copy {count} effects") }, |s, _| {
            if let Some(e) = s.editor.as_mut() {
                let n = e.ed.copy_selected_effects();
                e.say(if n == 1 { "Copied effect · paste puts it at the playhead".to_string() } else { format!("Copied {n} effects · paste puts them at the playhead") });
            }
        }),
        Item::new("Paste effects at playhead", {
            let o = owner.to_string();
            move |s, _| {
                if let Some(e) = s.editor.as_mut() {
                    let at = e.playhead;
                    let track = if e.doc().find_track(&o).is_some() { Some(o.clone()) } else { e.ed.find_clip_track_id(&o) };
                    e.ed.paste_effects(at, track.as_deref());
                }
            }
        })
        .enabled(e.ed.has_effect_clipboard()),
        Item::Separator,
        Item::new("Save as preset...", {
            let (o, f) = (owner.to_string(), fx.to_string());
            move |s, _| crate::editor::library::save_effect_preset(s, &o, &f)
        }),
        Item::Separator,
        Item::new(if effect.enabled { "Turn effect off" } else { "Turn effect on" }, with(|e, o, f, _| e.ed.toggle_effect(o, f))),
    ]);
    if effect.start_sec.is_some() && effect.dur_sec.is_some() {
        items.push(Item::new(
            if on_track { "Apply to the whole track" } else { "Apply to the whole clip" },
            with(|e, o, f, _| e.ed.set_effect_range(o, f, None, false)),
        ));
    } else {
        let (o, f) = (owner.to_string(), fx.to_string());
        items.push(Item::new(if on_track { "Limit to a timeline region" } else { "Limit to part on the timeline" }, move |s, _| {
            if let Some(e) = s.editor.as_mut() {
                let from = (e.playhead - origin).clamp(0.0, (span - 0.5).max(0.0));
                e.ed.set_effect_range(&o, &f, Some((from, 3f64.min(span - from))), false);
            }
        }));
    }
    items.extend([Item::Separator, Item::new("Remove effect", with(|e, o, f, _| e.ed.remove_effect(o, f))).danger()]);
    items
}
