//! Fig: a video editor for short art reels.

mod actions;
mod app;
mod beats;
mod chrome;
mod commands;
mod desktop;
mod dialogs;
mod editor;
mod graph;
mod home;
mod icons;
mod inspector;
mod keys;
mod layout;
mod library;
mod media;
mod menu;
mod overlays;
mod playback;
mod preferences;
mod preview;
mod settings;
mod shell;
mod theme;
mod timeline;
mod toasts;
mod widgets;

use rune::{App, AppSpec, KeyEvent, Tx, Waker, WindowOptions};

use crate::app::AppState;

fn setup(app: &mut AppState, waker: Waker) {
    app.surface.set_waker(waker);
}

fn poll(app: &mut AppState, _now: f64, tx: &mut Tx) -> bool {
    app.poll(tx)
}

fn on_key(app: &mut AppState, ev: &KeyEvent, tx: &mut Tx) -> bool {
    let handled = keys::handle(app, ev, tx);
    if handled {
        tx.instant();
    }
    handled
}

fn main() {
    if std::env::args().nth(1).as_deref() == Some("--uninstall") {
        desktop::remove_from_app_menu();
        return;
    }
    desktop::install_crash_log();
    desktop::on_startup();
    // Wayland reports no file drops to winit 0.30, so Fig runs through XWayland on Linux (where drops work) unless
    // FIG_WAYLAND=1 asks for native Wayland.
    #[cfg(target_os = "linux")]
    if std::env::var_os("FIG_WAYLAND").is_none() && std::env::var_os("DISPLAY").is_some() {
        // SAFETY: nothing else runs yet; the variable is read once when the window system starts.
        unsafe { std::env::remove_var("WAYLAND_DISPLAY") };
    }
    fig_media::init();
    let spec = AppSpec::<AppState> {
        min_size: Some((theme::WINDOW_MIN_W, theme::WINDOW_MIN_H)),
        setup: Some(setup),
        poll: Some(poll),
        on_key: Some(on_key),
        ..AppSpec::new("fig", (1280.0, 760.0), AppState::new, app::view)
    };
    let app = App::new(spec)
        .on_modifiers(|s: &mut AppState, mods: rune::Mods, _tx: &mut Tx| s.mods = mods)
        // the window manager's close can't wait for an answer: keep the changes
        .on_close(|s: &mut AppState, _tx: &mut Tx| s.close_project_quietly())
        .window_options(WindowOptions { borderless: true, min_size: Some((theme::WINDOW_MIN_W, theme::WINDOW_MIN_H)), ..Default::default() });
    app::set_handle(app.handle());
    app.run();
}
