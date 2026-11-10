//! Where elements are, for handlers. Rune answers `window_layout` only while a view builds, so an element a handler
//! needs to place things by (a menu's anchor, the timeline under a library drag) reports its rect here as it is laid
//! out.

use std::cell::RefCell;
use std::collections::HashMap;

use rune::{El, Tx};

use crate::app::AppState;

thread_local! {
    static RECTS: RefCell<HashMap<String, [f32; 4]>> = RefCell::new(HashMap::new());
}

/// Keys an element and keeps its window rect for [`rect`] (without rebuilding the view when it moves).
pub fn tracked(el: El, key: impl Into<String>) -> El {
    let key = key.into();
    let k = key.clone();
    el.key(key).on_layout_tx(move |_: &mut AppState, r: [f32; 4], tx: &mut Tx| {
        RECTS.with_borrow_mut(|m| m.insert(k.clone(), r));
        tx.live_only();
    })
}

/// Keeps a rect reported by an element's own `on_layout`.
pub fn remember(key: &str, r: [f32; 4]) {
    RECTS.with_borrow_mut(|m| m.insert(key.to_string(), r));
}

/// An element's rect in window points: as laid out now inside a view build, else as last reported.
pub fn rect(key: &str) -> Option<[f32; 4]> {
    rune::memo::window_layout(key).or_else(|| RECTS.with_borrow(|m| m.get(key).copied()))
}
