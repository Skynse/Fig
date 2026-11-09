//! The editor screen: Fig's dock layout (inventory §4) around the timeline canvas.
//!
//! `EditorView.axaml`: an 8 px inset, a bottom status bar, and a 5-column workspace
//! (library · splitter · preview · splitter · inspector) split into the monitor row and the
//! timeline. Panel widths are fractions (`WorkspaceLayout`: 0.20 / 0.22), the timeline height
//! 0.44; the 4 px splitters drag them, and a collapsed side leaves the 46 px rail / 44 px strip.
//!
//! Each panel owns its module; this one owns the frame and the splitter drags.

use rune::{Cx, Drag, El, Phase, Tx, col, el, icon, row, text};

use crate::app::AppState;
use crate::editor_state::EditorState;
use crate::icons;
use crate::library::LibraryTab;
use crate::theme::{font, Tokens, RADIUS_CONTROL, RADIUS_PANEL, SPLITTER, STATUS_H, WINDOW_PAD};

#[derive(Clone, Copy)]
enum Split {
    Left,
    Right,
    Timeline,
}

pub fn view(_app: &AppState, editor: &EditorState, t: &Tokens, _cx: &mut Cx<AppState>) -> El {
    let mut root = workspace(editor, t);
    if editor.graph_open {
        root = root.child(crate::graph::overlay(editor, t));
    }
    root
}

fn workspace(editor: &EditorState, t: &Tokens) -> El {
    let top_grow = ((1.0 - editor.timeline_frac).max(0.05) * 1000.0).max(1.0);
    let bottom_grow = (editor.timeline_frac.max(0.05) * 1000.0).max(1.0);

    let mut monitors = row().w_full().grow(top_grow).min_h(0.0);

    if editor.left_collapsed {
        monitors = monitors.child(collapsed_library(editor, t));
    } else {
        monitors = monitors.child(
            el().w_frac(editor.left_frac.clamp(0.12, 0.40))
                .h_full()
                .min_w(140.0)
                .child(crate::library::panel(editor, t)),
        );
    }
    monitors = monitors.child(splitter(t, Split::Left)).child(crate::preview::panel(editor, t));
    monitors = monitors.child(splitter(t, Split::Right));
    if editor.right_collapsed {
        monitors = monitors.child(collapsed_inspector(t));
    } else {
        monitors = monitors.child(
            el().w_frac(editor.right_frac.clamp(0.12, 0.40))
                .h_full()
                .min_w(160.0)
                .child(crate::inspector::panel(editor, t)),
        );
    }

    col()
        .key("editor.workspace")
        .w_full()
        .grow(1.0)
        .min_h(0.0)
        .padding(WINDOW_PAD)
        .child(monitors)
        .child(splitter(t, Split::Timeline))
        .child(row().w_full().grow(bottom_grow).min_h(0.0).child(timeline_panel(editor, t)))
        .child(el().w_full().h(4.0))
        .child(status_bar(editor, t))
}

fn splitter(t: &Tokens, which: Split) -> El {
    let base = match which {
        Split::Timeline => el().w_full().h(SPLITTER).min_h(SPLITTER),
        _ => el().w(SPLITTER).h_full().min_w(SPLITTER),
    };
    base.on_drag(move |s: &mut AppState, drag: &mut Drag, _tx: &mut Tx| {
        if matches!(drag.phase, Phase::Up) {
            s.save_workspace();
        }
        let editor = s.editor();
        match drag.phase {
            Phase::Down => {
                let size = rune::memo::layout_size("editor.workspace").unwrap_or([1184.0, 610.0]);
                editor.split_origin =
                    Some((editor.left_frac, editor.right_frac, editor.timeline_frac, size[0], size[1]));
            }
            Phase::Move => {
                if let Some((l0, r0, t0, width, height)) = editor.split_origin {
                    let width = width.max(1.0);
                    let height = height.max(1.0);
                    match which {
                        Split::Left => editor.left_frac = (l0 + drag.dx / width).clamp(0.12, 0.40),
                        Split::Right => editor.right_frac = (r0 - drag.dx / width).clamp(0.12, 0.40),
                        Split::Timeline => editor.timeline_frac = (t0 - drag.dy / height).clamp(0.20, 0.75),
                    }
                }
            }
            Phase::Up => editor.split_origin = None,
        }
        editor.ui_revision += 1;
    })
}

// ---------------- collapsed side panels ----------------

fn collapsed_library(editor: &EditorState, t: &Tokens) -> El {
    let mut rail = col()
        .w(46.0)
        .h_full()
        .fill(t.panel_bg)
        .radius(RADIUS_PANEL)
        .clip_rounded(RADIUS_PANEL)
        .padding(4.0)
        .gap(4.0);
    for tab in LibraryTab::ALL {
        let selected = editor.library.tab == tab;
        let mut item = el()
            .w(36.0)
            .h(36.0)
            .radius(RADIUS_CONTROL)
            .center()
            .child(icon(tab.icon(), 18.0).color(if selected { t.accent_text } else { t.text_muted }))
            .on_press(move |s: &mut AppState| {
                let editor = s.editor();
                editor.left_collapsed = false;
                editor.library.tab = tab;
                s.save_workspace();
            });
        if selected {
            item = item.fill(t.accent_soft);
        }
        rail = rail.child(item);
    }
    rail
}

fn collapsed_inspector(t: &Tokens) -> El {
    el().w(44.0)
        .h_full()
        .fill(t.panel_bg)
        .radius(RADIUS_PANEL)
        .clip_rounded(RADIUS_PANEL)
        .padding(4.0)
        .center()
        .child(
            el().w(34.0)
                .h(34.0)
                .radius(RADIUS_CONTROL)
                .center()
                .child(icon(&icons::CHEVRON_LEFT, 16.0).color(t.text_muted))
                .on_press(|s: &mut AppState| {
                    s.editor().right_collapsed = false;
                    s.save_workspace();
                }),
        )
}

// ---------------- bottom: the timeline panel ----------------

fn timeline_panel(editor: &EditorState, t: &Tokens) -> El {
    col()
        .w_full()
        .h_full()
        .fill(t.panel_bg)
        .radius(RADIUS_PANEL)
        .clip_rounded(RADIUS_PANEL)
        .padding(12.0)
        .gap(4.0)
        .child(
            row()
                .w_full()
                .h(24.0)
                .center_y()
                .child(text("Timeline 1").font(font(13.0, 600.0)).color(t.text_primary))
                .child(el().w(8.0))
                .child(
                    crate::widgets::icon_button(&icons::PLUS, 14.0, t.text_muted)
                        .on_press(|s: &mut AppState| {
                            let editor = s.editor();
                            editor.editor.add_track(fig_timeline::TrackKind::Video);
                            editor.editor.selection.clear();
                            editor.save = crate::editor_state::SaveState::Dirty;
                        }),
                ),
        )
        .child(toolbar(editor, t))
        .child(
            crate::timeline::view(editor, t)
                .w_full()
                .grow(1.0)
                .min_h(0.0)
                .clip_rounded(0.0),
        )
}

fn toolbar(editor: &EditorState, t: &Tokens) -> El {
    let undos = if editor.editor.can_undo() { t.text_secondary } else { t.text_faint };
    let redos = if editor.editor.can_redo() { t.text_secondary } else { t.text_faint };
    let blade = editor.tool == crate::editor_state::Tool::Blade;

    let tools = row()
        .gap(4.0)
        .center_y()
        .child(crate::widgets::chip("30 fps", t))
        .child(crate::widgets::chip("Auto", t))
        .child(el().w(10.0))
        .child(crate::widgets::icon_button(&icons::UNDO, 16.0, undos).on_press(|s: &mut AppState| {
            s.editor().editor.undo();
        }))
        .child(crate::widgets::icon_button(&icons::REDO, 16.0, redos).on_press(|s: &mut AppState| {
            s.editor().editor.redo();
        }))
        .child(el().w(10.0))
        .child(
            crate::widgets::tool(&icons::POINTER, if blade { t.text_muted } else { t.accent_text }, false)
                .on_press(|s: &mut AppState| s.editor().tool = crate::editor_state::Tool::Select),
        )
        .child(
            crate::widgets::tool(&icons::SLICE, if blade { t.accent_text } else { t.text_muted }, false)
                .on_press(|s: &mut AppState| s.editor().tool = crate::editor_state::Tool::Blade),
        )
        .child(crate::widgets::tool(&icons::SCISSORS, t.text_muted, false))
        .child(
            crate::widgets::tool(&icons::DIAMOND, t.text_muted, false)
                .on_press(|s: &mut AppState| s.preferences_page = 4),
        );

    let right = row()
        .gap(8.0)
        .center_y()
        .child(
            crate::widgets::chip("Fit sequence", t).on_press(|s: &mut AppState| {
                let duration = s.editor().duration_sec();
                let width = rune::memo::layout_size("editor.workspace")
                    .map(|size| size[0] - crate::timeline::HEADER_W)
                    .unwrap_or(1000.0);
                s.editor().timeline.fit(duration, width);
            }),
        )
        .child(crate::widgets::icon_button(&icons::MINUS, 12.0, t.text_muted).on_press(|s: &mut AppState| {
            s.editor().timeline.zoom(1.0 / 1.2);
        }))
        .child(el().w(80.0).h(3.0).radius(2.0).fill(t.accent))
        .child(
            text(format!("{}%", (editor.timeline.pps / 100.0 * 100.0).round() as i32))
                .font(font(11.0, 400.0))
                .color(t.text_secondary),
        )
        .child(crate::widgets::icon_button(&icons::PLUS, 12.0, t.text_muted).on_press(|s: &mut AppState| {
            s.editor().timeline.zoom(1.2);
        }))
        .child(icon(&icons::MAXIMIZE, 14.0).color(t.text_muted))
        .child(icon(&icons::LIST, 14.0).color(t.text_muted))
        .child(crate::widgets::chip("Beats", t))
        .child(
            el().w(28.0)
                .h(26.0)
                .radius(RADIUS_CONTROL)
                .fill(t.card_bg)
                .center()
                .child(text("…").font(font(12.0, 500.0)).color(t.text_secondary)),
        );

    row().w_full().h(30.0).center_y().child(tools).child(el().grow(1.0)).child(right)
}

fn status_bar(editor: &EditorState, t: &Tokens) -> El {
    row()
        .w_full()
        .h(STATUS_H)
        .center_y()
        .child(el().w(4.0))
        .child(text(selection_summary(editor)).font(font(11.0, 400.0)).color(t.text_muted))
        .child(el().grow(1.0))
        .child(
            text("Click a clip to select it · Space plays and pauses")
                .font(font(11.0, 400.0))
                .color(t.text_muted),
        )
}

fn selection_summary(editor: &EditorState) -> String {
    let selection = &editor.editor.selection;
    if selection.is_empty() {
        return "Nothing selected".into();
    }
    match selection.primary() {
        Some(key) if selection.items().len() == 1 => match editor.editor.resolve(key) {
            Some(selected) => selected.display_name(),
            None => "1 item selected".into(),
        },
        Some(_) => format!("{} items selected", selection.items().len()),
        None => "Nothing selected".into(),
    }
}
