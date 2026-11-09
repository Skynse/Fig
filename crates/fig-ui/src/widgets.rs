//! Shared widgets: the controls the C# app draws itself (`FigSlider`, `NumberField`, segmented
//! tabs, chips, keyframe diamonds). The editor never uses stock widgets, so these are hand-drawn
//! from the same tokens and metrics.
//!
//! Grows in P4 (inspector widgets); panels already build their chrome from the small set here.

use rune::{el, icon, text, Color, El, Icon};

use crate::theme::{font, Tokens, RADIUS_CONTROL};

/// A rounded chip with a label, e.g. `30 fps`, `Auto`, `360p`.
pub fn chip(label: &str, t: &Tokens) -> El {
    el().h(26.0)
        .padding(9.0)
        .radius(RADIUS_CONTROL)
        .fill(t.card_bg)
        .center()
        .child(text(label).font(font(11.0, 500.0)).color(t.text_secondary))
}

/// A 26×26 icon button (title bar, timeline header).
pub fn icon_button(icon_def: &'static Icon, size: f32, color: Color) -> El {
    el().w(26.0).h(26.0).radius(6.0).center().child(icon(icon_def, size).color(color))
}

/// A 30×30 toolbar button with the checked (accent) state.
pub fn tool(icon_def: &'static Icon, color: Color, checked: bool) -> El {
    let mut item = el().w(30.0).h(30.0).radius(RADIUS_CONTROL).center().child(icon(icon_def, 16.0).color(color));
    if checked {
        item = item.fill(Tokens::dark().accent_soft);
    }
    item
}

/// A text button (radius 8, accent fill when primary).
pub fn button(label: &str, t: &Tokens, primary: bool) -> El {
    let (fill, color) = if primary { (t.accent, t.on_accent) } else { (t.card_bg, t.text_secondary) };
    el().h(30.0)
        .padding(12.0)
        .radius(RADIUS_CONTROL)
        .fill(fill)
        .center()
        .child(text(label).font(font(12.0, 500.0)).color(color))
}

/// A one-line input well (not yet interactive; the caption/inspector text fields replace it).
pub fn field(placeholder: &str, t: &Tokens) -> El {
    el().h(30.0)
        .radius(RADIUS_CONTROL)
        .fill(t.surface_bg)
        .border(1.0, t.border)
        .padding(10.0)
        .center_y()
        .child(text(placeholder).font(font(11.0, 400.0)).color(t.text_faint))
}

/// A horizontal hairline.
pub fn rule(t: &Tokens) -> El {
    el().w_full().h(1.0).fill(t.border)
}

/// A rounded panel surface.
pub fn panel_surface(t: &Tokens, radius: f32) -> El {
    el().fill(t.panel_bg).radius(radius).clip_rounded(radius)
}
