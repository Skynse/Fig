//! The timeline panel's top: timeline tabs, then the toolbar (frame rate and shape, edit tools, zoom).

use std::rc::Rc;

use fig_timeline::{FrameRate, FrameShape, Timeline};
use rune::{El, Tx, col, el, icon, row, text};

use crate::actions;
use crate::app::AppState;
use crate::commands::Command;
use crate::editor::{Editor, Tool};
use crate::icons;
use crate::menu::Item;
use crate::theme::{Tokens, font};
use crate::widgets::{self, Kind};

pub fn panel(app: &AppState, e: &Editor, t: &Tokens) -> El {
    col()
        .w_full()
        .h_full()
        .padding(10.0)
        .gap(8.0)
        .child(tabs(e, t))
        .child(toolbar(app, e, t))
        .child(super::draw::canvas_view(app, e, t))
}

fn timeline_name(tl: &Timeline, index: usize) -> String {
    tl.name.clone().filter(|n| !n.trim().is_empty()).unwrap_or_else(|| format!("Timeline {}", index + 1))
}

fn tabs(e: &Editor, t: &Tokens) -> El {
    let mut r = row().h(28.0).gap(4.0).center_y();
    for (i, tl) in e.project.timelines.iter().enumerate() {
        let active = i == e.timeline_index;
        let name = if active { timeline_name(e.doc(), i) } else { timeline_name(tl, i) };
        r = r.child(
            el().key(format!("timeline.tab.{i}"))
                .h(26.0)
                .px(9.0)
                .radius(8.0)
                .fill(if active { t.chip_bg } else { rune::Color::TRANSPARENT })
                .hover(t.chip_bg)
                .center()
                .child(text(name).font(font(13.0, if active { 600.0 } else { 400.0 })).color(if active { t.text_primary } else { t.text_secondary }))
                .on_press(move |s: &mut AppState| {
                    let prefs = s.preferences.clone();
                    if let Some(e) = s.editor.as_mut() {
                        e.switch_timeline(i, &prefs);
                    }
                    crate::editor::seek(s, 0.0, false);
                })
                .on_double_press(move |s: &mut AppState| rename_timeline(s, i))
                .on_secondary_press(move |s: &mut AppState, p: rune::PointerPress, _tx: &mut Tx| {
                    let items = vec![
                        Item::new("Rename…", move |s, _| rename_timeline(s, i)),
                        Item::new("Duplicate", move |s, _| duplicate_timeline(s, i)),
                        Item::Separator,
                        Item::new("Delete", move |s, _| delete_timeline(s, i)).danger(),
                    ];
                    crate::menu::open(s, crate::menu::Popup::at(p.x, p.y, items));
                }),
        );
    }
    r.child(
        widgets::icon_button(&icons::PLUS, 14.0, 26.0, t)
            .help("New timeline (right-click a tab to duplicate one)")
            .on_press(|s: &mut AppState| new_timeline(s)),
    )
}

fn rename_timeline(app: &mut AppState, i: usize) {
    let Some(e) = app.editor.as_ref() else { return };
    let current = if i == e.timeline_index { timeline_name(e.doc(), i) } else { timeline_name(&e.project.timelines[i], i) };
    crate::dialogs::prompt(app, "Rename timeline", "Name", &current, "Rename", move |s, name| {
        let Some(e) = s.editor.as_mut() else { return };
        let name = Some(name.trim().to_string()).filter(|n| !n.is_empty());
        if i == e.timeline_index {
            e.ed.rename_timeline(name);
        } else if let Some(tl) = e.project.timelines.get_mut(i) {
            tl.name = name;
            e.media_rev += 1;
        }
    });
}

fn new_timeline(app: &mut AppState) {
    let prefs = app.preferences.clone();
    let Some(e) = app.editor.as_mut() else { return };
    e.commit_to_project();
    let mut tl = Timeline::default();
    tl.rate = e.doc().rate;
    tl.shape = e.doc().shape;
    tl.tracks = vec![fig_timeline::Track::new(fig_timeline::TrackKind::Video), fig_timeline::Track::new(fig_timeline::TrackKind::Audio)];
    e.project.timelines.push(tl);
    let i = e.project.timelines.len() - 1;
    e.switch_timeline(i, &prefs);
    e.media_rev += 1;
}

fn duplicate_timeline(app: &mut AppState, i: usize) {
    let prefs = app.preferences.clone();
    let Some(e) = app.editor.as_mut() else { return };
    e.commit_to_project();
    let mut copy = fig_project::ProjectStore::copy_timeline(&e.project.timelines[i]);
    copy.name = Some(format!("{} copy", timeline_name(&e.project.timelines[i], i)));
    e.project.timelines.insert(i + 1, copy);
    e.timeline_index = if e.timeline_index > i { e.timeline_index + 1 } else { e.timeline_index };
    e.switch_timeline(i + 1, &prefs);
    e.media_rev += 1;
}

fn delete_timeline(app: &mut AppState, i: usize) {
    let prefs = app.preferences.clone();
    let Some(e) = app.editor.as_mut() else { return };
    if e.project.timelines.len() <= 1 {
        e.say("A project keeps at least one timeline");
        return;
    }
    e.commit_to_project();
    let current = e.timeline_index;
    let next = if i == current { if i > 0 { i - 1 } else { 1 } } else { current };
    // switch away first, so the one going isn't the one open
    e.switch_timeline(next, &prefs);
    e.project.timelines.remove(i);
    if e.timeline_index > i {
        e.timeline_index -= 1;
    }
    e.media_rev += 1;
}

const RATES: [(i32, i32); 8] = [(24000, 1001), (24, 1), (25, 1), (30000, 1001), (30, 1), (50, 1), (60000, 1001), (60, 1)];

fn tool(i: &'static rune::Icon, tip: &str, on: bool, t: &Tokens) -> El {
    widgets::toggle_button(i, on, 34.0, t).help(tip.to_string())
}

fn cmd_button(app: &AppState, i: &'static rune::Icon, tip: &str, command: Command, enabled: bool, t: &Tokens) -> El {
    let hint = crate::commands::shortcut_label(command, &app.preferences.keyboard.shortcuts);
    let tip = if hint.is_empty() { tip.to_string() } else { format!("{tip} ({hint})") };
    let b = widgets::icon_button(i, 18.0, 34.0, t).help(tip);
    if enabled {
        b.on_press_tx(move |s: &mut AppState, tx: &mut Tx| actions::run(s, command, tx))
    } else {
        b.opacity(0.35)
    }
}

fn toolbar(app: &AppState, e: &Editor, t: &Tokens) -> El {
    let doc = e.doc();
    let has_sel = !e.ed.selection.selected_clip_ids().is_empty();
    let left = row()
        .gap(6.0)
        .center_y()
        .child(
            widgets::dropdown("tl.rate", doc.rate.label(), t).w(108.0).on_press(|s: &mut AppState| {
                let items = RATES
                    .iter()
                    .map(|&(n, d)| {
                        let r = FrameRate::new(n, d);
                        let current = s.editor.as_ref().is_some_and(|e| e.doc().rate == r);
                        Item::new(r.label(), move |s, _| {
                            if let Some(e) = s.editor.as_mut() {
                                e.ed.edit(|ed| ed.set_rate(r));
                            }
                        })
                        .checked(current)
                    })
                    .collect();
                crate::menu::open_below(s, "tl.rate", items);
            }),
        )
        .child(
            widgets::dropdown("tl.shape", doc.shape.label(), t).w(84.0).help("The picture's shape").on_press(|s: &mut AppState| {
                let items = FrameShape::ALL
                    .iter()
                    .map(|&shape| {
                        let current = s.editor.as_ref().is_some_and(|e| e.doc().shape == shape);
                        Item::new(shape.label(), move |s, _| {
                            if let Some(e) = s.editor.as_mut() {
                                e.ed.edit(|ed| ed.set_frame_shape(shape));
                            }
                        })
                        .checked(current)
                    })
                    .collect();
                crate::menu::open_below(s, "tl.shape", items);
            }),
        );
    let mut middle = row()
        .gap(2.0)
        .center_y()
        .child(cmd_button(app, &icons::UNDO, "Undo", Command::Undo, e.ed.can_undo(), t))
        .child(cmd_button(app, &icons::REDO, "Redo", Command::Redo, e.ed.can_redo(), t))
        .child(el().w(12.0))
        .child(tool(&icons::MOUSE_POINTER_2, "Select tool (A)", e.tool == Tool::Select, t).on_press(|s: &mut AppState| set_tool(s, Tool::Select)))
        .child(tool(&icons::SLICE, "Blade tool (B): click a clip to cut it", e.tool == Tool::Blade, t).on_press(|s: &mut AppState| set_tool(s, Tool::Blade)))
        .child(cmd_button(app, &icons::SCISSORS, "Split at playhead", Command::Split, true, t));
    if has_sel {
        middle = middle
            .child(cmd_button(app, &icons::ARROW_LEFT_TO_LINE, "Delete before the playhead", Command::TrimStart, true, t))
            .child(cmd_button(app, &icons::ARROW_RIGHT_TO_LINE, "Delete after the playhead", Command::TrimEnd, true, t))
            .child(cmd_button(app, &icons::SNOWFLAKE, "Freeze frame", Command::Freeze, true, t))
            .child(cmd_button(app, &icons::TRASH_2, "Delete", Command::Delete, true, t));
    }
    middle = middle
        .child(el().w(12.0))
        .child(
            tool(&icons::PANEL_BOTTOM, "Main track magnet: clips on the main track stay end to end", doc.main_track_magnet, t)
                .on_press_tx(|s: &mut AppState, tx: &mut Tx| actions::run(s, Command::Magnet, tx)),
        )
        .child(
            tool(&icons::MAGNET, "Snapping (N)", e.ed.snap.magnetic, t).on_press_tx(|s: &mut AppState, tx: &mut Tx| actions::run(s, Command::Snapping, tx)),
        );
    let zoom = e.tl.zoom_slider();
    let right = row()
        .gap(4.0)
        .center_y()
        .child(widgets::button("Fit sequence", Kind::Normal, t).h(28.0).on_press_tx(|s: &mut AppState, tx: &mut Tx| actions::run(s, Command::ZoomFit, tx)))
        .child(widgets::icon_button(&icons::ZOOM_OUT, 15.0, 30.0, t).help("Zoom out (Ctrl+-)").on_press_tx(|s: &mut AppState, tx: &mut Tx| actions::run(s, Command::ZoomOut, tx)))
        .child(
            el().w(110.0).child(widgets::slider(
                "tl.zoom",
                zoom,
                0.0,
                1000.0,
                f64::from((100f32).ln() / 2000f32.ln() * 1000.0),
                Rc::new(|s: &mut AppState, v, _| {
                    if let Some(e) = s.editor.as_mut() {
                        let p = e.playhead;
                        e.tl.set_zoom_slider(v, p);
                    }
                }),
                t,
            )),
        )
        .child(text(format!("{:.0}%", e.tl.pps)).font(font(11.0, 400.0)).color(t.text_muted).min_w(44.0))
        .child(widgets::icon_button(&icons::ZOOM_IN, 15.0, 30.0, t).help("Zoom in (Ctrl++)").on_press_tx(|s: &mut AppState, tx: &mut Tx| actions::run(s, Command::ZoomIn, tx)))
        .child(widgets::icon_button(&icons::FOLD_VERTICAL, 13.0, 30.0, t).help("Shorter tracks").on_press_tx(|s: &mut AppState, tx: &mut Tx| actions::run(s, Command::ShorterTracks, tx)))
        .child(widgets::icon_button(&icons::ROWS_3, 13.0, 30.0, t).help("Fit tracks").on_press_tx(|s: &mut AppState, tx: &mut Tx| actions::run(s, Command::FitTracks, tx)))
        .child(widgets::icon_button(&icons::UNFOLD_VERTICAL, 13.0, 30.0, t).help("Taller tracks").on_press_tx(|s: &mut AppState, tx: &mut Tx| actions::run(s, Command::TallerTracks, tx)))
        .child(
            widgets::button(crate::beats::button_label(e), Kind::Normal, t).key("tl.beats").h(28.0).help("Preview beat or kick markers, then apply them").on_press(|s: &mut AppState| {
                if let Some(e) = s.editor.as_mut() {
                    e.beats.open = !e.beats.open;
                }
            }),
        )
        .child(widgets::icon_button(&icons::ELLIPSIS, 15.0, 30.0, t).key("tl.more").on_press(|s: &mut AppState| {
            let items = vec![
                Item::command(s, "Enable / disable clip", Command::ToggleEnabled),
                Item::command(s, "Delete selected marker", Command::DeleteMarker),
                Item::new("Analyze beats for markers", |s, _| crate::actions::analyze_beats(s)),
            ];
            crate::menu::open_below(s, "tl.more", items);
        }));
    row()
        .w_full()
        .h(34.0)
        .center_y()
        .child(el().grow(1.0).min_w(0.0).child(left))
        .child(middle)
        .child(el().grow(1.0).min_w(0.0).child(row().w_full().justify_end().child(right)))
}

fn set_tool(app: &mut AppState, tool: Tool) {
    if let Some(e) = app.editor.as_mut() {
        e.tool = tool;
    }
}

#[allow(dead_code)]
fn _unused(_: &'static rune::Icon) -> El {
    icon(&icons::PLUS, 1.0)
}
