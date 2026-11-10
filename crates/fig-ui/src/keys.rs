//! The keyboard. Rune hands the window keys the focused control didn't take, so typing in a field never starts a
//! shortcut. Escape closes the topmost thing; everything else goes through the command table (rebindable).

use rune::{Key, KeyEvent, Tx};

use crate::app::AppState;
use crate::commands::{self, Command};

pub fn handle(app: &mut AppState, ev: &KeyEvent, tx: &mut Tx) -> bool {
    if ev.key == Key::Escape {
        return escape(app);
    }
    // while recording a shortcut in Preferences, the key is the answer
    if let Some(p) = app.preferences_open.as_mut()
        && p.recording.is_some()
    {
        return crate::preferences::record_key(app, ev);
    }
    if app.dialog.is_some() || app.preferences_open.is_some() {
        return false;
    }
    if app.editor.is_none() {
        return crate::home::key(app, ev, tx);
    }
    if app.editor.as_ref().is_some_and(|e| e.graph.is_some()) {
        return crate::graph::key(app, ev);
    }
    let Some(command) = commands::for_key(&ev.key, ev.mods, &app.preferences.keyboard.shortcuts) else { return false };
    app.popup = None;
    crate::actions::run(app, command, tx);
    true
}

/// Escape: closes the menu, a dialog or Preferences; cancels a drag; puts the blade away; clears the selection.
fn escape(app: &mut AppState) -> bool {
    if app.field_key.is_some() {
        crate::widgets::finish_typing(app, false);
        return true;
    }
    if app.popup.take().is_some() {
        return true;
    }
    if app.dialog.is_some() {
        crate::dialogs::close(app);
        return true;
    }
    if app.preferences_open.take().is_some() {
        return true;
    }
    let Some(e) = app.editor.as_mut() else { return false };
    if e.graph.is_some() {
        return crate::graph::escape(app);
    }
    if crate::timeline::input::cancel(e) {
        return true;
    }
    if e.tool == crate::editor::Tool::Blade {
        e.tool = crate::editor::Tool::Select;
        return true;
    }
    if e.tl.gap.take().is_some() {
        return true;
    }
    if !e.ed.selection.is_empty() {
        e.ed.selection.clear();
        e.tl.track_selected = None;
        return true;
    }
    false
}

/// Space plays from anywhere in the editor (not inside text fields, which take it first).
pub fn is_space(ev: &KeyEvent) -> bool {
    matches!(&ev.key, Key::Text(t) if t == " ") && !ev.mods.cmd
}

#[allow(dead_code)]
fn _cmd(_: Command) {}
