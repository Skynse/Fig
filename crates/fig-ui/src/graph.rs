//! The graph editor (`GraphEditorView`): a custom effect built from primitive nodes wired together, ending in one
//! Output. Drag a node to move it, drag from an output port to an input to wire them, drag empty space to pan;
//! the right panel edits the selected node's settings and the controls the effect shows on a clip.

use fig_timeline::{EffectGraph, GraphEdge, GraphNode, ParamKind, ParamValue};
use rune::{Color, Cursor, Drag, El, KeyEvent, Phase, Shape, TextEdit, Tx, canvas, col, el, row, text, text_input};

use crate::app::AppState;
use crate::editor::Editor;
use crate::menu::Item;
use crate::theme::{Tokens, font, hex};
use crate::widgets::{self, Kind};

const NODE_W: f32 = 176.0;
const HEADER_H: f32 = 26.0;
const ROW_H: f32 = 22.0;
const PORT_R: f32 = 5.0;

pub struct GraphEditor {
    pub graph: EffectGraph,
    pub name: TextEdit,
    pub pan: (f32, f32),
    pub selected: Option<String>,
    /// A wire being dragged from (node, output port) to the pointer.
    pub wiring: Option<(String, String, f32, f32)>,
    pub drag0: Option<(f64, f64)>,
    pub message: Option<String>,
}

/// Opens a graph effect (`graph:<id>`), or a new one.
pub fn open(app: &mut AppState, type_id: Option<&str>) {
    let Some(e) = app.editor.as_mut() else { return };
    let graph = type_id
        .and_then(|t| t.strip_prefix("graph:"))
        .and_then(|id| e.graphs.find(id).cloned())
        .unwrap_or_else(|| {
            let mut g = EffectGraph { name: "New effect".into(), ..Default::default() };
            let input = GraphNode { primitive_id: "input".into(), x: 40.0, y: 120.0, ..Default::default() };
            let output = GraphNode { primitive_id: "output".into(), x: 460.0, y: 120.0, ..Default::default() };
            g.edges.push(GraphEdge { from_node_id: input.id.clone(), from_port: "image".into(), to_node_id: output.id.clone(), to_port: "image".into() });
            g.nodes = vec![input, output];
            g
        });
    let name = TextEdit::new(graph.name.clone());
    e.graph = Some(GraphEditor { graph, name, pan: (0.0, 0.0), selected: None, wiring: None, drag0: None, message: None });
}

fn ge(app: &mut AppState) -> Option<&mut GraphEditor> {
    app.editor.as_mut()?.graph.as_mut()
}

fn ports_of(node: &GraphNode) -> (Vec<(&'static str, &'static str)>, Vec<(&'static str, &'static str)>) {
    match fig_fx::graph::find_primitive(&node.primitive_id) {
        Some(p) => (p.inputs.iter().map(|i| (i.id, i.label)).collect(), p.outputs.iter().map(|o| (o.id, o.label)).collect()),
        None => (Vec::new(), Vec::new()),
    }
}

fn node_title(node: &GraphNode) -> String {
    if let Some(t) = &node.effect_type_id {
        return fig_fx::effects::find(t).map_or(t.clone(), |e| e.display_name.clone());
    }
    fig_fx::graph::find_primitive(&node.primitive_id).map_or(node.primitive_id.clone(), |p| p.display_name.to_string())
}

fn node_height(node: &GraphNode) -> f32 {
    let (i, o) = ports_of(node);
    HEADER_H + 8.0 + (i.len().max(o.len()).max(1) as f32) * ROW_H + 6.0
}

fn port_pos(g: &GraphEditor, node: &GraphNode, port: &str, output: bool) -> (f32, f32) {
    let (ins, outs) = ports_of(node);
    let list = if output { outs } else { ins };
    let i = list.iter().position(|(id, _)| *id == port).unwrap_or(0);
    let x = node.x as f32 + g.pan.0 + if output { NODE_W } else { 0.0 };
    let y = node.y as f32 + g.pan.1 + HEADER_H + 8.0 + i as f32 * ROW_H + ROW_H / 2.0;
    (x, y)
}

fn wire(a: (f32, f32), b: (f32, f32), color: Color) -> El {
    let (minx, miny) = (a.0.min(b.0) - 60.0, a.1.min(b.1) - 4.0);
    let mut pts = Vec::new();
    for i in 0..=24 {
        let t = i as f32 / 24.0;
        let (c1, c2) = ((a.0 + 60.0, a.1), (b.0 - 60.0, b.1));
        let u = 1.0 - t;
        let x = u * u * u * a.0 + 3.0 * u * u * t * c1.0 + 3.0 * u * t * t * c2.0 + t * t * t * b.0;
        let y = u * u * u * a.1 + 3.0 * u * u * t * c1.1 + 3.0 * u * t * t * c2.1 + t * t * t * b.1;
        pts.push([x - minx, y - miny]);
    }
    let w = (a.0.max(b.0) + 60.0 - minx).max(1.0);
    let h = (a.1.max(b.1) + 4.0 - miny).max(1.0);
    canvas(Shape::new().stroke(pts, 2.0, color)).absolute(minx, miny).w(w).h(h).inert()
}

pub fn view(_app: &AppState, _e: &Editor, g: &GraphEditor, t: &Tokens) -> El {
    let compiled = fig_fx::graph::compile(&g.graph);
    let valid = compiled.is_valid();
    let toolbar = row()
        .w_full()
        .h(48.0)
        .px(8.0)
        .gap(8.0)
        .fill(t.panel_bg)
        .center_y()
        .child(widgets::button("‹ Effects", Kind::Ghost, t).on_press(|s: &mut AppState| {
            if let Some(e) = s.editor.as_mut() {
                e.graph = None;
            }
        }))
        .child(
            text_input(&g.name, font(13.0, 500.0))
                .single_line()
                .w(210.0)
                .h(28.0)
                .px(8.0)
                .radius(6.0)
                .fill(hex("#1b1d22"))
                .border(1.0, t.border)
                .color(t.text_primary)
                .on_edit(|s: &mut AppState| &mut s.editor.as_mut().unwrap().graph.as_mut().unwrap().name),
        )
        .child(widgets::button("Add node", Kind::Normal, t).key("graph.add").on_press(|s: &mut AppState| {
            let mut by_cat: Vec<(String, Vec<Item>)> = Vec::new();
            for p in fig_fx::graph::primitives() {
                if p.id == "output" || p.id == "input" {
                    continue;
                }
                let id = p.id;
                let item = Item::new(p.display_name, move |s, _| add_node(s, id, None));
                match by_cat.iter_mut().find(|(c, _)| c == p.category) {
                    Some((_, l)) => l.push(item),
                    None => by_cat.push((p.category.to_string(), vec![item])),
                }
            }
            let fx: Vec<Item> = fig_fx::effects::all()
                .into_iter()
                .filter(|e| !e.is_custom)
                .map(|e| {
                    let tid = e.type_id.clone();
                    Item::new(e.display_name.clone(), move |s, _| add_node(s, fig_fx::graph::STOCK_EFFECT, Some(&tid)))
                })
                .collect();
            let mut items: Vec<Item> = by_cat.into_iter().map(|(c, l)| Item::submenu(c, l)).collect();
            items.push(Item::submenu("Effects", fx));
            crate::menu::open_below(s, "graph.add", items);
        }))
        .child(el().grow(1.0))
        .child(
            el().w(28.0)
                .h(28.0)
                .radius(14.0)
                .fill(if valid { hex("#17302a") } else { hex("#2a2510") })
                .border(1.0, if valid { hex("#589b74") } else { hex("#eab308") })
                .center()
                .help(if valid { "The graph is complete".to_string() } else { compiled.errors.first().map_or("Incomplete".into(), |e| e.message.clone()) })
                .child(text(if valid { "✓" } else { "!" }).font(font(13.0, 700.0)).color(if valid { hex("#589b74") } else { hex("#eab308") })),
        )
        .child(widgets::button("Save", Kind::Primary, t).on_press(save));
    let mut cv = el()
        .key("graph.canvas")
        .w_full()
        .h_full()
        .fill(hex("#141517"))
        .clip_rounded(0.0)
        .on_drag(|s: &mut AppState, d: &mut Drag, tx: &mut Tx| {
            let Some(g) = ge(s) else { return };
            match d.phase {
                Phase::Down => {
                    g.drag0 = Some((f64::from(g.pan.0), f64::from(g.pan.1)));
                    g.selected = None;
                }
                Phase::Move => {
                    if let Some((x, y)) = g.drag0 {
                        g.pan = (x as f32 + d.dx, y as f32 + d.dy);
                    }
                }
                Phase::Up => g.drag0 = None,
            }
            tx.instant();
        });
    for edge in &g.graph.edges {
        let (Some(a), Some(b)) = (g.graph.nodes.iter().find(|n| n.id == edge.from_node_id), g.graph.nodes.iter().find(|n| n.id == edge.to_node_id)) else { continue };
        cv = cv.child(wire(port_pos(g, a, &edge.from_port, true), port_pos(g, b, &edge.to_port, false), hex("#5f9cf8")));
    }
    if let Some((node, port, x, y)) = &g.wiring
        && let Some(n) = g.graph.nodes.iter().find(|n| &n.id == node)
    {
        cv = cv.child(wire(port_pos(g, n, port, true), (*x, *y), hex("#9cc0ff")));
    }
    for node in &g.graph.nodes {
        cv = cv.child(node_view(g, node, t));
    }
    let right = side_panel(g, t);
    col()
        .w_full()
        .grow(1.0)
        .min_h(0.0)
        .child(toolbar)
        .child(row().w_full().grow(1.0).min_h(0.0).child(el().grow(1.0).h_full().child(cv)).child(right))
        .child(
            row()
                .w_full()
                .h(28.0)
                .px(10.0)
                .center_y()
                .fill(t.panel_bg)
                .child(text(g.message.clone().unwrap_or_else(|| "Drag from an output to an input to wire them · Delete removes the selected node · Esc goes back".into())).font(font(11.0, 400.0)).color(t.text_muted)),
        )
}

fn node_view(g: &GraphEditor, node: &GraphNode, t: &Tokens) -> El {
    let (x, y) = (node.x as f32 + g.pan.0, node.y as f32 + g.pan.1);
    let h = node_height(node);
    let on = g.selected.as_deref() == Some(node.id.as_str());
    let id = node.id.clone();
    let mut card = el()
        .absolute(x, y)
        .w(NODE_W)
        .h(h)
        .radius(8.0)
        .fill(hex("#232427"))
        .border(if on { 2.0 } else { 1.0 }, if on { hex("#5f9cf8") } else { hex("#34373c") })
        .child(
            row()
                .w_full()
                .h(HEADER_H)
                .px(8.0)
                .center_y()
                .cursor(Cursor::Move)
                .child(text(node_title(node)).font(font(12.0, 600.0)).color(hex("#f0f0f0")).truncate())
                .on_drag(move |s: &mut AppState, d: &mut Drag, tx: &mut Tx| {
                    let Some(g) = ge(s) else { return };
                    let Some(n) = g.graph.nodes.iter_mut().find(|n| n.id == id) else { return };
                    match d.phase {
                        Phase::Down => {
                            g.drag0 = Some((n.x, n.y));
                            g.selected = Some(id.clone());
                        }
                        Phase::Move => {
                            if let Some((x0, y0)) = g.drag0 {
                                n.x = x0 + f64::from(d.dx);
                                n.y = y0 + f64::from(d.dy);
                            }
                        }
                        Phase::Up => g.drag0 = None,
                    }
                    tx.instant();
                }),
        );
    let (ins, outs) = ports_of(node);
    for (i, (port, label)) in ins.iter().enumerate() {
        let py = HEADER_H + 8.0 + i as f32 * ROW_H;
        let (nid, p) = (node.id.clone(), port.to_string());
        card = card
            .child(text(*label).font(font(11.0, 400.0)).color(hex("#c8c8c8")).absolute(12.0, py + 3.0))
            .child(
                el().absolute(-PORT_R, py + ROW_H / 2.0 - PORT_R)
                    .w(PORT_R * 2.0)
                    .h(PORT_R * 2.0)
                    .radius(PORT_R)
                    .fill(hex("#8a8a8a"))
                    .help("Drop a wire here")
                    .on_hover(move |s: &mut AppState| {
                        // finishing a wire over an input connects it
                        let Some(g) = ge(s) else { return };
                        if let Some((from, port, _, _)) = g.wiring.take() {
                            g.graph.edges.retain(|e| !(e.to_node_id == nid && e.to_port == p));
                            g.graph.edges.push(GraphEdge { from_node_id: from, from_port: port, to_node_id: nid.clone(), to_port: p.clone() });
                        }
                    }),
            );
    }
    for (i, (port, label)) in outs.iter().enumerate() {
        let py = HEADER_H + 8.0 + i as f32 * ROW_H;
        let (nid, p) = (node.id.clone(), port.to_string());
        card = card
            .child(text(*label).font(font(11.0, 400.0)).color(hex("#c8c8c8")).absolute(NODE_W - 60.0, py + 3.0))
            .child(
                el().absolute(NODE_W - PORT_R, py + ROW_H / 2.0 - PORT_R)
                    .w(PORT_R * 2.0)
                    .h(PORT_R * 2.0)
                    .radius(PORT_R)
                    .fill(hex("#5f9cf8"))
                    .cursor(Cursor::Crosshair)
                    .on_drag(move |s: &mut AppState, d: &mut Drag, tx: &mut Tx| {
                        let origin = rune::memo::window_layout("graph.canvas").unwrap_or([0.0; 4]);
                        let Some(g) = ge(s) else { return };
                        let Some(n) = g.graph.nodes.iter().find(|n| n.id == nid) else { return };
                        let (sx, sy) = port_pos(g, n, &p, true);
                        let (x, y) = (sx + d.dx, sy + d.dy);
                        match d.phase {
                            Phase::Down | Phase::Move => g.wiring = Some((nid.clone(), p.clone(), x, y)),
                            Phase::Up => {
                                // dropped on an input port: find the nearest within reach
                                let target = g.graph.nodes.iter().find_map(|n| {
                                    ports_of(n).0.iter().find_map(|(port, _)| {
                                        let (px, py) = port_pos(g, n, port, false);
                                        ((px - x).abs() < 12.0 && (py - y).abs() < 12.0).then(|| (n.id.clone(), port.to_string()))
                                    })
                                });
                                let wiring = g.wiring.take();
                                if let (Some((to, to_port)), Some((from, from_port, _, _))) = (target, wiring)
                                    && to != from
                                {
                                    g.graph.edges.retain(|e| !(e.to_node_id == to && e.to_port == to_port));
                                    g.graph.edges.push(GraphEdge { from_node_id: from, from_port, to_node_id: to, to_port });
                                }
                                let _ = origin;
                            }
                        }
                        tx.instant();
                    }),
            );
    }
    let id = node.id.clone();
    card.on_press(move |s: &mut AppState| {
        if let Some(g) = ge(s) {
            g.selected = Some(id.clone());
        }
    })
}

fn side_panel(g: &GraphEditor, t: &Tokens) -> El {
    let mut c = col().w(320.0).h_full().padding(12.0).gap(8.0).fill(t.panel_bg);
    let Some(node) = g.selected.as_ref().and_then(|id| g.graph.nodes.iter().find(|n| &n.id == id)) else {
        return c.child(widgets::heading("Selected node", t)).child(widgets::note("Click a node to edit its settings.", t));
    };
    c = c.child(widgets::heading(node_title(node), t));
    for def in fig_fx::graph::param_schema_for(node) {
        let v = node.params.get(&def.key).copied().map_or(def.default, |v| v.as_number());
        let (nid, key, kind) = (node.id.clone(), def.key.clone(), def.kind);
        let set = std::rc::Rc::new(move |s: &mut AppState, v: f64, _p: Phase| {
            if let Some(g) = ge(s)
                && let Some(n) = g.graph.nodes.iter_mut().find(|n| n.id == nid)
            {
                let value = match kind {
                    ParamKind::Bool => ParamValue::Bool(v != 0.0),
                    ParamKind::Color => ParamValue::Color(v as u32),
                    ParamKind::List => ParamValue::Choice(v as i32),
                    _ => ParamValue::Double(v),
                };
                n.params.insert(key.clone(), value);
            }
        });
        let control = match def.kind {
            ParamKind::Bool => {
                let set = set.clone();
                widgets::switch(v != 0.0, t).on_press(move |s: &mut AppState| set(s, if v != 0.0 { 0.0 } else { 1.0 }, Phase::Up))
            }
            _ => widgets::slider(format!("g.{}.{}", node.id, def.key), v, def.min, if def.max > def.min { def.max } else { def.min + 1.0 }, def.default, set, t),
        };
        let nid = node.id.clone();
        let key = def.key.clone();
        let label = def.label.clone();
        let promoted = g.graph.promoted.iter().any(|p| p.targets.iter().any(|t| t.node_id == node.id && t.param == def.key));
        c = c.child(
            row()
                .w_full()
                .gap(6.0)
                .center_y()
                .child(text(def.label.clone()).font(font(11.5, 400.0)).color(t.text_muted).w(84.0).truncate())
                .child(el().grow(1.0).child(control))
                .child(widgets::toggle_button(&crate::icons::PIN, promoted, 24.0, t).help("Show this as a control on clips").on_press(move |s: &mut AppState| {
                    let Some(g) = ge(s) else { return };
                    if let Some(i) = g.graph.promoted.iter().position(|p| p.targets.iter().any(|t| t.node_id == nid && t.param == key)) {
                        g.graph.promoted.remove(i);
                    } else {
                        g.graph.promoted.push(fig_timeline::PromotedParam {
                            key: format!("{key}_{}", g.graph.promoted.len()),
                            label: label.clone(),
                            targets: vec![fig_timeline::PromotedTarget { node_id: nid.clone(), param: key.clone(), scale: 1.0 }],
                            ..Default::default()
                        });
                    }
                })),
        );
    }
    let id = node.id.clone();
    if node.primitive_id != "output" && node.primitive_id != "input" {
        c = c.child(widgets::button("Delete node", Kind::Danger, t).on_press(move |s: &mut AppState| delete_node(s, &id)));
    }
    c
}

fn add_node(app: &mut AppState, primitive: &str, effect: Option<&str>) {
    let Some(g) = ge(app) else { return };
    let mut node = GraphNode { primitive_id: primitive.into(), effect_type_id: effect.map(str::to_string), ..Default::default() };
    if let Some(t) = effect
        && let Some(entry) = fig_fx::effects::find(t)
    {
        node.params = entry.create_instance().params;
    }
    node.x = f64::from(240.0 - g.pan.0) + (g.graph.nodes.len() as f64 * 17.0) % 120.0;
    node.y = f64::from(220.0 - g.pan.1) + (g.graph.nodes.len() as f64 * 23.0) % 120.0;
    g.selected = Some(node.id.clone());
    g.graph.nodes.push(node);
}

fn delete_node(app: &mut AppState, id: &str) {
    let Some(g) = ge(app) else { return };
    g.graph.nodes.retain(|n| n.id != id);
    g.graph.edges.retain(|e| e.from_node_id != id && e.to_node_id != id);
    g.graph.promoted.retain(|p| p.targets.iter().all(|t| t.node_id != id));
    g.selected = None;
}

fn save(app: &mut AppState) {
    let Some(e) = app.editor.as_mut() else { return };
    let Some(g) = e.graph.as_mut() else { return };
    g.graph.name = if g.name.text.trim().is_empty() { "Untitled effect".into() } else { g.name.text.trim().to_string() };
    let result = e.graphs.save(&g.graph);
    if result.is_valid() {
        g.message = Some(format!("Saved \u{201c}{}\u{201d}: it's in the Effects library under Custom", g.graph.name));
        e.media_rev += 1;
    } else {
        g.message = Some(result.errors.first().map_or("The graph isn't complete yet".into(), |er| er.message.clone()));
    }
}

pub fn key(app: &mut AppState, ev: &KeyEvent) -> bool {
    if matches!(ev.key, rune::Key::Delete | rune::Key::Backspace)
        && let Some(id) = ge(app).and_then(|g| g.selected.clone())
    {
        let protected = ge(app).and_then(|g| g.graph.nodes.iter().find(|n| n.id == id).map(|n| n.primitive_id == "output" || n.primitive_id == "input")).unwrap_or(true);
        if !protected {
            delete_node(app, &id);
        }
        return true;
    }
    false
}
