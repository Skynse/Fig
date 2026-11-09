//! Real-event interaction tests: Rune's headless `Runtime` dispatches the same pointer events the
//! window does, so these prove the UI works rather than just that it draws. Keyboard goes through
//! the same `keyboard::handle` the window's `AppSpec::on_key` calls (the runtime has no App-level
//! key hook, so the test calls the handler directly).

use rune::{Env, Key, KeyEvent, Mods, Runtime};

use crate::app::{self, AppState, Menu};
use crate::editor_state::EditorState;
use crate::keyboard;
use fig_timeline::{SelectionGroup, SelectionKey};

fn runtime_with(mut state: AppState) -> Runtime<AppState> {
    // tests must not depend on the user's real preferences/workspace/shortcut overrides
    state.preferences = crate::services::Preferences::default();
    state.workspace = crate::services::WorkspaceLayout::default();
    state.gestures = crate::services::GestureMap::defaults();
    state.reload_shortcuts();
    let mut rt = Runtime::new(state, app::view, (1200.0, 700.0), Env::default());
    rt.frame(0.0);
    rt
}

fn default_runtime() -> Runtime<AppState> {
    runtime_with(AppState::new())
}

/// A runtime with a project open, as opening a project from Home gives.
fn editor_state() -> AppState {
    let mut state = AppState::new();
    state.route = app::Route::Editor;
    state.editor = Some(EditorState::demo_with_media());
    state
}

fn editor_runtime() -> Runtime<AppState> {
    runtime_with(editor_state())
}

fn click(rt: &mut Runtime<AppState>, x: f32, y: f32) {
    rt.pointer_down(x, y);
    rt.pointer_up(x, y);
    rt.frame(0.0);
}

fn key(rt: &mut Runtime<AppState>, event: KeyEvent) -> bool {
    let handled = keyboard::handle(&mut rt.state, &event);
    rt.frame(0.0);
    handled
}

fn text(rt: &mut Runtime<AppState>, value: &str) {
    key(rt, KeyEvent { key: Key::Text(value.to_string()), mods: Mods::default() });
}

fn command(rt: &mut Runtime<AppState>, c: char) {
    key(rt, KeyEvent { key: Key::Command(c), mods: Mods { cmd: true, ctrl: true, ..Default::default() } });
}

#[test]
fn clicking_a_clip_selects_it() {
    let mut rt = editor_runtime();
    let id = rt.state.editor.as_ref().unwrap().doc().tracks[0].clips[0].id.clone();
    let (x, y) = rt.find(&format!("clip.{id}")).expect("clip element is missing or culled");
    click(&mut rt, x, y);
    assert!(
        rt.state.editor.as_ref().unwrap().editor.selection.contains(&SelectionKey::new(SelectionGroup::Clip, &id)),
        "a click on a clip must select it"
    );
}

#[test]
fn dragging_a_clip_moves_it_and_one_undo_restores_it() {
    let mut rt = editor_runtime();
    let id = rt.state.editor.as_ref().unwrap().doc().tracks[0].clips[0].id.clone();
    let before = rt.state.editor.as_ref().unwrap().doc().tracks[0].clips[0].start_sec;
    let (x, y) = rt.find(&format!("clip.{id}")).expect("clip element");
    rt.pointer_down(x, y);
    rt.frame(0.0);
    rt.pointer_move(x + 120.0, y);
    rt.frame(0.0);
    rt.pointer_up(x + 120.0, y);
    rt.frame(0.0);
    let after = rt.state.editor.as_ref().unwrap().doc().tracks[0].clips[0].start_sec;
    assert!(after > before + 0.5, "drag moves the clip: {before} -> {after}");
    command(&mut rt, 'z');
    let restored = rt.state.editor.as_ref().unwrap().doc().tracks[0].clips[0].start_sec;
    assert!((restored - before).abs() < 1e-9, "one undo step restores the drag: {restored} vs {before}");
}

#[test]
fn space_plays_and_tick_advances_the_playhead_with_a_decoded_frame() {
    let mut rt = editor_runtime();
    text(&mut rt, " ");
    assert!(rt.state.editor.as_ref().unwrap().transport.playing, "Space starts playback");
    let before = rt.state.editor.as_ref().unwrap().playhead;
    rt.state.tick(0.5);
    let editor = rt.state.editor.as_ref().unwrap();
    assert!(editor.playhead > before + 0.4, "the clock advances: {} -> {}", before, editor.playhead);
    assert!(editor.preview.pixels.is_some() || editor.preview.surface.is_some(), "the monitor decodes a frame");
    text(&mut rt, "k");
    assert!(!rt.state.editor.as_ref().unwrap().transport.playing, "K stops");
}

#[test]
fn the_file_menu_opens_and_escape_closes_it() {
    let mut rt = editor_runtime();
    let (x, y) = rt.find("menu.File").expect("the File menu button");
    click(&mut rt, x, y);
    assert_eq!(rt.state.menu, Some(Menu::File), "clicking File opens the menu");
    key(&mut rt, KeyEvent { key: Key::Escape, mods: Mods::default() });
    assert_eq!(rt.state.menu, None, "Escape closes it");
}

#[test]
fn clicking_library_media_adds_a_clip() {
    let state = editor_state();
    let before: usize = state.editor.as_ref().unwrap().doc().tracks.iter().map(|t| t.clips.len()).sum();
    let mut rt = runtime_with(state);
    let (x, y) = rt.find("media.0").expect("the first library card");
    click(&mut rt, x, y);
    let after: usize = rt.state.editor.as_ref().unwrap().doc().tracks.iter().map(|t| t.clips.len()).sum();
    assert!(after > before, "clicking a media card adds it to the timeline: {before} -> {after} clips");
}

#[test]
fn dragging_the_opacity_slider_edits_the_clip() {
    let mut state = editor_state();
    let id = {
        let editor = state.editor.as_mut().unwrap();
        let id = editor.doc().tracks[0].clips[0].id.clone();
        editor.editor.selection.select_only(&id);
        id
    };
    let mut rt = runtime_with(state);
    let (x, y) = rt.find("inspector.slider.Opacity").expect("the opacity slider");
    rt.pointer_down(x, y);
    rt.frame(0.0);
    rt.pointer_move(x - 40.0, y);
    rt.frame(0.0);
    rt.pointer_up(x - 40.0, y);
    rt.frame(0.0);
    let clip = rt.state.editor.as_ref().unwrap().doc().tracks[0].clips.iter().find(|c| c.id == id).unwrap();
    assert!(clip.opacity < 0.99, "the slider edits opacity: {}", clip.opacity);
}

#[test]
fn ctrl_comma_opens_preferences_and_escape_closes_them() {
    let mut rt = editor_runtime();
    command(&mut rt, ',');
    assert!(rt.state.preferences_open, "Ctrl+, opens Preferences");
    key(&mut rt, KeyEvent { key: Key::Escape, mods: Mods::default() });
    assert!(!rt.state.preferences_open, "Escape closes Preferences");
}

#[test]
fn the_export_button_opens_the_export_dialog() {
    let mut rt = editor_runtime();
    let (x, y) = rt.find("title.export").expect("the Export button");
    click(&mut rt, x, y);
    assert!(rt.state.dialogs.is_open(), "clicking Export opens the export dialog");
}

#[test]
fn the_app_opens_on_home_with_no_project() {
    let rt = default_runtime();
    assert_eq!(rt.state.route, app::Route::Home, "the app starts on Home, like the C# build");
    assert!(rt.state.editor.is_none(), "no fabricated project is open at launch");
}

#[test]
fn home_shortcuts_do_not_open_a_project() {
    let mut rt = default_runtime();
    text(&mut rt, " ");
    assert!(rt.state.editor.is_none(), "Space on Home must not create a project");
    command(&mut rt, 'z');
    assert!(rt.state.editor.is_none(), "Ctrl+Z on Home must not create a project");
}

#[test]
fn a_rebound_shortcut_from_preferences_is_honored() {
    let mut rt = runtime_with(editor_state());
    rt.state.preferences.keyboard.shortcuts.insert("edit.split".into(), "B".into());
    rt.state.reload_shortcuts();
    rt.frame(0.0);
    let before: usize = rt.state.editor.as_ref().unwrap().doc().tracks.iter().map(|t| t.clips.len()).sum();
    // put the playhead inside the first clip so the split is possible
    rt.state.editor.as_mut().unwrap().playhead = 1.5;
    println!("overrides: {:?}", rt.state.shortcut_overrides.len());
    println!("playhead before: {}", rt.state.editor.as_ref().unwrap().playhead);
    text(&mut rt, "b");
    println!("playhead after: {}", rt.state.editor.as_ref().unwrap().playhead);
    println!("clips: {:?}", rt.state.editor.as_ref().unwrap().doc().tracks.iter().map(|t| t.clips.len()).collect::<Vec<_>>());
    let after: usize = rt.state.editor.as_ref().unwrap().doc().tracks.iter().map(|t| t.clips.len()).sum();
    assert!(after > before, "the user's B = split binding must split, not pick the blade tool: {before} -> {after}");
    assert_eq!(rt.state.editor.as_ref().unwrap().tool, crate::editor_state::Tool::Select, "the override wins over the default B");
}

#[test]
fn preferences_and_workspace_apply_to_an_opened_editor() {
    let mut prefs = crate::services::Preferences::default();
    prefs.editing.snap = false;
    prefs.editing.undo_steps = 25;
    prefs.playback.loop_playback = true;
    let workspace = crate::services::WorkspaceLayout { left: 0.32, right: 0.25, timeline: 0.42, left_collapsed: false, right_collapsed: true };
    let mut editor = EditorState::demo();
    editor.apply_preferences(&prefs, &workspace);
    assert!(!editor.editor.snap.magnetic, "snap off from preferences");
    assert!(editor.transport.loop_playback, "loop on from preferences");
    assert!((editor.left_frac - 0.32).abs() < 1e-6, "workspace left fraction");
    assert!((editor.timeline_frac - 0.42).abs() < 1e-6, "workspace timeline fraction");
    assert!(editor.right_collapsed, "workspace collapsed panel");
}
