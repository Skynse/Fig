//! The editor's layout (`EditorView.axaml`): library, preview and inspector across the top, the timeline below,
//! splitters between them, and the status bar.

use rune::{Cursor, Cx, Drag, El, Phase, Tx, col, el, row, text};

use super::Editor;
use crate::app::AppState;
use crate::ui::theme::{RADIUS_PANEL, Tokens, font};

const SPLIT: f32 = 6.0;

pub fn view(app: &AppState, e: &Editor, t: &Tokens, cx: &mut Cx<AppState>) -> El {
    if let Some(graph) = e.graph.as_ref() {
        return crate::editor::graph::view(app, e, graph, t);
    }
    let p = e.panels;
    let mut top = row().w_full().grow(1.0 - p.timeline).min_h(220.0);
    top = if p.left_collapsed {
        top.child(panel(crate::editor::library::rail(e, t)).w(46.0).h_full())
    } else {
        top.child(panel(crate::editor::library::view(app, e, t)).grow(p.left).min_w(220.0).h_full())
            .child(splitter(true, |s, d| {
                let w = s.window_size.0.max(1.0);
                if let Some(e) = s.editor.as_mut() {
                    let (l0, _, _) = e.panels.drag0.unwrap_or((e.panels.left, e.panels.right, e.panels.timeline));
                    e.panels.left = (l0 + d.dx / w).clamp(0.12, 0.40);
                }
            }))
    };
    // the growths across the row must add up to 1: below that, the row leaves the rest of its width empty, so a
    // collapsed side's share goes to the preview
    let sides = if p.left_collapsed { 0.0 } else { p.left } + if p.right_collapsed { 0.0 } else { p.right };
    top = top.child(panel(crate::editor::preview::view(app, e, t, cx)).grow((1.0 - sides).max(0.2)).min_w(360.0).h_full());
    top = if p.right_collapsed {
        top.child(el().w(SPLIT)).child(panel(crate::editor::inspector::collapsed(t)).w(44.0).h_full())
    } else {
        top.child(splitter(true, |s, d| {
            let w = s.window_size.0.max(1.0);
            if let Some(e) = s.editor.as_mut() {
                let (_, r0, _) = e.panels.drag0.unwrap_or((e.panels.left, e.panels.right, e.panels.timeline));
                e.panels.right = (r0 - d.dx / w).clamp(0.12, 0.40);
            }
        }))
        .child(panel(crate::editor::inspector::view(app, e, t)).grow(p.right).min_w(260.0).h_full())
    };
    col()
        .w_full()
        .grow(1.0)
        .min_h(0.0)
        .padding(8.0)
        .child(top)
        .child(splitter(false, |s, d| {
            let h = (s.window_size.1 - 46.0).max(1.0);
            if let Some(e) = s.editor.as_mut() {
                let (_, _, t0) = e.panels.drag0.unwrap_or((e.panels.left, e.panels.right, e.panels.timeline));
                e.panels.timeline = (t0 - d.dy / h).clamp(0.20, 0.75);
            }
        }))
        .child(panel(crate::timeline::toolbar::panel(app, e, t)).w_full().grow(p.timeline).min_h(240.0))
        .child(status_bar(e, t))
}

fn panel(content: El) -> El {
    let t = Tokens::dark();
    el().fill(t.panel_bg).radius(RADIUS_PANEL).clip_rounded(RADIUS_PANEL).min_h(0.0).child(content)
}

fn splitter(vertical: bool, on: fn(&mut AppState, &Drag)) -> El {
    let base = if vertical { el().w(SPLIT).h_full().cursor(Cursor::ResizeColumn) } else { el().w_full().h(SPLIT).cursor(Cursor::ResizeRow) };
    base.help(if vertical { "Drag to resize the panel" } else { "Drag to resize the timeline" }).on_drag(move |s: &mut AppState, d: &mut Drag, tx: &mut Tx| {
        if let Some(e) = s.editor.as_mut()
            && d.phase == Phase::Down {
                e.panels.drag0 = Some((e.panels.left, e.panels.right, e.panels.timeline));
            }
        on(s, d);
        if d.phase == Phase::Up {
            if let Some(e) = s.editor.as_mut() {
                e.panels.drag0 = None;
            }
            s.save_workspace();
        }
        tx.instant();
    })
}

fn status_bar(e: &Editor, t: &Tokens) -> El {
    let (summary, hint) = e.ed.describe_selection();
    let hint = e.hint.as_ref().map_or(hint, |(h, _)| h.clone());
    row()
        .w_full()
        .h(22.0)
        .px(4.0)
        .pt(4.0)
        .center_y()
        .child(text(summary).font(font(11.0, 400.0)).color(t.text_muted).grow(1.0).truncate())
        .child(text(hint).font(font(11.0, 400.0)).color(t.text_muted).truncate())
}
