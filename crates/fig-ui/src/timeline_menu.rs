//! Timeline context menus (`TimelineView.ContextMenus.cs`, inventory §6.2): app-drawn menus for a
//! clip, track, the ruler or empty lane space. Rune's native popup menus are macOS/Windows only, so
//! the menu is drawn in the app's own layer, like the C# ones.

use fig_timeline::TrackKind;
use rune::{El, col, el, row, text};

use crate::app::AppState;
use crate::editor_state::EditorState;
use crate::theme::{font, Tokens, RADIUS_MENU};

#[derive(Clone, Debug, PartialEq)]
pub enum Target {
    Clip { clip_id: String, track_id: String },
    Track { track_id: String },
    Ruler,
    Marker { marker_id: String },
    Empty,
}

#[derive(Clone, Debug)]
pub struct ContextMenu {
    pub target: Target,
    /// Where to draw it, in window points.
    pub x: f32,
    pub y: f32,
}

/// Opens the context menu for the pointer at canvas-local `(local_x, local_y)`, drawn at the
/// window point `(window_x, window_y)`.
pub fn open(app: &mut AppState, local_x: f32, local_y: f32, window_x: f32, window_y: f32) {
    let editor = app.editor();
    let target = match crate::timeline_hit::hit(editor, local_x, local_y) {
        Some(crate::timeline_hit::HitTarget::Clip { clip_id, track_id, .. }) => Target::Clip { clip_id, track_id },
        Some(crate::timeline_hit::HitTarget::Lane { track_id }) | Some(crate::timeline_hit::HitTarget::Header { track_id, .. }) => {
            Target::Track { track_id }
        }
        Some(crate::timeline_hit::HitTarget::Ruler) => Target::Ruler,
        None => Target::Empty,
    };
    editor.context_menu = Some(ContextMenu { target, x: window_x, y: window_y });
}

pub fn overlay(state: &AppState, menu: ContextMenu, t: &Tokens) -> El {
    let items = items(&menu.target, state, t);
    col()
        .absolute(menu.x.min(900.0), menu.y.min(600.0))
        .w(230.0)
        .padding(6.0)
        .gap(1.0)
        .fill(crate::theme::hex("#23252b"))
        .radius(RADIUS_MENU)
        .border(1.0, crate::theme::hex("#2f323a"))
        .children(items)
}

fn items(target: &Target, state: &AppState, t: &Tokens) -> Vec<El> {
    let mut out = Vec::new();
    match target {
        Target::Clip { clip_id, track_id } => {
            let clip = clip_id.clone();
            let track = track_id.clone();
            out.push(item("Cut", "Ctrl+X", t, move |s: &mut AppState| {
                s.editor().editor.cut_selected();
            }));
            out.push(item("Copy", "Ctrl+C", t, |s: &mut AppState| {
                s.editor().editor.copy_selected();
            }));
            out.push(item("Paste", "Ctrl+V", t, |s: &mut AppState| {
                let editor = s.editor();
                let at = editor.playhead;
                editor.editor.paste(at, None);
            }));
            out.push(item("Duplicate", "Ctrl+D", t, |s: &mut AppState| {
                s.editor().editor.duplicate_selected();
            }));
            out.push(item("Split at playhead", "Ctrl+B", t, |s: &mut AppState| {
                let editor = s.editor();
                let at = editor.playhead;
                editor.editor.split_at_playhead(at);
            }));
            out.push(separator(t));
            out.push(item("Ripple delete", "Shift+Del", t, move |s: &mut AppState| {
                let editor = s.editor();
                editor.editor.ripple_delete(&clip);
                let _ = &track;
            }));
            out.push(item("Lift (leave gap)", "Del", t, |s: &mut AppState| {
                s.editor().editor.lift_selected();
            }));
            out.push(item("Enable / disable", "Ctrl+E", t, |s: &mut AppState| {
                s.editor().editor.toggle_enabled_selected();
            }));
            out.push(separator(t));
            out.push(item("Add effect…", "", t, |s: &mut AppState| {
                s.toasts.show("Add effect");
            }));
            out.push(item("Properties…", "", t, |s: &mut AppState| {
                s.editor().inspector.tab = 0;
            }));
        }
        Target::Track { track_id } => {
            let id = track_id.clone();
            let kind = state
                .editor
                .as_ref()
                .and_then(|editor| editor.editor.find_track(&id).map(|track| (track.visible, track.muted, track.kind)));
            out.push(item("Add video track", "", t, |s: &mut AppState| {
                s.editor().editor.add_track(TrackKind::Video);
            }));
            out.push(item("Add audio track", "", t, |s: &mut AppState| {
                s.editor().editor.add_track(TrackKind::Audio);
            }));
            if let Some((visible, muted, _)) = kind {
                let hide_id = id.clone();
                out.push(item(
                    if visible { "Hide track" } else { "Show track" },
                    "",
                    t,
                    move |s: &mut AppState| {
                        s.editor().editor.set_track_visible(&hide_id, !visible);
                    },
                ));
                let mute_id = id.clone();
                out.push(item(
                    if muted { "Unmute track" } else { "Mute track" },
                    "",
                    t,
                    move |s: &mut AppState| {
                        s.editor().editor.set_track_muted(&mute_id, !muted);
                    },
                ));
            }
            out.push(separator(t));
            out.push(item("Delete track", "", t, move |s: &mut AppState| {
                s.editor().editor.remove_track(&id);
            }));
        }
        Target::Ruler => {
            out.push(item("Add marker at playhead", "M", t, |s: &mut AppState| {
                let editor = s.editor();
                let at = editor.playhead;
                editor.editor.add_timeline_marker(at, "Marker", "#ffd60a");
            }));
            out.push(item("Add range at playhead", "Shift+M", t, |s: &mut AppState| {
                let editor = s.editor();
                let at = editor.playhead;
                editor.editor.add_range_marker(at, 2.0, "Range", "#ffd60a");
            }));
            out.push(separator(t));
            out.push(item("Zoom to fit", "Shift+Z", t, |s: &mut AppState| {
                let duration = s.editor().duration_sec();
                s.editor().timeline.fit(duration, 1000.0);
            }));
        }
        Target::Marker { .. } => {
            out.push(item("Delete marker", "Shift+Del", t, |s: &mut AppState| {
                s.toasts.show("Delete marker");
            }));
            out.push(item("Rename marker…", "", t, |s: &mut AppState| {
                s.toasts.show("Rename marker");
            }));
        }
        Target::Empty => {
            out.push(item("Add marker at playhead", "M", t, |s: &mut AppState| {
                let editor = s.editor();
                let at = editor.playhead;
                editor.editor.add_timeline_marker(at, "Marker", "#ffd60a");
            }));
            out.push(item("Paste", "Ctrl+V", t, |s: &mut AppState| {
                let editor = s.editor();
                let at = editor.playhead;
                editor.editor.paste(at, None);
            }));
        }
    }
    out
}

fn item(label: &str, shortcut: &str, t: &Tokens, action: impl Fn(&mut AppState) + 'static) -> El {
    row()
        .w_full()
        .h(30.0)
        .radius(8.0)
        .padding(10.0)
        .center_y()
        .child(text(label).font(font(13.0, 400.0)).color(t.text_primary))
        .child(el().grow(1.0))
        .child(text(shortcut).font(font(11.0, 400.0)).color(t.text_faint))
        .on_press(move |s: &mut AppState| {
            action(s);
            if let Some(editor) = s.editor.as_mut() {
                editor.context_menu = None;
            }
        })
}

fn separator(t: &Tokens) -> El {
    el().w_full().h(1.0).fill(t.border)
}

/// Marks the menu closed when the app changes screen; used by the keyboard's Escape path.
pub fn close(editor: &mut EditorState) {
    editor.context_menu = None;
}
