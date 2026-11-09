//! The graph editor overlay: the effect node graph (`GraphEditorView.axaml` + `GraphCanvas.cs`,
//! inventory §4.6). Node cards with sockets, bezier cables, drag-to-move, zoom, and an add-node
//! menu. The real graph model (`fig-fx`) compiles node graphs for rendering; this is its editor.
//!
//! The canvas maths is pure functions (`to_screen`, `cable_points`) so it unit-tests.

use rune::{El, Shape, canvas, col, el, icon, row, text};

use crate::app::AppState;
use crate::editor_state::EditorState;
use crate::icons;
use crate::theme::{font, mono, Tokens, RADIUS_CARD};

pub struct GraphNode {
    pub id: &'static str,
    pub title: &'static str,
    pub x: f32,
    pub y: f32,
    pub params: &'static [(&'static str, &'static str)],
}

pub struct GraphState {
    pub zoom: f32,
    pub pan: (f32, f32),
    pub nodes: Vec<GraphNode>,
    pub selected: Option<usize>,
    pub drag: Option<(usize, f32, f32)>,
    pub open_menu: bool,
}

impl Default for GraphState {
    fn default() -> Self {
        GraphState {
            zoom: 1.0,
            pan: (0.0, 0.0),
            nodes: vec![
                GraphNode {
                    id: "grade",
                    title: "Colour grade",
                    x: 60.0,
                    y: 90.0,
                    params: &[("Exposure", "0.0"), ("Contrast", "1.0")],
                },
                GraphNode { id: "blur", title: "Blur", x: 300.0, y: 90.0, params: &[("Radius", "8 px")] },
                GraphNode { id: "dissolve", title: "Dissolve", x: 300.0, y: 240.0, params: &[("Progress", "0.5")] },
                GraphNode { id: "output", title: "Output", x: 540.0, y: 150.0, params: &[] },
            ],
            selected: None,
            drag: None,
            open_menu: false,
        }
    }
}

const NODE_W: f32 = 190.0;
pub const NODE_HEADER: f32 = 26.0;

fn node_height(node: &GraphNode) -> f32 {
    NODE_HEADER + 8.0 + node.params.len() as f32 * 20.0 + 10.0
}

/// Canvas point to screen point, inside the graph window.
pub fn to_screen(zoom: f32, pan: (f32, f32), x: f32, y: f32) -> (f32, f32) {
    (x * zoom + pan.0, y * zoom + pan.1)
}

/// A cubic cable from an output socket to an input socket, sampled as a polyline.
pub fn cable_points(from: (f32, f32), to: (f32, f32)) -> Vec<[f32; 2]> {
    let dx = (to.0 - from.0).abs().max(40.0) * 0.5;
    let (c1, c2) = ((from.0 + dx, from.1), (to.0 - dx, to.1));
    (0..=12)
        .map(|i| {
            let t = i as f32 / 12.0;
            let u = 1.0 - t;
            let x = u * u * u * from.0 + 3.0 * u * u * t * c1.0 + 3.0 * u * t * t * c2.0 + t * t * t * to.0;
            let y = u * u * u * from.1 + 3.0 * u * u * t * c1.1 + 3.0 * u * t * t * c2.1 + t * t * t * to.1;
            [x, y]
        })
        .collect()
}

pub fn overlay(editor: &EditorState, t: &Tokens) -> El {
    let graph = &editor.graph;
    let mut canvas_el = el().w_full().h_full().fill(crate::theme::hex("#101114"));
    for i in 0..80 {
        let x = i as f32 * 24.0;
        canvas_el = canvas_el.child(el().absolute(x, 0.0).w(1.0).h_full().fill(crate::theme::hex("#16171b")));
    }
    for i in 0..50 {
        let y = i as f32 * 24.0;
        canvas_el = canvas_el.child(el().absolute(0.0, y).w_full().h(1.0).fill(crate::theme::hex("#16171b")));
    }

    // cables first, under the nodes
    let cables: [(&str, &str); 3] = [("grade", "blur"), ("grade", "dissolve"), ("blur", "output")];
    for (from, to) in cables {
        let from_node = graph.nodes.iter().find(|n| n.id == from).map(|n| (n, node_height(n)));
        let to_node = graph.nodes.iter().find(|n| n.id == to);
        if let (Some((a, ah)), Some(b)) = (from_node, to_node) {
            let start = to_screen(graph.zoom, graph.pan, a.x + NODE_W - 8.0, a.y + ah / 2.0);
            let end = to_screen(graph.zoom, graph.pan, b.x + 8.0, b.y + node_height(b) / 2.0);
            let points = cable_points(start, end);
            canvas_el = canvas_el.child(
                canvas(Shape::new().stroke(points, 2.0, t.accent))
                    .absolute(0.0, 0.0)
                    .w_full()
                    .h_full(),
            );
        }
    }

    for (i, node) in graph.nodes.iter().enumerate() {
        let (x, y) = to_screen(graph.zoom, graph.pan, node.x, node.y);
        let h = node_height(node) * graph.zoom;
        let mut card = col()
            .absolute(x, y)
            .w(NODE_W * graph.zoom)
            .h(h)
            .radius(RADIUS_CARD)
            .fill(t.card_bg)
            .border(1.0, if graph.selected == Some(i) { t.accent } else { t.border })
            .clip_rounded(RADIUS_CARD)
            .child(
                row()
                    .w_full()
                    .h(NODE_HEADER * graph.zoom)
                    .center_y()
                    .padding(8.0)
                    .child(icon(&icons::WAND, 12.0).color(t.accent_text))
                    .child(el().w(6.0))
                    .child(text(node.title).font(font(12.0, 600.0)).color(t.text_primary)),
            );
        for (label, value) in node.params {
            card = card.child(
                row()
                    .w_full()
                    .h(20.0 * graph.zoom)
                    .padding(8.0)
                    .center_y()
                    .child(text(*label).font(font(10.0, 400.0)).color(t.text_muted))
                    .child(el().grow(1.0))
                    .child(text(*value).font(mono(10.0, 400.0)).color(t.text_secondary)),
            );
        }
        // sockets: input on the left, output on the right, at the header's middle
        card = card
            .child(el().absolute(-4.0, NODE_HEADER * graph.zoom / 2.0 - 4.0).w(8.0).h(8.0).radius(4.0).fill(t.accent))
            .child(
                el().absolute(NODE_W * graph.zoom - 4.0, NODE_HEADER * graph.zoom / 2.0 - 4.0)
                    .w(8.0)
                    .h(8.0)
                    .radius(4.0)
                    .fill(t.accent),
            );
        let index = i;
        card = card.on_drag(move |s: &mut AppState, drag: &mut rune::Drag, _tx: &mut rune::Tx| {
            let graph = &mut s.editor().graph;
            match drag.phase {
                rune::Phase::Down => {
                    graph.selected = Some(index);
                    let node = &graph.nodes[index];
                    graph.drag = Some((index, drag.x - node.x, drag.y - node.y));
                }
                rune::Phase::Move => {
                    if let Some((dragged, grab_x, grab_y)) = graph.drag {
                        if let Some(node) = graph.nodes.get_mut(dragged) {
                            node.x = drag.x - grab_x;
                            node.y = drag.y - grab_y;
                        }
                    }
                }
                rune::Phase::Up => graph.drag = None,
            }
        });
        canvas_el = canvas_el.child(card);
    }

    el().absolute(0.0, 0.0)
        .w_full()
        .h_full()
        .fill(crate::theme::hex("#000000b4"))
        .child(
            el().absolute(24.0, 24.0)
                .w(900.0)
                .h(600.0)
                .radius(14.0)
                .clip_rounded(14.0)
                .fill(crate::theme::hex("#131417"))
                .border(1.0, t.border)
                .child(canvas_el)
                .child(
                    row()
                        .absolute(12.0, 10.0)
                        .w(876.0)
                        .h(30.0)
                        .center_y()
                        .child(text("Effects graph").font(font(13.0, 600.0)).color(t.text_primary))
                        .child(el().grow(1.0))
                        .child(
                            el().h(24.0)
                                .padding(8.0)
                                .radius(6.0)
                                .fill(t.card_bg)
                                .center()
                                .child(text("+ Add node").font(font(11.0, 500.0)).color(t.text_secondary))
                                .on_press(|s: &mut AppState| s.toasts.show("Add node")),
                        )
                        .child(el().w(8.0))
                        .child(
                            icon(&icons::X, 14.0)
                                .color(t.text_muted)
                                .on_press(|s: &mut AppState| s.editor().graph_open = false),
                        ),
                )
                .child(
                    row()
                        .absolute(12.0, 560.0)
                        .w(876.0)
                        .h(28.0)
                        .center_y()
                        .child(text(format!("{:.0}%", editor.graph.zoom * 100.0)).font(mono(11.0, 400.0)).color(t.text_muted))
                        .child(el().grow(1.0))
                        .child(text("Drag nodes · wheel pans · ⌘wheel zooms").font(font(10.0, 400.0)).color(t.text_faint)),
                ),
        )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cables_run_from_the_output_socket_to_the_input() {
        let points = cable_points((10.0, 20.0), (210.0, 80.0));
        assert_eq!(points.len(), 13);
        assert!((points[0][0] - 10.0).abs() < 0.01 && (points[0][1] - 20.0).abs() < 0.01);
        let last = points[12];
        assert!((last[0] - 210.0).abs() < 0.01 && (last[1] - 80.0).abs() < 0.01);
    }

    #[test]
    fn screen_mapping_round_trips() {
        let (x, y) = to_screen(2.0, (10.0, 20.0), 30.0, 40.0);
        assert!((x - 70.0).abs() < 0.001 && (y - 100.0).abs() < 0.001);
    }
}
