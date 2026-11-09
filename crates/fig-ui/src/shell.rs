//! The shell: the undecorated window's app-drawn title bar and the menu dropdowns.
//!
//! The C# window has `WindowDecorations="None"`; `TitleBar.axaml` draws the brand, File/Edit/View/
//! Help, the save status, the panel toggles, Export and the window buttons, and the strip moves the
//! window (inventory §1.3–§1.4). Rune draws the same strip and takes the drag with `El::window_drag`.

use rune::{Tx, col, el, icon, row, text, El};

use crate::app::{AppState, Menu, Route};
use crate::icons;
use crate::theme::{font, Tokens, TITLEBAR_H};

const BRAND_TILE: &str = "#7a3596";

fn menu_label(label: &str, menu: Menu, t: &Tokens) -> El {
    text(label)
        .key(format!("menu.{label}"))
        .font(font(13.0, 400.0))
        .color(t.text_secondary)
        .on_press(move |s: &mut AppState| {
            s.menu = if s.menu == Some(menu) { None } else { Some(menu) };
        })
}

pub fn title_bar(state: &AppState, t: &Tokens) -> El {
    let brand = row()
        .gap(8.0)
        .center_y()
        .child(
            el().w(22.0)
                .h(22.0)
                .radius(6.0)
                .fill(crate::theme::hex(BRAND_TILE))
                .child(
                    rune::canvas(rune::Shape::new().fill(vec![[0.0, 0.0], [7.0, 4.0], [0.0, 8.0]], rune::Color::WHITE))
                        .absolute(8.0, 7.0)
                        .w(7.0)
                        .h(8.0),
                ),
        )
        .child(text("fig").font(font(12.5, 600.0)).color(t.text_primary));

    let menus = row()
        .gap(18.0)
        .center_y()
        .child(menu_label("File", Menu::File, t))
        .child(menu_label("Edit", Menu::Edit, t))
        .child(menu_label("View", Menu::View, t))
        .child(menu_label("Help", Menu::Help, t));

    let center = if state.route == Route::Home {
        text("fig").font(font(11.0, 400.0)).color(t.text_muted)
    } else {
        text(state.save_status()).font(font(11.0, 400.0)).color(t.text_muted)
    };

    let toggles = row()
        .gap(6.0)
        .center_y()
        .child(crate::widgets::icon_button(&icons::GRID_2X2, 15.0, t.text_secondary))
        .child(crate::widgets::icon_button(&icons::LIST, 15.0, t.text_secondary))
        .child(
            el().w(34.0)
                .h(26.0)
                .radius(6.0)
                .border(1.0, t.border)
                .center()
                .child(icon(&icons::PANEL_LEFT, 15.0).color(if state.editor.as_ref().is_some_and(|e| e.left_collapsed) {
                    t.text_faint
                } else {
                    t.text_secondary
                }))
                .on_press(|s: &mut AppState| {
                    let editor = s.editor();
                    editor.left_collapsed = !editor.left_collapsed;
                    s.save_workspace();
                }),
        )
        .child(
            el().w(34.0)
                .h(26.0)
                .radius(6.0)
                .border(1.0, t.border)
                .center()
                .child(icon(&icons::PANEL_RIGHT, 15.0).color(if state.editor.as_ref().is_some_and(|e| e.right_collapsed) {
                    t.text_faint
                } else {
                    t.text_secondary
                }))
                .on_press(|s: &mut AppState| {
                    let editor = s.editor();
                    editor.right_collapsed = !editor.right_collapsed;
                    s.save_workspace();
                }),
        );

    let export = el()
        .key("title.export")
        .w(86.0)
        .h(30.0)
        .radius(8.0)
        .fill(t.accent)
        .center()
        .child(text("Export").font(font(13.0, 600.0)).color(t.on_accent))
        .on_press(|s: &mut AppState| s.dialogs.open_export());

    let window_buttons = row()
        .center_y()
        .child(
            crate::widgets::icon_button(&icons::MINUS, 13.0, t.text_secondary)
                .on_press_tx(|_: &mut AppState, tx: &mut Tx| tx.minimize()),
        )
        .child(
            crate::widgets::icon_button(&icons::SQUARE, 11.0, t.text_secondary)
                .on_press_tx(|_: &mut AppState, tx: &mut Tx| tx.toggle_maximized()),
        )
        .child(
            crate::widgets::icon_button(&icons::X, 13.0, t.text_secondary)
                .on_press_tx(|_: &mut AppState, tx: &mut Tx| tx.close_window()),
        );

    let mut bar = row()
        .w_full()
        .h(TITLEBAR_H)
        .fill(t.surface_bg)
        .padding(12.0)
        .center_y()
        .window_drag()
        .child(brand)
        .child(el().w(20.0))
        .child(menus)
        .child(el().grow(1.0))
        .child(center)
        .child(el().grow(1.0));
    if state.route == Route::Editor {
        bar = bar.child(toggles).child(el().w(12.0)).child(export).child(el().w(8.0));
    }
    bar.child(window_buttons)
}

fn item(label: &str, shortcut: &str, t: &Tokens, action: fn(&mut AppState)) -> El {
    row()
        .w_full()
        .h(30.0)
        .radius(8.0)
        .padding(10.0)
        .center_y()
        .child(text(label).font(font(13.0, 400.0)).color(t.text_primary))
        .child(el().grow(1.0))
        .child(text(shortcut).font(font(11.0, 400.0)).color(t.text_faint))
        .on_press(move |s: &mut AppState| {
            action(s);
            s.menu = None;
        })
}

/// A menu item that only applies while a project is open (Home has no timeline to edit).
fn item_editor(label: &str, shortcut: &str, t: &Tokens, action: fn(&mut AppState)) -> El {
    row()
        .w_full()
        .h(30.0)
        .radius(8.0)
        .padding(10.0)
        .center_y()
        .child(text(label).font(font(13.0, 400.0)).color(t.text_primary))
        .child(el().grow(1.0))
        .child(text(shortcut).font(font(11.0, 400.0)).color(t.text_faint))
        .on_press(move |s: &mut AppState| {
            if !s.has_editor() {
                s.toasts.show("Open a project first");
            } else {
                action(s);
            }
            s.menu = None;
        })
}

fn separator(t: &Tokens) -> El {
    el().w_full().h(1.0).fill(t.border).padding(0.0).child(el().w_full().h(1.0))
}

/// The open menu's dropdown, drawn under its title-bar item.
pub fn menu_dropdown(state: &AppState, menu: Menu, t: &Tokens) -> El {
    let (x, items): (f32, Vec<El>) = match menu {
        Menu::File => (
            60.0,
            vec![
                item("New project…", "Ctrl+N", t, |s| {
                    s.toasts.show("New project");
                }),
                item("Open project…", "Ctrl+O", t, |s| {
                    s.toasts.show("Open project");
                }),
                separator(t),
                item_editor("Save", "Ctrl+S", t, |s| {
                    let e = s.editor();
                    e.save = crate::editor_state::SaveState::Saved;
                    s.toasts.show("All changes saved");
                }),
                item_editor("Close project", "Ctrl+W", t, |s| {
                    s.close_project();
                }),
                separator(t),
                item("Preferences…", "Ctrl+,", t, |s| {
                    s.preferences_open = true;
                }),
                separator(t),
                item("Quit", "Ctrl+Q", t, |s| {
                    s.toasts.show("Quit");
                }),
            ],
        ),
        Menu::Edit => (
            100.0,
            vec![
                item_editor("Undo", "Ctrl+Z", t, |s| {
                    s.editor().editor.undo();
                }),
                item_editor("Redo", "Ctrl+Y", t, |s| {
                    s.editor().editor.redo();
                }),
                separator(t),
                item_editor("Cut", "Ctrl+X", t, |s| {
                    s.toasts.show("Cut");
                }),
                item_editor("Copy", "Ctrl+C", t, |s| {
                    s.toasts.show("Copy");
                }),
                item_editor("Paste", "Ctrl+V", t, |s| {
                    s.toasts.show("Paste");
                }),
                item_editor("Duplicate", "Ctrl+D", t, |s| {
                    s.toasts.show("Duplicate");
                }),
                separator(t),
                item_editor("Split at playhead", "Ctrl+B", t, |s| {
                    let e = s.editor();
                    if !e.split_at_playhead().is_empty() {
                        e.save = crate::editor_state::SaveState::Dirty;
                    }
                }),
                item_editor("Delete", "Del", t, |s| {
                    s.editor().editor.lift_selected();
                }),
                separator(t),
                item_editor("Select all", "Ctrl+A", t, |s| {
                    s.editor().editor.select_all();
                }),
            ],
        ),
        Menu::View => (
            145.0,
            vec![
                item_editor("Show media panel", "Ctrl+D1", t, |s| {
                    let e = s.editor();
                    e.left_collapsed = !e.left_collapsed;
                }),
                item_editor("Show inspector", "Ctrl+D2", t, |s| {
                    let e = s.editor();
                    e.right_collapsed = !e.right_collapsed;
                }),
                separator(t),
                item_editor("Zoom in", "Ctrl++", t, |s| {
                    s.editor().timeline.zoom(1.25);
                }),
                item_editor("Zoom out", "Ctrl+-", t, |s| {
                    s.editor().timeline.zoom(0.8);
                }),
                item_editor("Fit sequence", "Shift+Z", t, |s| {
                    let duration = s.editor().duration_sec();
                    let width = 1000.0;
                    s.editor().timeline.fit(duration, width);
                }),
                separator(t),
                item_editor("Preferences…", "Ctrl+,", t, |s| {
                    s.preferences_open = true;
                }),
            ],
        ),
        Menu::Help => (
            190.0,
            vec![
                item("Keyboard shortcuts", "?", t, |s| {
                    s.dialogs.open_shortcuts();
                }),
                item("About Fig", "", t, |s| {
                    s.toasts.show("Fig — a simple open-source video editor");
                }),
            ],
        ),
    };

    col()
        .absolute(x, 42.0)
        .w(240.0)
        .padding(6.0)
        .gap(1.0)
        .fill(crate::theme::hex("#23252b"))
        .radius(12.0)
        .border(1.0, crate::theme::hex("#2f323a"))
        .children(items)
}

/// A transparent full-window layer under an open menu/dropdown: a press anywhere else closes it.
pub fn menu_scrim(_t: &Tokens) -> El {
    el().absolute(0.0, 0.0)
        .w_full()
        .h_full()
        .on_press(|s: &mut AppState| {
            s.menu = None;
            if let Some(editor) = s.editor.as_mut() {
                editor.context_menu = None;
            }
        })
}
