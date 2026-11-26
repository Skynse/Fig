//! The undecorated window's edge grips: the right edge, the bottom edge and their corner.
//!
//! The C# window has `WindowDecorations="None"` and grips give it resize edges (`MainWindow.axaml`). Rune exposes no
//! compositor-driven resize on Linux, so the grips resize the window themselves with `Tx::resize_window_instant`.
//! Rune keeps the top-left corner where it is and can't move a window on Wayland, so there are no left or top grips
//! until Rune grows a `drag_resize_window`.

use rune::{Drag, El, Phase, Tx};

/// FIG_DECORATED=1: the system's own window frame (title bar and borders, resized by the compositor) instead of
/// Fig's. Then Fig draws no window buttons and no grips.
pub fn decorated() -> bool {
    static ON: std::sync::LazyLock<bool> = std::sync::LazyLock::new(|| std::env::var_os("FIG_DECORATED").is_some());
    *ON
}

use crate::app::AppState;

const EDGE: f32 = 5.0;
const CORNER: f32 = 10.0;

#[derive(Clone, Copy)]
pub enum Grip {
    South,
    East,
    SouthEast,
}

impl Grip {
    fn dx(self) -> f32 {
        if matches!(self, Grip::East | Grip::SouthEast) { 1.0 } else { 0.0 }
    }

    fn dy(self) -> f32 {
        if matches!(self, Grip::South | Grip::SouthEast) { 1.0 } else { 0.0 }
    }
}

/// The grips for the window, placed from the previous frame's root layout (the first frame falls back to the app's
/// initial 1200×700). Only the right, bottom and their corner: Rune resizes keeping the top-left corner where it is
/// and can't move a window on Wayland, so a left or top grip would grow the window away from the pointer.
pub fn resize_handles() -> Vec<El> {
    if decorated() {
        return Vec::new();
    }
    let [w, h] = rune::memo::layout_size("app.root").unwrap_or([1200.0, 700.0]);
    let (c, e) = (CORNER, EDGE);
    vec![
        grip(Grip::South, 0.0, h - e, w - c, e),
        grip(Grip::East, w - e, 0.0, e, h - c),
        grip(Grip::SouthEast, w - c, h - c, c, c),
    ]
}

fn grip(which: Grip, x: f32, y: f32, w: f32, h: f32) -> El {
    let cursor = match which {
        Grip::East => rune::Cursor::ResizeColumn,
        Grip::South => rune::Cursor::ResizeRow,
        Grip::SouthEast => rune::Cursor::Move,
    };
    rune::el()
        .absolute(x, y)
        .w(w)
        .h(h)
        .cursor(cursor)
        .on_drag(move |s: &mut AppState, drag: &mut Drag, tx: &mut Tx| on_drag(s, which, drag, tx))
}

fn on_drag(state: &mut AppState, grip: Grip, drag: &mut Drag, tx: &mut Tx) {
    match drag.phase {
        Phase::Down => {
            state.resize_origin = Some(state.window_size);
        }
        Phase::Move => {
            let Some((w0, h0)) = state.resize_origin else { return };
            let w = (w0 + grip.dx() * drag.dx).clamp(crate::ui::theme::WINDOW_MIN_W, 8192.0);
            let h = (h0 + grip.dy() * drag.dy).clamp(crate::ui::theme::WINDOW_MIN_H, 8192.0);
            if (w - w0).abs() > 0.5 || (h - h0).abs() > 0.5 {
                // at once: the animated resize springs toward each new size, so the window trails the pointer
                tx.resize_window_instant((w, h));
                tx.instant();
            }
        }
        Phase::Up => state.resize_origin = None,
    }
}
