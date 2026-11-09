//! Fig's Rust UI entry point.
//!
//! The Avalonia app's 1:1 port on Rune. Screens route through [`app::AppState`]; each screen owns
//! its module. Headless snapshots are the acceptance gate:
//!
//! ```text
//! cargo run -p fig-ui -- --snap out/editor.png --scene editor
//! ```
//!
//! Scenes are registered in [`app::SCENES`], one per screen/state the 1:1 harness compares against
//! `docs/ui-port/reference/`.

mod app;
mod captions;
mod chrome;
mod dialogs;
mod editor;
mod editor_state;
mod graph;
mod home;
mod icons;
mod inspector;
mod keyboard;
mod library;
mod playback;
mod preferences;
mod preview;
mod services;
mod shell;
mod shortcuts;
mod theme;
mod timeline;
mod timeline_gesture;
mod timeline_hit;
mod timeline_menu;
mod toasts;
mod widgets;

#[cfg(test)]
mod interaction_tests;

use std::time::Duration;

use rune::{App, AppSpec, Cx, El, KeyEvent, Tx, Waker, WindowOptions};

fn view(state: &app::AppState, cx: &mut Cx<app::AppState>) -> El {
    app::view(state, cx)
}

fn setup(state: &mut app::AppState, waker: Waker) {
    state.set_waker(waker);
}

/// Runs before every frame. `now` is seconds since start (virtual in headless runs), so the delta is
/// worked out here; a playing sequence wakes itself for the next frame and reports the change.
fn poll(state: &mut app::AppState, now: f64, tx: &mut Tx) -> bool {
    let dt = (now - state.now).clamp(0.0, 0.1);
    state.now = now;
    let changed = state.tick(dt);
    if state.is_playing() {
        tx.wake_after(Duration::from_millis(16));
        return true;
    }
    changed
}

fn on_key(state: &mut app::AppState, key: &KeyEvent, tx: &mut Tx) -> bool {
    let handled = keyboard::handle(state, key);
    if handled {
        // Rune does not schedule a frame for AppSpec::on_key by itself: without this a handled
        // shortcut (Space, Ctrl+Z, ...) changes the state but the window never repaints.
        tx.instant();
        tx.wake_after(Duration::from_millis(1));
    }
    handled
}

fn main() {
    let spec = AppSpec::<app::AppState> {
        min_size: Some((theme::WINDOW_MIN_W, theme::WINDOW_MIN_H)),
        setup: Some(setup),
        poll: Some(poll),
        on_key: Some(on_key),
        scenes: app::SCENES,
        ..AppSpec::new("fig", (1200.0, 700.0), app::AppState::new, view)
    };

    // Fig's window has no system frame: the title bar is drawn by `shell` and the edge grips by
    // `chrome`. Windows/macOS keep Rune's native hit-tested frame; Linux uses the grips.
    App::new(spec)
        .on_modifiers(|state: &mut app::AppState, mods: rune::Mods, _tx: &mut Tx| state.mods = mods)
        .window_options(WindowOptions {
            borderless: true,
            min_size: Some((theme::WINDOW_MIN_W, theme::WINDOW_MIN_H)),
            ..Default::default()
        })
        .run();
}
