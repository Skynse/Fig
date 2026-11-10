//! Fig: a video editor for short art reels.

mod app;
mod commands;
mod desktop;
mod dialogs;
mod editor;
mod home;
mod playback;
mod preferences;
mod settings;
mod timeline;
mod ui;

use rune::{App, AppSpec, KeyEvent, Tx, Waker, WindowOptions};

use crate::app::AppState;

fn setup(app: &mut AppState, waker: Waker) {
    app.surface.set_waker(waker);
}

fn poll(app: &mut AppState, _now: f64, tx: &mut Tx) -> bool {
    app.poll(tx)
}

fn on_key(app: &mut AppState, ev: &KeyEvent, tx: &mut Tx) -> bool {
    let handled = commands::keys::handle(app, ev, tx);
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
    // Native Wayland by default: through XWayland, NVIDIA's Vulkan driver fails to reconfigure the surface when the
    // window is resized or moved (wgpu panics "Failed to wait for GPU to come idle"). Wayland gives winit 0.30 no
    // file drops, so FIG_X11=1 runs through XWayland for dropping files from a file manager.
    #[cfg(target_os = "linux")]
    if std::env::var_os("FIG_X11").is_some() && std::env::var_os("DISPLAY").is_some() {
        // SAFETY: nothing else runs yet; the variable is read once when the window system starts.
        unsafe { std::env::remove_var("WAYLAND_DISPLAY") };
    }
    fig_media::init();
    let spec = AppSpec::<AppState> {
        min_size: Some((ui::theme::WINDOW_MIN_W, ui::theme::WINDOW_MIN_H)),
        setup: Some(setup),
        poll: Some(poll),
        on_key: Some(on_key),
        ..AppSpec::new("fig", (1280.0, 760.0), AppState::new, app::view)
    };
    let app = App::new(spec)
        .on_modifiers(|s: &mut AppState, mods: rune::Mods, _tx: &mut Tx| s.mods = mods)
        // the window manager's close can't wait for an answer: keep the changes
        .on_close(|s: &mut AppState, _tx: &mut Tx| s.close_project_quietly())
        .window_options(WindowOptions { borderless: true, min_size: Some((ui::theme::WINDOW_MIN_W, ui::theme::WINDOW_MIN_H)), ..Default::default() });
    app::set_handle(app.handle());
    app.run();
}
