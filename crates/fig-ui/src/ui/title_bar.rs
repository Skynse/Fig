//! The title bar (`TitleBar.axaml`): the app icon and menus on the left, the project's name and save status in the
//! middle, project actions and the window buttons on the right. Empty space drags the window.

use rune::{Color, El, Tx, el, icon, row, text};

use crate::app::{AppState, Route};
use crate::commands::Command;
use crate::ui::icons;
use crate::ui::menu::Item;
use crate::ui::theme::{Tokens, font, hex};

fn menu_button(name: &'static str, t: &Tokens, build: fn(&AppState) -> Vec<Item>) -> El {
    let key = format!("menu.{name}");
    let k = key.clone();
    el().key(key)
        .h(30.0)
        .px(11.0)
        .radius(8.0)
        .hover(t.chip_bg)
        .center()
        .no_window_drag()
        .child(text(name).font(font(13.0, 400.0)).color(t.text_secondary))
        .on_press(move |s: &mut AppState| {
            let items = build(s);
            crate::ui::menu::open_below(s, &k, items);
        })
}

fn file_menu(app: &AppState) -> Vec<Item> {
    let editing = app.editor.is_some();
    vec![
        Item::command(app, "Import…", Command::Import).enabled(editing),
        Item::command(app, "Save", Command::Save).enabled(editing),
        Item::command(app, "Save as…", Command::SaveAs).enabled(editing),
        Item::command(app, "Save as package…", Command::SavePackage).enabled(editing),
        Item::Separator,
        Item::command(app, "Export…", Command::Export).enabled(editing),
        Item::command(app, "Export marked ranges…", Command::ExportRanges).enabled(editing),
        Item::command(app, "Export captions (SRT, TXT)…", Command::ExportCaptions).enabled(editing),
        Item::Separator,
        Item::command(app, "Preferences…", Command::Preferences),
        Item::command(app, "Close project", Command::CloseProject).enabled(editing),
        Item::command(app, "Exit", Command::Exit),
    ]
}

fn edit_menu(app: &AppState) -> Vec<Item> {
    let Some(e) = app.editor.as_ref() else { return vec![Item::new("Open a project to edit", |_, _| {}).enabled(false)] };
    let magnet = e.doc().main_track_magnet;
    let blade = e.tool == crate::editor::Tool::Blade;
    vec![
        Item::command(app, "Undo", Command::Undo).enabled(e.ed.can_undo()),
        Item::command(app, "Redo", Command::Redo).enabled(e.ed.can_redo()),
        Item::Separator,
        Item::command(app, "Cut", Command::Cut),
        Item::command(app, "Copy", Command::Copy),
        Item::command(app, "Paste", Command::Paste),
        Item::command(app, "Duplicate", Command::Duplicate),
        Item::Separator,
        Item::command(app, "Split at playhead", Command::Split),
        Item::command(app, "Split every track", Command::SplitAll),
        Item::command(app, "Delete before the playhead", Command::TrimStart),
        Item::command(app, "Delete after the playhead", Command::TrimEnd),
        Item::command(app, "Freeze frame", Command::Freeze),
        Item::command(app, "Ripple delete", Command::RippleDelete),
        Item::command(app, "Lift delete", Command::LiftDelete),
        Item::command(app, "Enable / disable", Command::ToggleEnabled),
        Item::command(app, "Group clips", Command::Group),
        Item::command(app, "Ungroup clips", Command::Ungroup),
        Item::Separator,
        Item::command(app, "Main track magnet", Command::Magnet).checked(magnet),
        Item::command(app, "Blade tool", Command::BladeTool).checked(blade),
        Item::Separator,
        Item::command(app, "Add marker at playhead", Command::AddMarker),
        Item::command(app, "Create ranges from bookmarks…", Command::RangesFromBookmarks),
        Item::command(app, "Delete marker", Command::DeleteMarker),
        Item::Separator,
        Item::command(app, "Auto captions…", Command::AutoCaptions),
    ]
}

fn view_menu(app: &AppState) -> Vec<Item> {
    let Some(e) = app.editor.as_ref() else { return vec![Item::new("Open a project first", |_, _| {}).enabled(false)] };
    vec![
        Item::command(app, "Media panel", Command::MediaPanel).checked(!e.panels.left_collapsed),
        Item::command(app, "Inspector", Command::Inspector).checked(!e.panels.right_collapsed),
        Item::command(app, "Reset layout", Command::ResetLayout),
        Item::Separator,
        Item::command(app, "Audio scrubbing", Command::AudioScrub).checked(e.audio_scrub),
        Item::command(app, "Loop playback", Command::Loop).checked(e.loop_playback),
        Item::Separator,
        Item::command(app, "Zoom to fit sequence", Command::ZoomFit),
        Item::command(app, "Zoom in", Command::ZoomIn),
        Item::command(app, "Zoom out", Command::ZoomOut),
        Item::command(app, "Fit tracks", Command::FitTracks),
        Item::command(app, "Taller tracks", Command::TallerTracks),
        Item::command(app, "Shorter tracks", Command::ShorterTracks),
    ]
}

fn help_menu(app: &AppState) -> Vec<Item> {
    vec![Item::command(app, "Keyboard shortcuts", Command::Shortcuts)]
}

fn bar_button(i: &'static rune::Icon, tip: &str, on: bool, t: &Tokens) -> El {
    el().w(32.0)
        .h(32.0)
        .radius(8.0)
        .fill(if on { t.accent_softer } else { Color::TRANSPARENT })
        .hover(t.chip_bg)
        .center()
        .no_window_drag()
        .help(tip.to_string())
        .child(icon(i, 16.0).color(t.text_secondary))
}

fn caption(i: &'static rune::Icon, size: f32, close: bool, t: &Tokens) -> El {
    el().w(42.0)
        .h(30.0)
        .radius(8.0)
        .hover(if close { hex("#d9534f") } else { t.chip_bg })
        .center()
        .no_window_drag()
        .child(icon(i, size).color(t.text_secondary))
}

pub fn title_bar(app: &AppState, t: &Tokens) -> El {
    let left = row()
        .gap(2.0)
        .pl(12.0)
        .center_y()
        .child(
            el().w(22.0)
                .h(22.0)
                .mr(10.0)
                .radius(6.0)
                .fill(hex("#7a3596"))
                .center()
                .child(text("f").font(font(14.0, 800.0)).color(Color::WHITE)),
        )
        .child(menu_button("File", t, file_menu))
        .child(menu_button("Edit", t, edit_menu))
        .child(menu_button("View", t, view_menu))
        .child(menu_button("Help", t, help_menu));

    let center = match (app.route, app.editor.as_ref()) {
        (Route::Editor, Some(e)) => row()
            .gap(10.0)
            .center_y()
            .inert()
            .child(text(e.project.name.clone()).font(font(13.0, 600.0)).color(t.text_primary).truncate().max_w(360.0))
            .child(text(e.save.text(e.is_dirty())).font(font(12.0, 400.0)).color(t.text_muted)),
        _ => row().inert().child(text("fig").font(font(13.0, 400.0)).color(t.text_muted)),
    };

    let mut right = row().gap(4.0).pr(6.0).center_y();
    if let (Route::Editor, Some(e)) = (app.route, app.editor.as_ref()) {
        let active = app.jobs.active();
        right = right
            .child(bar_button(&icons::LAYOUT_GRID, "All projects (Ctrl+W)", false, t).on_press_tx(|s: &mut AppState, tx: &mut Tx| s.close_project(tx)))
            .child(
                bar_button(&icons::LIST_CHECKS, "Export jobs", false, t)
                    .children((active > 0).then(|| {
                        el().absolute(20.0, 3.0)
                            .min_w(14.0)
                            .h(14.0)
                            .px(3.0)
                            .radius(7.0)
                            .fill(t.accent)
                            .center()
                            .child(text(active.to_string()).font(font(9.0, 700.0)).color(t.on_accent))
                    }))
                    .on_press(|s: &mut AppState| s.dialog = Some(crate::dialogs::Dialog::Jobs)),
            )
            .child(bar_button(&icons::PANEL_LEFT, "Media panel (Ctrl+1)", !e.panels.left_collapsed, t).on_press_tx(|s: &mut AppState, tx: &mut Tx| {
                crate::commands::actions::run(s, Command::MediaPanel, tx)
            }))
            .child(bar_button(&icons::PANEL_RIGHT, "Inspector (Ctrl+2)", !e.panels.right_collapsed, t).on_press_tx(|s: &mut AppState, tx: &mut Tx| {
                crate::commands::actions::run(s, Command::Inspector, tx)
            }))
            .child(
                row()
                    .h(32.0)
                    .px(14.0)
                    .gap(6.0)
                    .ml(4.0)
                    .mr(8.0)
                    .radius(9.0)
                    .fill(t.accent)
                    .hover(hex("#8bb8ff"))
                    .center()
                    .no_window_drag()
                    .child(icon(&icons::UPLOAD, 14.0).color(t.on_accent))
                    .child(text("Export").font(font(13.0, 600.0)).color(t.on_accent))
                    .on_press_tx(|s: &mut AppState, tx: &mut Tx| crate::commands::actions::run(s, Command::Export, tx)),
            );
    }
    right = right
        .child(caption(&icons::MINUS, 14.0, false, t).on_press_tx(|_: &mut AppState, tx: &mut Tx| tx.minimize()))
        .child(caption(&icons::SQUARE, 12.0, false, t).on_press_tx(|_: &mut AppState, tx: &mut Tx| tx.toggle_maximized()))
        .child(caption(&icons::X, 14.0, true, t).on_press_tx(|s: &mut AppState, tx: &mut Tx| crate::commands::actions::run(s, crate::commands::Command::Exit, tx)));

    row()
        .key("titlebar")
        .w_full()
        .h(crate::ui::theme::TITLEBAR_H)
        .center_y()
        // pressing empty space moves the window, and a double press zooms it as the system says; a press handler of
        // its own here would make the bar a control that keeps its presses, so the window wouldn't move
        .window_drag()
        .child(el().grow(1.0).min_w(0.0).child(left))
        .child(center)
        .child(el().grow(1.0).min_w(0.0).child(row().w_full().justify_end().child(right)))
}
