//! Keyboard dispatch: the rebindable shortcut table and the transport keys.
//!
//! Rune calls [`handle`] for keys the focused control did not take, or for every key while nothing
//! has focus, so typing in a text field never triggers these. The table mirrors
//! `ShortcutCatalog.cs` (inventory §6.3); rebinding through Preferences and the full command table
//! land in P5.

use rune::{Key, KeyEvent};

use crate::services::{KeyCombo, SpecialKey};

use crate::app::AppState;

pub fn handle(state: &mut AppState, event: &KeyEvent) -> bool {
    let key = event.key.clone();
    let mods = event.mods;

    // On Home (no project open) only the app-level shortcuts apply; editing keys would be dead.
    if !state.has_editor() {
        if key == Key::Escape {
            if state.dialogs.is_open() {
                state.dialogs.close();
                return true;
            }
            if state.preferences_open {
                state.preferences_open = false;
                return true;
            }
            if state.menu.is_some() {
                state.menu = None;
                return true;
            }
            return false;
        }
        if mods.cmd {
            return match key {
                Key::Command('n') => {
                    state.new_project();
                    true
                }
                Key::Command(',') => {
                    state.preferences_open = true;
                    state.preferences_page = 0;
                    true
                }
                Key::Command('o') => {
                    state.toasts.show("Open a project from the list, or import one");
                    true
                }
                _ => false,
            };
        }
        return false;
    }

    // overlays take Escape first; while one is open, the other shortcuts stay out of its way
    if !state.preferences_open && !state.dialogs.is_open() && state.menu.is_none() {
        if let Some(handled) = transport_key(state, &key, mods.shift) {
            return handled;
        }
    }

    if key == Key::Escape {
        if state.editor.as_ref().is_some_and(|e| !matches!(e.timeline.gesture, crate::timeline_gesture::Gesture::Idle)) {
            let editor = state.editor();
            crate::timeline_gesture::cancel(editor);
            return true;
        }
        if state.editor.as_ref().is_some_and(|e| e.context_menu.is_some()) {
            let editor = state.editor();
            crate::timeline_menu::close(editor);
            return true;
        }
        if state.dialogs.is_open() {
            state.dialogs.close();
        } else if state.preferences_open {
            state.preferences_open = false;
        } else if state.menu.is_some() {
            state.menu = None;
        } else if state.editor.as_ref().is_some_and(|e| e.graph_open) {
            state.editor().graph_open = false;
        } else {
            state.editor().transport.stop();
            state.editor().editor.selection.clear();
        }
        return true;
    }

    // the user's changed shortcuts (preferences.json → Keyboard.Shortcuts) win over the defaults
    if !state.shortcut_overrides.is_empty() {
        let hit = state
            .shortcut_overrides
            .iter()
            .find(|(_, combo)| combo_matches(combo, event))
            .map(|(id, _)| id.clone());
        if let Some(id) = hit {
            return execute_action(state, &id);
        }
    }

    // a family of Command shortcuts (⌘ on macOS, Ctrl elsewhere)
    if mods.cmd {
        return match key {
            Key::Command('z') if !mods.shift => {
                state.editor().editor.undo();
                true
            }
            Key::Command('y') | Key::Command('z') if mods.shift => {
                state.editor().editor.redo();
                true
            }
            Key::Command('b') => {
                let editor = state.editor();
                if !editor.split_at_playhead().is_empty() {
                    editor.save = crate::editor_state::SaveState::Dirty;
                }
                true
            }
            Key::Command('s') => {
                state.save_project();
                true
            }
            Key::Command('c') => {
                state.editor().editor.copy_selected();
                true
            }
            Key::Command('x') => {
                let editor = state.editor();
                editor.editor.cut_selected();
                editor.save = crate::editor_state::SaveState::Dirty;
                true
            }
            Key::Command('v') => {
                let editor = state.editor();
                let at = editor.playhead;
                let made = editor.editor.paste(at, None);
                if !made.is_empty() {
                    editor.save = crate::editor_state::SaveState::Dirty;
                }
                true
            }
            Key::Command('d') => {
                let editor = state.editor();
                if !editor.editor.duplicate_selected().is_empty() {
                    editor.save = crate::editor_state::SaveState::Dirty;
                }
                true
            }
            Key::Command('a') => {
                state.editor().editor.select_all();
                true
            }
            Key::Command(',') => {
                state.preferences_open = true;
                state.preferences_page = 0;
                true
            }
            Key::Command('1') => {
                let editor = state.editor();
                editor.left_collapsed = !editor.left_collapsed;
                state.save_workspace();
                true
            }
            Key::Command('2') => {
                let editor = state.editor();
                editor.right_collapsed = !editor.right_collapsed;
                state.save_workspace();
                true
            }
            Key::Command('w') => {
                state.close_project();
                true
            }
            Key::Command('n') => {
                state.new_project();
                true
            }
            Key::Command('o') => {
                state.toasts.show("Open a project from Home, or import one");
                true
            }
            Key::Command('+') | Key::Command('=') => {
                state.editor().timeline.zoom(1.25);
                true
            }
            Key::Command('-') => {
                state.editor().timeline.zoom(0.8);
                true
            }
            _ => false,
        };
    }

    match key {
        Key::Delete | Key::Backspace => {
            let editor = state.editor();
            editor.editor.lift_selected();
            editor.save = crate::editor_state::SaveState::Dirty;
            true
        }
        Key::Command(_) => false,
        Key::Text(text) if text == " " => {
            state.editor().transport.toggle_play();
            true
        }
        Key::Text(text) if text.eq_ignore_ascii_case("m") => {
            let editor = state.editor();
            let at = editor.playhead;
            editor.editor.add_timeline_marker(at, "Marker", "#ffd60a");
            editor.save = crate::editor_state::SaveState::Dirty;
            true
        }
        Key::Text(text) if text.eq_ignore_ascii_case("a") => {
            state.editor().tool = crate::editor_state::Tool::Select;
            true
        }
        Key::Text(text) if text.eq_ignore_ascii_case("b") => {
            state.editor().tool = crate::editor_state::Tool::Blade;
            true
        }
        Key::Text(text) if text.eq_ignore_ascii_case("l") => {
            state.editor().transport.shuttle_towards(true);
            true
        }
        Key::Text(text) if text.eq_ignore_ascii_case("j") => {
            state.editor().transport.shuttle_towards(false);
            true
        }
        Key::Text(text) if text.eq_ignore_ascii_case("k") => {
            state.editor().transport.stop();
            true
        }
        Key::Escape => true,
        _ => false,
    }
}

fn transport_key(state: &mut AppState, key: &Key, shift: bool) -> Option<bool> {
    let editor = state.editor();
    let rate = editor.project_rate().max(1.0);
    let frame = 1.0 / rate;
    match key {
        Key::Left => {
            let step = if shift { 1.0 } else { frame };
            editor.playhead = (editor.playhead - step).max(0.0);
            Some(true)
        }
        Key::Right => {
            let step = if shift { 1.0 } else { frame };
            let duration = editor.duration_sec();
            editor.playhead = (editor.playhead + step).min(duration);
            Some(true)
        }
        Key::Home => {
            editor.playhead = 0.0;
            Some(true)
        }
        Key::End => {
            let duration = editor.duration_sec();
            editor.playhead = duration;
            Some(true)
        }
        Key::Up => {
            editor.playhead = previous_cut(editor, editor.playhead);
            Some(true)
        }
        Key::Down => {
            editor.playhead = next_cut(editor, editor.playhead);
            Some(true)
        }
        _ => None,
    }
}

fn previous_cut(editor: &crate::editor_state::EditorState, at: f64) -> f64 {
    let mut times: Vec<f64> = editor
        .doc()
        .tracks
        .iter()
        .flat_map(|t| t.clips.iter())
        .flat_map(|c| [c.start_sec, c.end_sec()])
        .chain(editor.doc().markers.iter().map(|m| m.start_sec))
        .filter(|t| *t < at - 1e-6)
        .collect();
    times.sort_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal));
    times.last().copied().unwrap_or(0.0)
}

fn next_cut(editor: &crate::editor_state::EditorState, at: f64) -> f64 {
    let mut times: Vec<f64> = editor
        .doc()
        .tracks
        .iter()
        .flat_map(|t| t.clips.iter())
        .flat_map(|c| [c.start_sec, c.end_sec()])
        .chain(editor.doc().markers.iter().map(|m| m.start_sec))
        .filter(|t| *t > at + 1e-6)
        .collect();
    times.sort_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal));
    times.first().copied().unwrap_or_else(|| editor.duration_sec())
}

/// True when `combo` (parsed from `preferences.json`) is the key event.
fn combo_matches(combo: &KeyCombo, event: &KeyEvent) -> bool {
    let ctrl = event.mods.cmd || event.mods.ctrl;
    if combo.ctrl != ctrl || combo.shift != event.mods.shift || combo.alt != event.mods.alt {
        return false;
    }
    let (key, special) = event_key(event);
    match (combo.special, special) {
        (Some(wanted), Some(actual)) => wanted == actual,
        (None, _) => key.is_some_and(|c| c == combo.key),
        _ => false,
    }
}

fn event_key(event: &KeyEvent) -> (Option<char>, Option<SpecialKey>) {
    match &event.key {
        Key::Command(c) => (Some(*c), None),
        Key::Text(text) => {
            if text == " " {
                (None, Some(SpecialKey::Space))
            } else if text.chars().count() == 1 {
                (text.chars().next().map(|c| c.to_ascii_lowercase()), None)
            } else {
                (None, None)
            }
        }
        Key::Left => (None, Some(SpecialKey::Left)),
        Key::Right => (None, Some(SpecialKey::Right)),
        Key::Up => (None, Some(SpecialKey::Up)),
        Key::Down => (None, Some(SpecialKey::Down)),
        Key::Home => (None, Some(SpecialKey::Home)),
        Key::End => (None, Some(SpecialKey::End)),
        Key::PageUp => (None, Some(SpecialKey::PageUp)),
        Key::PageDown => (None, Some(SpecialKey::PageDown)),
        Key::Delete => (None, Some(SpecialKey::Delete)),
        Key::Backspace => (None, Some(SpecialKey::Backspace)),
        Key::Enter => (None, Some(SpecialKey::Enter)),
        Key::Tab => (None, Some(SpecialKey::Tab)),
        Key::Escape => (None, Some(SpecialKey::Escape)),
        Key::Function(n) => (None, Some(SpecialKey::Function(*n))),
        _ => (None, None),
    }
}

/// Runs one catalog action (`ShortcutCatalog`'s id) from a rebound key.
fn execute_action(state: &mut AppState, id: &str) -> bool {
    match id {
        "project.save" => {
            state.save_project();
            true
        }
        "project.close" => {
            state.close_project();
            true
        }
        "app.preferences" => {
            state.preferences_open = true;
            state.preferences_page = 0;
            true
        }
        "edit.undo" => {
            state.editor().editor.undo();
            true
        }
        "edit.redo" => {
            state.editor().editor.redo();
            true
        }
        "edit.split" | "edit.splitAll" => {
            let editor = state.editor();
            if !editor.split_at_playhead().is_empty() {
                editor.save = crate::editor_state::SaveState::Dirty;
            }
            true
        }
        "tool.select" => {
            state.editor().tool = crate::editor_state::Tool::Select;
            true
        }
        "tool.blade" => {
            state.editor().tool = crate::editor_state::Tool::Blade;
            true
        }
        "edit.copy" => {
            state.editor().editor.copy_selected();
            true
        }
        "edit.cut" => {
            let editor = state.editor();
            editor.editor.cut_selected();
            editor.save = crate::editor_state::SaveState::Dirty;
            true
        }
        "edit.paste" => {
            let editor = state.editor();
            let at = editor.playhead;
            if !editor.editor.paste(at, None).is_empty() {
                editor.save = crate::editor_state::SaveState::Dirty;
            }
            true
        }
        "edit.duplicate" => {
            let editor = state.editor();
            if !editor.editor.duplicate_selected().is_empty() {
                editor.save = crate::editor_state::SaveState::Dirty;
            }
            true
        }
        "edit.magnet" => {
            let editor = state.editor();
            let on = !editor.doc().main_track_magnet;
            editor.editor.set_main_track_magnet(on);
            true
        }
        "play.loop" => {
            let transport = &mut state.editor().transport;
            transport.loop_playback = !transport.loop_playback;
            true
        }
        "play.frameBack" | "play.frameForward" | "play.secondBack" | "play.secondForward" => {
            let editor = state.editor();
            let rate = editor.project_rate().max(1.0);
            let seconds = if id.ends_with("Back") || id.ends_with("Forward") {
                if id.starts_with("play.second") { 1.0 } else { 1.0 / rate }
            } else {
                1.0 / rate
            };
            let backwards = id.contains("Back");
            let duration = editor.duration_sec();
            editor.playhead = if backwards {
                (editor.playhead - seconds).max(0.0)
            } else {
                (editor.playhead + seconds).min(duration)
            };
            editor.transport.clock_sec = editor.playhead;
            true
        }
        "play.shuttleForward" => {
            state.editor().transport.shuttle_towards(true);
            true
        }
        "play.shuttleBack" => {
            state.editor().transport.shuttle_towards(false);
            true
        }
        "play.shuttleStop" => {
            state.editor().transport.stop();
            true
        }
        "play.audioScrub" => {
            let transport = &mut state.editor().transport;
            transport.audio_scrub = !transport.audio_scrub;
            true
        }
        "play.previousEdit" => {
            let editor = state.editor();
            editor.playhead = previous_cut(editor, editor.playhead);
            true
        }
        "play.nextEdit" => {
            let editor = state.editor();
            editor.playhead = next_cut(editor, editor.playhead);
            true
        }
        "play.start" => {
            let editor = state.editor();
            editor.playhead = 0.0;
            editor.transport.clock_sec = 0.0;
            true
        }
        "play.end" => {
            let editor = state.editor();
            editor.playhead = editor.duration_sec();
            true
        }
        "view.mediaPanel" => {
            let editor = state.editor();
            editor.left_collapsed = !editor.left_collapsed;
            state.save_workspace();
            true
        }
        "view.inspector" => {
            let editor = state.editor();
            editor.right_collapsed = !editor.right_collapsed;
            state.save_workspace();
            true
        }
        "view.zoomIn" => {
            state.editor().timeline.zoom(1.25);
            true
        }
        "view.zoomOut" => {
            state.editor().timeline.zoom(0.8);
            true
        }
        "view.zoomFit" => {
            let duration = state.editor().duration_sec();
            let width = rune::memo::layout_size("editor.workspace")
                .map(|size| size[0] - crate::timeline::HEADER_W)
                .unwrap_or(1000.0);
            state.editor().timeline.fit(duration, width);
            true
        }
        _ => {
            state.toasts.show(format!("'{id}' isn't wired yet"));
            true
        }
    }
}

/// The redo gesture the C# default binding offers (`Ctrl+Y`), kept for the non-override path.
#[allow(dead_code)]
fn is_redo_default(event: &KeyEvent) -> bool {
    matches!(event.key, Key::Command('y')) || (matches!(event.key, Key::Command('z')) && event.mods.shift)
}
