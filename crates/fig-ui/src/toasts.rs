//! Toasts: the small bottom-centre messages (`ToastHost.axaml`, inventory §1.5).
//!
//! The C# host stacks them over the workspace, each for a few seconds. The port keeps the same
//! look; timing lands with the shell polish phase.

use rune::{col, el, text, El};

use crate::theme::{font, Tokens};

pub struct Toast {
    pub message: String,
    /// Seconds of runtime time when it was raised (drives the fade-out once timed).
    pub born: f64,
}

const TOAST_SECONDS: f64 = 2.8;

#[derive(Default)]
pub struct Toasts {
    pub items: Vec<Toast>,
    /// Seconds of app time, advanced by [`tick`](Self::tick).
    pub clock: f64,
}

impl Toasts {
    pub fn new() -> Toasts {
        Toasts { items: Vec::new(), clock: 0.0 }
    }

    pub fn show(&mut self, message: impl Into<String>) {
        self.items.push(Toast { message: message.into(), born: self.clock });
        if self.items.len() > 4 {
            self.items.remove(0);
        }
    }

    /// Ages the stack; true when a toast expired (the frame must redraw).
    pub fn tick(&mut self, dt: f64) -> bool {
        self.clock += dt;
        let before = self.items.len();
        let clock = self.clock;
        self.items.retain(|toast| clock - toast.born < TOAST_SECONDS);
        self.items.len() != before
    }
}

pub fn overlay(toasts: &Toasts, t: &Tokens) -> El {
    let mut stack = col().absolute(0.0, 0.0).w_full().h_full().justify_end().center_y().padding(18.0).gap(6.0);
    for toast in &toasts.items {
        stack = stack.child(
            el().h(30.0)
                .padding(14.0)
                .radius(6.0)
                .fill(crate::theme::hex("#26282d"))
                .border(1.0, crate::theme::hex("#34373c"))
                .center()
                .child(text(toast.message.clone()).font(font(13.0, 400.0)).color(crate::theme::hex("#eeeeee"))),
        );
    }
    stack
}
