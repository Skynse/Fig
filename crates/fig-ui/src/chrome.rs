//! The undecorated window's edge grips.
//!
//! The C# window has `WindowDecorations="None"` and eight transparent grips give it resize edges
//! (`MainWindow.axaml`, inventory §1.2). Rune's native `Titlebar`/`Chrome` handle that on
//! macOS/Windows but expose no edge-resize API on Linux, so the port draws the same grips and
//! drives `Tx::resize_window` from their drag (see `docs/ui-port/decision.md`). Rune resizes
//! top-left anchored, so the north/west grips feel different from a native frame until Rune grows
//! a `drag_resize_window` API; that is the only non-1:1 chrome behaviour on Linux.

use rune::{Drag, El, Phase, Tx};

use crate::app::AppState;

const EDGE: f32 = 5.0;
const CORNER: f32 = 10.0;

#[derive(Clone, Copy)]
pub enum Grip {
    North,
    South,
    East,
    West,
    NorthEast,
    NorthWest,
    SouthEast,
    SouthWest,
}

impl Grip {
    fn dx(self) -> f32 {
        match self {
            Grip::East | Grip::NorthEast | Grip::SouthEast => 1.0,
            Grip::West | Grip::NorthWest | Grip::SouthWest => -1.0,
            _ => 0.0,
        }
    }

    fn dy(self) -> f32 {
        match self {
            Grip::South | Grip::SouthEast | Grip::SouthWest => 1.0,
            Grip::North | Grip::NorthEast | Grip::NorthWest => -1.0,
            _ => 0.0,
        }
    }
}

/// The eight grips for the window, placed from the previous frame's root layout (the first frame
/// falls back to the app's initial 1200×700).
pub fn resize_handles() -> Vec<El> {
    let [w, h] = rune::memo::layout_size("app.root").unwrap_or([1200.0, 700.0]);
    let (c, e) = (CORNER, EDGE);
    vec![
        grip(Grip::North, 0.0, 0.0, w, e),
        grip(Grip::South, 0.0, h - e, w, e),
        grip(Grip::West, 0.0, 0.0, e, h),
        grip(Grip::East, w - e, 0.0, e, h),
        grip(Grip::NorthWest, 0.0, 0.0, c, c),
        grip(Grip::NorthEast, w - c, 0.0, c, c),
        grip(Grip::SouthWest, 0.0, h - c, c, c),
        grip(Grip::SouthEast, w - c, h - c, c, c),
    ]
}

fn grip(which: Grip, x: f32, y: f32, w: f32, h: f32) -> El {
    rune::el()
        .absolute(x, y)
        .w(w)
        .h(h)
        .on_drag(move |s: &mut AppState, drag: &mut Drag, tx: &mut Tx| on_drag(s, which, drag, tx))
}

fn on_drag(state: &mut AppState, grip: Grip, drag: &mut Drag, tx: &mut Tx) {
    match drag.phase {
        Phase::Down => {
            state.resize_origin = Some(state.window_size);
        }
        Phase::Move => {
            let Some((w0, h0)) = state.resize_origin else { return };
            let w = (w0 + grip.dx() * drag.dx).clamp(crate::theme::WINDOW_MIN_W, 8192.0);
            let h = (h0 + grip.dy() * drag.dy).clamp(crate::theme::WINDOW_MIN_H, 8192.0);
            if (w - w0).abs() > 0.5 || (h - h0).abs() > 0.5 {
                tx.resize_window((w, h));
            }
        }
        Phase::Up => state.resize_origin = None,
    }
}
