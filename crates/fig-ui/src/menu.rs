//! Pop-up menus drawn by the app (title-bar menus, right-click menus, dropdowns): Rune has no native ones on Linux.
//! One is open at a time; a click anywhere else closes it.

use std::rc::Rc;

use rune::{El, Tx, col, el, icon, row, text};

use crate::app::AppState;
use crate::commands::Command;
use crate::icons;
use crate::theme::{Tokens, font, hex};

pub type Action = Rc<dyn Fn(&mut AppState, &mut Tx)>;

pub enum Item {
    Action { label: String, hint: String, enabled: bool, checked: Option<bool>, danger: bool, action: Action },
    Submenu { label: String, items: Vec<Item> },
    Header(String),
    Separator,
}

impl Item {
    pub fn new(label: impl Into<String>, action: impl Fn(&mut AppState, &mut Tx) + 'static) -> Item {
        Item::Action {
            label: label.into(),
            hint: String::new(),
            enabled: true,
            checked: None,
            danger: false,
            action: Rc::new(action),
        }
    }

    /// A command from the catalog: its shortcut shown, run through `commands::run`.
    pub fn command(app: &AppState, label: impl Into<String>, command: Command) -> Item {
        let hint = crate::commands::shortcut_label(command, &app.preferences.keyboard.shortcuts);
        Item::new(label, move |s, tx| crate::actions::run(s, command, tx)).hint(hint)
    }

    pub fn hint(mut self, h: impl Into<String>) -> Item {
        if let Item::Action { hint, .. } = &mut self {
            *hint = h.into();
        }
        self
    }

    pub fn enabled(mut self, on: bool) -> Item {
        if let Item::Action { enabled, .. } = &mut self {
            *enabled = on;
        }
        self
    }

    pub fn checked(mut self, on: bool) -> Item {
        if let Item::Action { checked, .. } = &mut self {
            *checked = Some(on);
        }
        self
    }

    pub fn danger(mut self) -> Item {
        if let Item::Action { danger, .. } = &mut self {
            *danger = true;
        }
        self
    }

    pub fn submenu(label: impl Into<String>, items: Vec<Item>) -> Item {
        Item::Submenu { label: label.into(), items }
    }
}

pub struct Popup {
    pub items: Vec<Item>,
    /// Top-left, in window points (clamped to the window when drawn).
    pub x: f32,
    pub y: f32,
    pub min_w: f32,
    /// The open submenus: the index of each, from the top level down.
    pub sub: Vec<usize>,
    /// The element the menu hangs from, so pressing it again closes rather than reopens.
    pub owner: Option<String>,
    /// Hang under this element (placed when drawn: a handler can't read where things are laid out).
    pub anchor: Option<String>,
}

impl Popup {
    pub fn at(x: f32, y: f32, items: Vec<Item>) -> Popup {
        Popup { items, x, y, min_w: 220.0, sub: Vec::new(), owner: None, anchor: None }
    }

    pub fn width(mut self, w: f32) -> Popup {
        self.min_w = w;
        self
    }

    pub fn owner(mut self, key: impl Into<String>) -> Popup {
        self.owner = Some(key.into());
        self
    }
}

/// Opens a menu (replacing any open one). Pressing the same owner twice toggles it shut.
pub fn open(app: &mut AppState, popup: Popup) {
    if popup.owner.is_some() && app.popup.as_ref().is_some_and(|p| p.owner == popup.owner) {
        app.popup = None;
        return;
    }
    app.popup = Some(popup);
}

/// Opens a menu under the element keyed `key` (its rect as last laid out).
pub fn open_below(app: &mut AppState, key: &str, items: Vec<Item>) {
    let mut popup = Popup::at(0.0, 0.0, items).width(160.0).owner(key);
    popup.anchor = Some(key.to_string());
    open(app, popup);
}

const ITEM_H: f32 = 30.0;
const SEP_H: f32 = 11.0;
const HEADER_H: f32 = 24.0;

fn height_of(items: &[Item]) -> f32 {
    12.0 + items
        .iter()
        .map(|i| match i {
            Item::Separator => SEP_H,
            Item::Header(_) => HEADER_H,
            _ => ITEM_H + 2.0,
        })
        .sum::<f32>()
}

fn card(items: &[Item], path: Vec<usize>, min_w: f32, t: &Tokens) -> El {
    let mut list = col().padding(6.0).min_w(min_w).fill(hex("#23252b")).radius(12.0).border(1.0, hex("#2f323a"));
    list = list.shadow(10.0, 30.0, rune::Color::BLACK.alpha(0.45));
    for (i, item) in items.iter().enumerate() {
        let mut here = path.clone();
        here.push(i);
        list = list.child(match item {
            Item::Separator => el().w_full().h(SEP_H).py(5.0).child(el().w_full().h(1.0).fill(hex("#2f323a"))),
            Item::Header(label) => el()
                .h(HEADER_H)
                .px(10.0)
                .center_y()
                .child(text(label.clone()).font(font(11.0, 600.0)).color(t.text_muted)),
            Item::Action { label, hint, enabled, checked, danger, .. } => {
                let color = if !enabled {
                    t.text_faint
                } else if *danger {
                    t.danger
                } else {
                    t.text_primary
                };
                let mut r = row()
                    .h(ITEM_H)
                    .w_full()
                    .px(10.0)
                    .my(1.0)
                    .gap(8.0)
                    .radius(8.0)
                    .center_y()
                    .child(el().w(14.0).h(14.0).center().children(
                        checked.filter(|c| *c).map(|_| icon(&icons::CHECK, 13.0).color(t.accent_text)),
                    ))
                    .child(text(label.clone()).font(font(13.0, 400.0)).color(color).grow(1.0))
                    .child(text(hint.clone()).font(font(12.0, 400.0)).color(t.text_muted).ml(24.0));
                let parent = path.clone();
                r = r.on_hover(move |s: &mut AppState| {
                    if let Some(popup) = s.popup.as_mut() {
                        popup.sub.truncate(parent.len());
                    }
                });
                if *enabled {
                    let p = here.clone();
                    r = r.hover(hex("#323640")).press(hex("#3a3f4a")).on_press_tx(move |s: &mut AppState, tx: &mut Tx| {
                        if let Some(action) = action_at(s, &p) {
                            s.popup = None;
                            action(s, tx);
                        }
                    });
                }
                r
            }
            Item::Submenu { label, .. } => {
                let p = here.clone();
                row()
                    .h(ITEM_H)
                    .w_full()
                    .px(10.0)
                    .my(1.0)
                    .gap(8.0)
                    .radius(8.0)
                    .center_y()
                    .hover(hex("#323640"))
                    .child(el().w(14.0))
                    .child(text(label.clone()).font(font(13.0, 400.0)).color(t.text_primary).grow(1.0))
                    .child(icon(&icons::CHEVRON_RIGHT, 13.0).color(t.text_muted))
                    .on_hover(move |s: &mut AppState| {
                        if let Some(popup) = s.popup.as_mut() {
                            popup.sub = p.clone();
                        }
                    })
                    .on_press(move |s: &mut AppState| {
                        if let Some(popup) = s.popup.as_mut() {
                            popup.sub = here.clone();
                        }
                    })
            }
        });
    }
    list
}

fn action_at(app: &AppState, path: &[usize]) -> Option<Action> {
    let popup = app.popup.as_ref()?;
    let mut items = &popup.items;
    for (depth, i) in path.iter().enumerate() {
        match items.get(*i)? {
            Item::Action { action, .. } if depth == path.len() - 1 => return Some(action.clone()),
            Item::Submenu { items: inner, .. } => items = inner,
            _ => return None,
        }
    }
    None
}

/// The open menu over everything, with the click-away scrim under it.
pub fn overlay(app: &AppState, t: &Tokens) -> Option<El> {
    let popup = app.popup.as_ref()?;
    let (ww, wh) = app.window_size;
    let h = height_of(&popup.items);
    let (mut px, mut py, mut min_w) = (popup.x, popup.y, popup.min_w);
    let mut above = None;
    if let Some(key) = &popup.anchor {
        let r = crate::layout::rect(key)?;
        (px, py, min_w) = (r[0], r[1] + r[3] + 4.0, r[2].max(160.0));
        above = Some(r[1] - 4.0);
    }
    let w = min_w.max(220.0);
    let x = px.min(ww - w - 8.0).max(4.0);
    let y = if py + h > wh - 8.0 { (above.unwrap_or(py) - h).max(4.0) } else { py };
    let mut layer = el()
        .absolute(0.0, 0.0)
        .w(ww)
        .h(wh)
        .on_press(|s: &mut AppState| s.popup = None)
        .on_secondary_press(|s: &mut AppState, _, _| s.popup = None)
        .child(card(&popup.items, Vec::new(), w, t).absolute(x, y).on_press(|_: &mut AppState| {}));
    // each open submenu beside the row that opened it, as deep as they go
    let (mut items, mut px, mut py, mut pw) = (&popup.items, x, y, w);
    for depth in 0..popup.sub.len() {
        let index = popup.sub[depth];
        let Some(Item::Submenu { items: inner, .. }) = items.get(index) else { break };
        let top = py + 6.0 + items[..index]
            .iter()
            .map(|i| match i {
                Item::Separator => SEP_H,
                Item::Header(_) => HEADER_H,
                _ => ITEM_H + 2.0,
            })
            .sum::<f32>();
        let sw = 220.0;
        let sx = if px + pw + sw > ww { px - sw + 4.0 } else { px + pw - 4.0 };
        let sy = top.min(wh - height_of(inner) - 8.0).max(4.0);
        layer = layer.child(card(inner, popup.sub[..=depth].to_vec(), sw, t).absolute(sx, sy).on_press(|_: &mut AppState| {}));
        (items, px, py, pw) = (inner, sx, sy, sw);
    }
    Some(layer)
}
