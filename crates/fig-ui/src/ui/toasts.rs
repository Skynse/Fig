//! Toasts: short messages stacked at the top right, each for a few seconds (`ToastHost.axaml`).

use std::time::{Duration, Instant};

use rune::{El, col, el, text};

use crate::app::AppState;
use crate::ui::theme::{Tokens, font, hex};

const LIFETIME: Duration = Duration::from_millis(3200);

#[derive(Default)]
pub struct Toasts {
    items: Vec<(String, Instant)>,
}

impl Toasts {
    /// Drops expired toasts; true when one went (the view must redraw).
    pub fn expire(&mut self) -> bool {
        let before = self.items.len();
        self.items.retain(|(_, born)| born.elapsed() < LIFETIME);
        self.items.len() != before
    }

    pub fn any(&self) -> bool {
        !self.items.is_empty()
    }
}

pub fn show(app: &mut AppState, message: impl Into<String>) {
    app.toasts.items.push((message.into(), Instant::now()));
    if app.toasts.items.len() > 4 {
        app.toasts.items.remove(0);
    }
}

pub fn overlay(app: &AppState, t: &Tokens) -> El {
    let _ = t;
    let mut stack = col().absolute(0.0, 0.0).w_full().items_end().pt(62.0).pr(16.0).gap(8.0).inert();
    for (message, _) in &app.toasts.items {
        stack = stack.child(
            el().px(14.0)
                .py(10.0)
                .max_w(420.0)
                .radius(6.0)
                .fill(hex("#2a2a2b"))
                .border(1.0, hex("#4da3ff"))
                .child(text(message.clone()).font(font(13.0, 400.0)).color(hex("#eeeeee")).wrap()),
        );
    }
    stack
}
