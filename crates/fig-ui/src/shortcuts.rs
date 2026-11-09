//! Keyboard shortcuts: the rebindable catalog (`Services/Config/ShortcutCatalog.cs`).
//!
//! Same action ids, sections, labels and default gestures as the C# table, so the Preferences >
//! Keyboard page and the shortcut dialog show the same thing. The user's changes live in
//! `preferences.json` under `Keyboard.Shortcuts` (action id → gesture, "" meaning none).

use rune::{col, el, row, text, El};

use crate::services::{display_gesture, Preferences};
use crate::theme::{font, mono, Tokens, RADIUS_CONTROL};

pub struct Shortcut {
    pub id: &'static str,
    pub section: &'static str,
    pub label: &'static str,
    pub default_gesture: &'static str,
}

pub struct Shortcuts {
    pub rows: Vec<Shortcut>,
}

impl Shortcuts {
    pub fn new() -> Shortcuts {
        let rows: &[(&str, &str, &str, &str)] = &[
            ("project.save", "Project", "Save", "Ctrl+S"),
            ("project.close", "Project", "Close project", "Ctrl+W"),
            ("app.preferences", "Project", "Preferences", "Ctrl+OemComma"),
            ("edit.undo", "Editing", "Undo", "Ctrl+Z"),
            ("edit.redo", "Editing", "Redo", "Ctrl+Y"),
            ("edit.split", "Editing", "Split at playhead", "Ctrl+B"),
            ("edit.splitAll", "Editing", "Split every track at the playhead", "Ctrl+Shift+B"),
            ("edit.trimStart", "Editing", "Delete before the playhead", "Q"),
            ("edit.trimEnd", "Editing", "Delete after the playhead", "W"),
            ("edit.freeze", "Editing", "Freeze frame", ""),
            ("edit.group", "Editing", "Group clips (move and cut together)", "Ctrl+G"),
            ("edit.ungroup", "Editing", "Ungroup clips", "Ctrl+Shift+G"),
            ("edit.magnet", "Editing", "Main track magnet on or off", ""),
            ("tool.select", "Editing", "Select tool", "A"),
            ("tool.blade", "Editing", "Blade tool (click a clip to cut it)", "B"),
            ("edit.copy", "Editing", "Copy", "Ctrl+C"),
            ("edit.cut", "Editing", "Cut", "Ctrl+X"),
            ("edit.paste", "Editing", "Paste", "Ctrl+V"),
            ("edit.duplicate", "Editing", "Duplicate", "Ctrl+D"),
            ("play.loop", "Playback", "Loop playback", "Ctrl+L"),
            ("play.frameBack", "Playback", "Previous frame", "Left"),
            ("play.frameForward", "Playback", "Next frame", "Right"),
            ("play.secondBack", "Playback", "Back one second", "Shift+Left"),
            ("play.secondForward", "Playback", "Forward one second", "Shift+Right"),
            ("play.shuttleForward", "Playback", "Play forward (faster each press)", "L"),
            ("play.shuttleBack", "Playback", "Play backward (faster each press)", "J"),
            ("play.shuttleStop", "Playback", "Stop", "K"),
            ("play.audioScrub", "Playback", "Audio scrubbing on or off", "Shift+S"),
            ("play.previousEdit", "Playback", "Previous cut", "Up"),
            ("play.nextEdit", "Playback", "Next cut", "Down"),
            ("play.start", "Playback", "Go to the start", "Home"),
            ("play.end", "Playback", "Go to the end", "End"),
            ("view.mediaPanel", "View", "Show or hide the media panel", "Ctrl+D1"),
            ("view.inspector", "View", "Show or hide the inspector", "Ctrl+D2"),
            ("view.zoomIn", "View", "Zoom the timeline in", "Ctrl+OemPlus"),
            ("view.zoomOut", "View", "Zoom the timeline out", "Ctrl+OemMinus"),
            ("view.zoomFit", "View", "Zoom to fit the sequence", "Shift+Z"),
        ];
        Shortcuts {
            rows: rows
                .iter()
                .map(|(id, section, label, default_gesture)| Shortcut {
                    id,
                    section,
                    label,
                    default_gesture,
                })
                .collect(),
        }
    }

    /// The gesture the action has now: the user's choice if they made one, else the default.
    pub fn gesture_for(prefs: &Preferences, shortcut: &Shortcut) -> String {
        match prefs.keyboard.shortcuts.get(shortcut.id) {
            Some(chosen) if chosen.trim().is_empty() => "None".into(),
            Some(chosen) => display_gesture(chosen),
            None => display_gesture(shortcut.default_gesture),
        }
    }

    pub fn grouped(&self) -> Vec<(&'static str, Vec<&Shortcut>)> {
        let mut groups: Vec<(&'static str, Vec<&Shortcut>)> = Vec::new();
        for row in &self.rows {
            match groups.last_mut() {
                Some((section, items)) if *section == row.section => items.push(row),
                _ => groups.push((row.section, vec![row])),
            }
        }
        groups
    }
}

pub fn dialog(shortcuts: &Shortcuts, prefs: &Preferences, t: &Tokens) -> El {
    let mut body = col().w_full().gap(10.0);
    for (section, rows) in shortcuts.grouped() {
        let mut column = col()
            .w_full()
            .gap(2.0)
            .child(text(section).font(font(11.0, 600.0)).color(t.text_muted).padding(4.0));
        for row_data in rows {
            column = column.child(
                row()
                    .w_full()
                    .h(24.0)
                    .radius(RADIUS_CONTROL)
                    .padding(6.0)
                    .center_y()
                    .child(text(row_data.label).font(font(12.0, 400.0)).color(t.text_secondary))
                    .child(el().grow(1.0))
                    .child(text(Shortcuts::gesture_for(prefs, row_data)).font(mono(11.0, 400.0)).color(t.text_faint)),
            );
        }
        body = body.child(column);
    }
    body
}
