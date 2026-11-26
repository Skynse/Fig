//! The controls Fig draws itself: buttons, the slider (FigSlider), the number field you drag or type into
//! (NumberField), checkboxes, switches, dropdowns and text fields. All take their colours from the theme.

use std::rc::Rc;

use rune::{Color, Cursor, Drag, El, Icon, Phase, TextEdit, Tx, el, icon, row, text, text_input};

use crate::app::AppState;
use crate::ui::theme::{RADIUS_CONTROL, Tokens, font, hex, mono};

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum Kind {
    Normal,
    Primary,
    Ghost,
    Danger,
}

pub fn button(label: impl Into<String>, kind: Kind, t: &Tokens) -> El {
    let (fill, hover, color) = match kind {
        Kind::Normal => (t.chip_bg, t.hover_bg, t.text_primary),
        Kind::Primary => (t.accent, hex("#8bb8ff"), t.on_accent),
        Kind::Ghost => (Color::TRANSPARENT, t.chip_bg, t.text_secondary),
        Kind::Danger => (hex("#3a2226"), hex("#4a292e"), t.danger),
    };
    el().h(30.0)
        .px(12.0)
        .radius(RADIUS_CONTROL)
        .fill(fill)
        .hover(hover)
        .press(t.pressed_bg)
        .center()
        .cursor(Cursor::Pointer)
        .child(text(label.into()).font(font(12.5, if kind == Kind::Primary { 600.0 } else { 500.0 })).color(color))
}

/// A button with an icon before its label.
pub fn icon_text_button(i: &'static Icon, label: impl Into<String>, kind: Kind, t: &Tokens) -> El {
    let color = match kind {
        Kind::Primary => t.on_accent,
        Kind::Danger => t.danger,
        _ => t.text_primary,
    };
    let (fill, hover) = match kind {
        Kind::Normal => (t.chip_bg, t.hover_bg),
        Kind::Primary => (t.accent, hex("#8bb8ff")),
        Kind::Ghost => (Color::TRANSPARENT, t.chip_bg),
        Kind::Danger => (hex("#3a2226"), hex("#4a292e")),
    };
    row()
        .h(30.0)
        .px(12.0)
        .gap(6.0)
        .radius(RADIUS_CONTROL)
        .fill(fill)
        .hover(hover)
        .press(t.pressed_bg)
        .center()
        .cursor(Cursor::Pointer)
        .child(icon(i, 14.0).color(color))
        .child(text(label.into()).font(font(12.5, 500.0)).color(color))
}

/// A square transparent icon button.
pub fn icon_button(i: &'static Icon, size: f32, box_size: f32, t: &Tokens) -> El {
    el().w(box_size)
        .h(box_size)
        .radius(RADIUS_CONTROL)
        .hover(t.chip_bg)
        .press(t.pressed_bg)
        .center()
        .cursor(Cursor::Pointer)
        .child(icon(i, size).color(t.text_secondary))
}

/// An icon button that shows an on state (accent tint).
pub fn toggle_button(i: &'static Icon, on: bool, box_size: f32, t: &Tokens) -> El {
    el().w(box_size)
        .h(box_size)
        .radius(RADIUS_CONTROL)
        .fill(if on { t.accent_soft } else { Color::TRANSPARENT })
        .hover(if on { t.accent_soft } else { t.chip_bg })
        .press(t.pressed_bg)
        .center()
        .cursor(Cursor::Pointer)
        .child(icon(i, 16.0).color(if on { t.accent_text } else { t.text_secondary }))
}

pub fn rule(t: &Tokens) -> El {
    el().w_full().h(1.0).fill(t.border)
}

pub fn label(s: impl Into<String>, t: &Tokens) -> El {
    text(s.into()).font(font(12.0, 400.0)).color(t.text_muted)
}

pub fn heading(s: impl Into<String>, t: &Tokens) -> El {
    text(s.into()).font(font(12.0, 600.0)).color(t.text_secondary)
}

pub fn note(s: impl Into<String>, t: &Tokens) -> El {
    text(s.into()).font(font(11.5, 400.0)).color(t.text_faint).wrap()
}

pub type OnValue = Rc<dyn Fn(&mut AppState, f64, Phase)>;

/// FigSlider: a 3 px rail and a thumb. Grab the thumb (no jump) or click the rail to jump; Shift drags 10× finer;
/// double-click resets to `default`. `on` hears every change with the drag's phase (Up ends the gesture).
pub fn slider(key: impl Into<String>, value: f64, min: f64, max: f64, default: f64, on: OnValue, t: &Tokens) -> El {
    let span = (max - min).max(1e-9);
    let frac = ((value - min) / span).clamp(0.0, 1.0) as f32;
    let on_drag = on.clone();
    let reset = on;
    el().key(key.into())
        .h(18.0)
        .w_full()
        .min_w(40.0)
        .cursor(Cursor::Pointer)
        .child(el().absolute(0.0, 7.5).w_frac(1.0).h(3.0).radius(2.0).fill(t.hover_bg))
        .child(el().absolute(0.0, 7.5).w_frac(frac).h(3.0).radius(2.0).fill(t.accent))
        .child(
            el().absolute(0.0, 4.0)
                .w_frac(frac)
                .h(10.0)
                .child(row().w_full().h(10.0).justify_end().child(el().w(10.0).h(10.0).mr(-5.0).radius(5.0).fill(t.text_primary))),
        )
        .on_drag(move |s: &mut AppState, d: &mut Drag, tx: &mut Tx| {
            let w = d.w.max(1.0);
            let shift = tx.mods().shift;
            if d.phase == Phase::Down {
                // grabbing the thumb keeps it under the pointer; the rail jumps
                let thumb = frac * w;
                d.data = if (d.x - thumb).abs() <= 7.0 { d.x - thumb } else { 0.0 };
                if d.clicks >= 2 {
                    reset(s, default, Phase::Up);
                    return;
                }
            }
            let x = if shift && d.phase != Phase::Down { d.start_x - d.data + (d.x - d.start_x) * 0.1 } else { d.x - d.data };
            let v = min + (x / w).clamp(0.0, 1.0) as f64 * span;
            on_drag(s, v, d.phase);
            tx.instant();
        })
}

/// The NumberField: shows its value as text; drag up or down to change it (Shift fine, Ctrl coarse); click to type.
#[allow(clippy::too_many_arguments)]
pub fn number_field(
    key: impl Into<String>,
    value: f64,
    shown: String,
    step: f64,
    (min, max): (f64, f64),
    on: OnValue,
    app: &AppState,
    t: &Tokens,
) -> El {
    let key = key.into();
    if app.field_key.as_deref() == Some(key.as_str()) {
        return text_field_editing(&app.field_edit, t).key(key);
    }
    let typing_key = key.clone();
    let on_drag = on.clone();
    el().key(key)
        .h(22.0)
        .min_w(52.0)
        .px(6.0)
        .radius(3.0)
        .fill(hex("#1b1d22"))
        .hover(hex("#25282e"))
        .center()
        .cursor(Cursor::ResizeRow)
        .help("Drag up or down to adjust (Shift: fine) · click to type")
        .child(text(shown).font(mono(11.0, 400.0)).color(t.text_secondary))
        .drag_threshold(3.0)
        .on_drag(move |s: &mut AppState, d: &mut Drag, tx: &mut Tx| {
            let mods = tx.mods();
            let per_px = step * if mods.shift { 0.1 } else if mods.cmd { 10.0 } else { 1.0 };
            let v = (value - f64::from(d.dy) * per_px).clamp(min, max);
            on_drag(s, v, d.phase);
            tx.instant();
        })
        .on_press(move |s: &mut AppState| {
            let on = on.clone();
            begin_typing(s, &typing_key, &format_plain(value), Rc::new(move |s: &mut AppState, text: &str| {
                if let Some(v) = parse_number(text) {
                    on(s, v.clamp(min, max), Phase::Up);
                }
            }));
        })
}

fn format_plain(v: f64) -> String {
    let s = format!("{v:.3}");
    s.trim_end_matches('0').trim_end_matches('.').to_string()
}

/// Reads "12", "12.5", "1.5×", "50%", "-3°".
pub fn parse_number(text: &str) -> Option<f64> {
    let cleaned: String = text.trim().chars().filter(|c| c.is_ascii_digit() || matches!(c, '.' | '-' | ',')).collect();
    cleaned.replace(',', ".").parse().ok()
}

pub type OnText = Rc<dyn Fn(&mut AppState, &str)>;

/// Starts typing into the one-at-a-time inline field: Enter applies, Escape or clicking away cancels.
pub fn begin_typing(app: &mut AppState, key: &str, initial: &str, apply: OnText) {
    app.field_key = Some(key.to_string());
    app.field_edit = TextEdit::new(initial);
    app.field_edit.set_selection(0, initial.len());
    app.field_edit.focused = true;
    app.field_apply = Some(apply);
}

pub fn finish_typing(app: &mut AppState, commit: bool) {
    let apply = app.field_apply.take();
    let text = app.field_edit.text.clone();
    app.field_key = None;
    if commit && let Some(apply) = apply {
        apply(app, &text);
    }
}

fn text_field_editing(edit: &TextEdit, t: &Tokens) -> El {
    text_input(edit, mono(11.0, 400.0))
        .single_line()
        .h(22.0)
        .min_w(52.0)
        .px(6.0)
        .radius(3.0)
        .fill(hex("#25282e"))
        .border(1.0, t.accent)
        .color(t.text_primary)
        .on_edit(|s: &mut AppState| &mut s.field_edit)
        .on_submit(|s: &mut AppState, tx: &mut Tx| {
            finish_typing(s, true);
            tx.blur();
        })
        .on_input_key(|s: &mut AppState, k: &rune::KeyEvent, tx: &mut Tx| {
            if k.key == rune::Key::Escape {
                finish_typing(s, false);
                tx.blur();
                return true;
            }
            false
        })
}

/// A one-line text box bound to a `TextEdit` the caller keeps in state.
pub fn text_box(edit: &TextEdit, placeholder: &str, access: fn(&mut AppState) -> &mut TextEdit, t: &Tokens) -> El {
    text_input(edit, font(12.5, 400.0))
        .single_line()
        .placeholder(placeholder, t.text_faint)
        .h(30.0)
        .px(10.0)
        .radius(RADIUS_CONTROL)
        .fill(hex("#1b1d22"))
        .border(1.0, if edit.focused { t.accent } else { t.border })
        .color(t.text_primary)
        .on_edit(access)
}

pub fn checkbox(on: bool, label: impl Into<String>, t: &Tokens) -> El {
    row()
        .gap(8.0)
        .center_y()
        .cursor(Cursor::Pointer)
        .child(
            el().w(16.0)
                .h(16.0)
                .radius(4.0)
                .fill(if on { t.accent } else { hex("#1b1d22") })
                .border(1.0, if on { t.accent } else { t.border })
                .center()
                .children(on.then(|| icon(&crate::ui::icons::CHECK, 12.0).color(t.on_accent))),
        )
        .child(text(label.into()).font(font(12.5, 400.0)).color(t.text_secondary))
}

pub fn switch(on: bool, t: &Tokens) -> El {
    el().w(34.0)
        .h(20.0)
        .radius(10.0)
        .fill(if on { t.accent } else { t.hover_bg })
        .cursor(Cursor::Pointer)
        .child(el().absolute(if on { 16.0 } else { 2.0 }, 2.0).w(16.0).h(16.0).radius(8.0).fill(t.text_primary))
}

/// A dropdown button: shows the current choice, opens a menu of the others under itself.
pub fn dropdown(key: impl Into<String>, current: impl Into<String>, t: &Tokens) -> El {
    row()
        .key(key.into())
        .h(28.0)
        .px(10.0)
        .gap(6.0)
        .radius(RADIUS_CONTROL)
        .fill(t.card_bg_alt)
        .hover(t.hover_bg)
        .center_y()
        .cursor(Cursor::Pointer)
        .child(text(current.into()).font(font(12.0, 400.0)).color(t.text_secondary).grow(1.0).truncate())
        .child(icon(&crate::ui::icons::CHEVRON_DOWN, 12.0).color(t.text_muted))
}

/// A labelled row: label on the left, control on the right.
pub fn field_row(name: impl Into<String>, control: El, t: &Tokens) -> El {
    row()
        .w_full()
        .min_h(28.0)
        .gap(8.0)
        .center_y()
        .child(text(name.into()).font(font(12.0, 400.0)).color(t.text_muted).w(84.0).truncate())
        .child(el().grow(1.0).min_w(0.0).child(control))
}
