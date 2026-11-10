//! The graph editor (`GraphEditorView`, `GraphEditorViewModel`, `GraphCanvas`): a custom effect built from primitive
//! nodes wired together, ending in one Output. Drag a node to move it (with the others picked), drag from a port to
//! another to wire them, drag empty space to box-select, middle-drag to pan, the wheel to zoom. Right-click for the
//! node, wire and canvas menus; Shift+A searches for a node. Every edit is an undo step; leaving with unsaved
//! changes asks first. The right panel edits the picked node, lists the controls the effect shows on a clip, and
//! shows the effect on a test picture.

use std::cell::RefCell;
use std::collections::HashSet;
use std::time::Instant;

use fig_timeline::{EffectGraph, GraphEdge, GraphNode, ParamKind, ParamValue};
use rune::{Color, Cursor, Drag, El, KeyEvent, Phase, PointerButton, Shape, TextEdit, Tx, canvas, col, el, row, text, text_input};

use crate::app::AppState;
use crate::editor::Editor;
use crate::ui::menu::{Item, Popup};
use crate::ui::theme::{Tokens, font, hex};
use crate::ui::widgets::{self, Kind};

const NODE_W: f32 = 176.0;
const HEADER_H: f32 = 26.0;
const ROW_H: f32 = 22.0;
const PORT_R: f32 = 5.0;
const OUTPUT: &str = "output";
const INPUT: &str = "input";

pub struct GraphEditor {
    pub graph: EffectGraph,
    /// The graph as last saved (Revert goes back to it).
    baseline: EffectGraph,
    pub name: TextEdit,
    pub pan: (f32, f32),
    pub zoom: f32,
    pub selected: Vec<String>,
    /// A picked wire (Delete removes it): its (to node, to port).
    pub edge: Option<(String, String)>,
    /// A wire being dragged: from (node, port, from an output), to the pointer (canvas coordinates).
    pub wiring: Option<(String, String, bool, f32, f32)>,
    drag: CanvasDrag,
    undo: Vec<EffectGraph>,
    redo: Vec<EffectGraph>,
    coalesce: Option<(String, Instant)>,
    clipboard: (Vec<GraphNode>, Vec<GraphEdge>),
    pub dirty: bool,
    /// Leaving with changes: the save / discard / keep editing card is up.
    pub confirm_close: bool,
    /// Shift+A: the search box and the canvas point a picked node goes.
    pub search: Option<(TextEdit, (f32, f32))>,
    pub message: Option<String>,
    /// The test picture before and after the effect, and the graph it was made for.
    preview: RefCell<Option<(String, rune::Pixels, rune::Pixels)>>,
    framed: bool,
}

#[derive(Default)]
enum CanvasDrag {
    #[default]
    None,
    Pan(f32, f32),
    Nodes(Vec<(String, f64, f64)>),
    Marquee { x0: f32, y0: f32, x1: f32, y1: f32, base: Vec<String> },
}

/// Opens a graph effect (`graph:<id>`), or a new one: an Input wired straight to an Output.
pub fn open(app: &mut AppState, type_id: Option<&str>) {
    let Some(e) = app.editor.as_mut() else { return };
    let saved = type_id.and_then(|t| t.strip_prefix("graph:")).and_then(|id| e.graphs.find(id).cloned());
    if type_id.is_some() && saved.is_none() {
        e.say("That effect is not part of this project.");
        return;
    }
    let graph = saved.unwrap_or_else(|| {
        let mut name = "New effect".to_string();
        for i in 2.. {
            if !e.graphs.graphs().iter().any(|g| g.name == name) {
                break;
            }
            name = format!("New effect {i}");
        }
        let mut g = EffectGraph { name, ..Default::default() };
        let input = GraphNode { primitive_id: INPUT.into(), x: 40.0, y: 120.0, ..Default::default() };
        let output = GraphNode { primitive_id: OUTPUT.into(), x: 520.0, y: 120.0, ..Default::default() };
        g.edges.push(GraphEdge { from_node_id: input.id.clone(), from_port: "image".into(), to_node_id: output.id.clone(), to_port: "image".into() });
        g.nodes = vec![input, output];
        g
    });
    let name = TextEdit::new(graph.name.clone());
    e.graph = Some(GraphEditor {
        baseline: graph.clone(),
        graph,
        name,
        pan: (0.0, 0.0),
        zoom: 1.0,
        selected: Vec::new(),
        edge: None,
        wiring: None,
        drag: CanvasDrag::None,
        undo: Vec::new(),
        redo: Vec::new(),
        coalesce: None,
        clipboard: (Vec::new(), Vec::new()),
        dirty: false,
        confirm_close: false,
        search: None,
        message: None,
        preview: RefCell::new(None),
        framed: false,
    });
}

fn ge(app: &mut AppState) -> Option<&mut GraphEditor> {
    app.editor.as_mut()?.graph.as_mut()
}

// ---- history -----------------------------------------------------------------------------------

impl GraphEditor {
    /// Remembers the graph before an edit. Edits sharing a `coalesce` key within 0.7 s are one step.
    fn checkpoint(&mut self, coalesce: Option<&str>) {
        if let (Some(key), Some((last, at))) = (coalesce, &self.coalesce)
            && key == last
            && at.elapsed().as_millis() < 700
        {
            self.coalesce = Some((key.to_string(), Instant::now()));
            return;
        }
        self.coalesce = coalesce.map(|k| (k.to_string(), Instant::now()));
        self.undo.push(self.graph.clone());
        if self.undo.len() > 100 {
            self.undo.remove(0);
        }
        self.redo.clear();
        self.dirty = true;
    }

    fn restore(&mut self, g: EffectGraph) {
        self.name = TextEdit::new(g.name.clone());
        self.graph = g;
        self.selected.retain(|id| self.graph.nodes.iter().any(|n| &n.id == id));
        self.edge = None;
    }

    fn undo(&mut self) {
        if let Some(g) = self.undo.pop() {
            self.redo.push(self.graph.clone());
            self.restore(g);
            self.coalesce = None;
            self.dirty = true;
        }
    }

    fn redo(&mut self) {
        if let Some(g) = self.redo.pop() {
            self.undo.push(self.graph.clone());
            self.restore(g);
            self.coalesce = None;
            self.dirty = true;
        }
    }

    fn revert(&mut self) {
        self.checkpoint(None);
        self.restore(self.baseline.clone());
        self.dirty = false;
    }

    fn node(&self, id: &str) -> Option<&GraphNode> {
        self.graph.nodes.iter().find(|n| n.id == id)
    }

    fn new_id(&self) -> String {
        loop {
            let id: String = fig_timeline::new_id().chars().filter(|c| *c != '-').take(8).collect();
            if self.node(&id).is_none() {
                return id;
            }
        }
    }

    // ---- edits -------------------------------------------------------------------------------

    fn add_node(&mut self, primitive: &str, effect: Option<&str>, at: (f64, f64)) -> String {
        self.checkpoint(None);
        let mut node = GraphNode { id: self.new_id(), primitive_id: primitive.into(), effect_type_id: effect.map(str::to_string), x: at.0, y: at.1, ..Default::default() };
        for def in fig_fx::graph::param_schema_for(&node) {
            node.params.insert(def.key.clone(), def.default_value());
        }
        let id = node.id.clone();
        self.graph.nodes.push(node);
        self.selected = vec![id.clone()];
        self.edge = None;
        id
    }

    /// Why an output can't feed an input, or none when it can (dropping on a wired input replaces its wire).
    fn why_not(&self, from: &str, from_port: &str, to: &str, to_port: &str) -> Option<String> {
        if from == to {
            return Some("A node cannot feed itself.".into());
        }
        let (Some(a), Some(b)) = (self.node(from), self.node(to)) else { return Some("That node is gone.".into()) };
        let out = fig_fx::graph::find_primitive(&a.primitive_id).and_then(|p| p.output(from_port));
        let inp = fig_fx::graph::find_primitive(&b.primitive_id).and_then(|p| p.input(to_port));
        let (Some(out), Some(inp)) = (out, inp) else { return Some("That port does not exist.".into()) };
        if out.kind != inp.kind {
            return Some(format!("A {:?} cannot go into a {:?} input.", out.kind, inp.kind).to_lowercase().replacen('a', "A", 1));
        }
        // the wire would make a loop when the target already reaches the source
        let mut stack = vec![to.to_string()];
        let mut seen = HashSet::new();
        while let Some(id) = stack.pop() {
            if id == from {
                return Some("That wire would make a loop.".into());
            }
            if seen.insert(id.clone()) {
                stack.extend(self.graph.edges.iter().filter(|e| e.from_node_id == id).map(|e| e.to_node_id.clone()));
            }
        }
        None
    }

    fn connect(&mut self, from: &str, from_port: &str, to: &str, to_port: &str) {
        if let Some(why) = self.why_not(from, from_port, to, to_port) {
            self.message = Some(why);
            return;
        }
        self.checkpoint(None);
        self.graph.edges.retain(|e| !(e.to_node_id == to && e.to_port == to_port));
        self.graph.edges.push(GraphEdge { from_node_id: from.into(), from_port: from_port.into(), to_node_id: to.into(), to_port: to_port.into() });
        self.edge = None;
    }

    fn disconnect(&mut self, to: &str, to_port: &str) {
        self.checkpoint(None);
        self.graph.edges.retain(|e| !(e.to_node_id == to && e.to_port == to_port));
        self.edge = None;
    }

    fn disconnect_node(&mut self, id: &str) {
        if self.graph.edges.iter().any(|e| e.from_node_id == id || e.to_node_id == id) {
            self.checkpoint(None);
            self.graph.edges.retain(|e| e.from_node_id != id && e.to_node_id != id);
        }
    }

    /// A node in the middle of a wire: wire source → new node → wire target.
    fn insert_on_edge(&mut self, to: &str, to_port: &str, primitive: &str) {
        let Some(edge) = self.graph.edges.iter().find(|e| e.to_node_id == to && e.to_port == to_port).cloned() else { return };
        let (Some(a), Some(b)) = (self.node(&edge.from_node_id), self.node(&edge.to_node_id)) else { return };
        let Some(p) = fig_fx::graph::find_primitive(primitive) else { return };
        let kind = fig_fx::graph::find_primitive(&a.primitive_id).and_then(|q| q.output(&edge.from_port)).map(|o| o.kind);
        let (Some(input), Some(output)) = (p.inputs.iter().find(|i| Some(i.kind) == kind), p.outputs.iter().find(|o| Some(o.kind) == kind)) else { return };
        let at = ((a.x + b.x) / 2.0, (a.y + b.y) / 2.0 + 40.0);
        let (iid, oid) = (input.id, output.id);
        let id = self.add_node(primitive, None, at);
        self.graph.edges.retain(|e| !(e.to_node_id == to && e.to_port == to_port));
        self.graph.edges.push(GraphEdge { from_node_id: edge.from_node_id, from_port: edge.from_port, to_node_id: id.clone(), to_port: iid.into() });
        self.graph.edges.push(GraphEdge { from_node_id: id, from_port: oid.into(), to_node_id: edge.to_node_id, to_port: edge.to_port });
    }

    fn delete_selection(&mut self) {
        if let Some((to, port)) = self.edge.clone() {
            return self.disconnect(&to, &port);
        }
        let ids: Vec<String> = self.selected.iter().filter(|id| self.node(id).is_some_and(|n| n.primitive_id != OUTPUT)).cloned().collect();
        if ids.is_empty() {
            if !self.selected.is_empty() {
                self.message = Some("Every effect needs its Output node.".into());
            }
            return;
        }
        self.checkpoint(None);
        self.graph.nodes.retain(|n| !ids.contains(&n.id));
        self.graph.edges.retain(|e| !ids.contains(&e.from_node_id) && !ids.contains(&e.to_node_id));
        for p in &mut self.graph.promoted {
            p.targets.retain(|t| !ids.contains(&t.node_id));
        }
        self.graph.promoted.retain(|p| !p.targets.is_empty());
        self.selected.clear();
    }

    fn can_bypass(node: &GraphNode) -> bool {
        fig_fx::graph::find_primitive(&node.primitive_id).is_some_and(|p| p.inputs.len() == 1 && p.outputs.len() == 1 && p.inputs[0].kind == p.outputs[0].kind)
    }

    fn toggle_bypass(&mut self, id: &str) {
        if !self.node(id).is_some_and(Self::can_bypass) {
            return;
        }
        self.checkpoint(None);
        if let Some(n) = self.graph.nodes.iter_mut().find(|n| n.id == id) {
            n.bypass = !n.bypass;
        }
    }

    fn copy(&mut self) {
        let ids: HashSet<&String> = self.selected.iter().filter(|id| self.node(id).is_some_and(|n| n.primitive_id != OUTPUT)).collect();
        self.clipboard = (
            self.graph.nodes.iter().filter(|n| ids.contains(&n.id)).cloned().collect(),
            self.graph.edges.iter().filter(|e| ids.contains(&e.from_node_id) && ids.contains(&e.to_node_id)).cloned().collect(),
        );
    }

    fn paste(&mut self, at: (f64, f64)) {
        if self.clipboard.0.is_empty() {
            return;
        }
        self.checkpoint(None);
        let (nodes, edges) = self.clipboard.clone();
        let min_x = nodes.iter().map(|n| n.x).fold(f64::INFINITY, f64::min);
        let min_y = nodes.iter().map(|n| n.y).fold(f64::INFINITY, f64::min);
        let mut map = std::collections::HashMap::new();
        self.selected.clear();
        for mut n in nodes {
            let id = self.new_id();
            map.insert(n.id.clone(), id.clone());
            n.id = id.clone();
            n.x = at.0 + (n.x - min_x);
            n.y = at.1 + (n.y - min_y);
            self.graph.nodes.push(n);
            self.selected.push(id);
        }
        for e in edges {
            self.graph.edges.push(GraphEdge { from_node_id: map[&e.from_node_id].clone(), from_port: e.from_port, to_node_id: map[&e.to_node_id].clone(), to_port: e.to_port });
        }
        self.edge = None;
    }

    fn duplicate(&mut self) {
        self.copy();
        let x = self.clipboard.0.iter().map(|n| n.x).fold(f64::INFINITY, f64::min);
        let y = self.clipboard.0.iter().map(|n| n.y).fold(f64::INFINITY, f64::min);
        if x.is_finite() {
            self.paste((x + 32.0, y + 32.0));
        }
    }

    fn set_param(&mut self, node: &str, key: &str, value: ParamValue) {
        self.checkpoint(Some(&format!("param:{node}:{key}")));
        if let Some(n) = self.graph.nodes.iter_mut().find(|n| n.id == node) {
            n.params.insert(key.to_string(), value);
        }
    }

    fn is_promoted(&self, node: &str, param: &str) -> bool {
        self.graph.promoted.iter().any(|p| p.targets.iter().any(|t| t.node_id == node && t.param == param))
    }

    fn unique_control_key(&self, preferred: &str) -> String {
        let mut key = preferred.to_string();
        for i in 2.. {
            if !self.graph.promoted.iter().any(|p| p.key == key) {
                break;
            }
            key = format!("{preferred}{i}");
        }
        key
    }

    fn promote(&mut self, node: &str, def: &fig_timeline::ParamDef) {
        let key = self.unique_control_key(&def.key);
        self.graph.promoted.push(fig_timeline::PromotedParam {
            key,
            label: def.label.clone(),
            targets: vec![fig_timeline::PromotedTarget { node_id: node.into(), param: def.key.clone(), scale: 1.0 }],
            ..Default::default()
        });
    }

    fn toggle_promote(&mut self, node: &str, param: &str) {
        let Some(n) = self.node(node).cloned() else { return };
        self.checkpoint(None);
        if self.is_promoted(node, param) {
            for p in &mut self.graph.promoted {
                p.targets.retain(|t| !(t.node_id == node && t.param == param));
            }
            self.graph.promoted.retain(|p| !p.targets.is_empty());
        } else if let Some(def) = fig_fx::graph::param_schema_for(&n).into_iter().find(|d| d.key == param) {
            self.promote(node, &def);
        }
    }

    fn promote_all(&mut self, node: &str) {
        let Some(n) = self.node(node).cloned() else { return };
        let missing: Vec<_> = fig_fx::graph::param_schema_for(&n).into_iter().filter(|d| !self.is_promoted(node, &d.key)).collect();
        if missing.is_empty() {
            return;
        }
        self.checkpoint(None);
        for def in missing {
            self.promote(node, &def);
        }
    }

    fn move_control(&mut self, key: &str, delta: isize) {
        let Some(i) = self.graph.promoted.iter().position(|p| p.key == key) else { return };
        let to = i as isize + delta;
        if to < 0 || to as usize >= self.graph.promoted.len() {
            return;
        }
        self.checkpoint(None);
        self.graph.promoted.swap(i, to as usize);
    }

    fn remove_control(&mut self, key: &str) {
        self.checkpoint(None);
        self.graph.promoted.retain(|p| p.key != key);
    }

    // ---- the view's coordinates ----------------------------------------------------------------

    fn to_screen(&self, x: f64, y: f64) -> (f32, f32) {
        (x as f32 * self.zoom + self.pan.0, y as f32 * self.zoom + self.pan.1)
    }

    fn to_world(&self, x: f32, y: f32) -> (f64, f64) {
        (f64::from((x - self.pan.0) / self.zoom), f64::from((y - self.pan.1) / self.zoom))
    }

    /// Zooms and pans so every node fits the canvas.
    fn frame_all(&mut self, size: [f32; 2]) {
        if self.graph.nodes.is_empty() || size[0] < 10.0 {
            return;
        }
        let left = self.graph.nodes.iter().map(|n| n.x as f32).fold(f32::MAX, f32::min);
        let top = self.graph.nodes.iter().map(|n| n.y as f32).fold(f32::MAX, f32::min);
        let right = self.graph.nodes.iter().map(|n| n.x as f32 + NODE_W).fold(f32::MIN, f32::max);
        let bottom = self.graph.nodes.iter().map(|n| n.y as f32 + node_height(n)).fold(f32::MIN, f32::max);
        const MARGIN: f32 = 48.0;
        let z = ((size[0] - 2.0 * MARGIN) / (right - left).max(1.0)).min((size[1] - 2.0 * MARGIN) / (bottom - top).max(1.0));
        self.zoom = z.clamp(0.35, 1.2);
        self.pan = ((size[0] - (right - left) * self.zoom) / 2.0 - left * self.zoom, (size[1] - (bottom - top) * self.zoom) / 2.0 - top * self.zoom);
        self.framed = true;
    }
}

// ---- drawing ---------------------------------------------------------------------------------

/// A node's ports: (id, label), its inputs then its outputs.
type Ports = Vec<(&'static str, &'static str)>;

fn ports_of(node: &GraphNode) -> (Ports, Ports) {
    match fig_fx::graph::find_primitive(&node.primitive_id) {
        Some(p) => (p.inputs.iter().map(|i| (i.id, i.label)).collect(), p.outputs.iter().map(|o| (o.id, o.label)).collect()),
        None => (Vec::new(), Vec::new()),
    }
}

pub fn node_title(node: &GraphNode) -> String {
    if let Some(t) = &node.effect_type_id {
        return fig_fx::effects::find(t).map_or(t.clone(), |e| e.display_name.clone());
    }
    fig_fx::graph::find_primitive(&node.primitive_id).map_or(node.primitive_id.clone(), |p| p.display_name.to_string())
}

fn node_height(node: &GraphNode) -> f32 {
    let (i, o) = ports_of(node);
    HEADER_H + 8.0 + (i.len().max(o.len()).max(1) as f32) * ROW_H + 6.0
}

/// A port's place on the canvas.
fn port_pos(g: &GraphEditor, node: &GraphNode, port: &str, output: bool) -> (f32, f32) {
    let (ins, outs) = ports_of(node);
    let list = if output { outs } else { ins };
    let i = list.iter().position(|(id, _)| *id == port).unwrap_or(0) as f32;
    let (x, y) = g.to_screen(node.x, node.y);
    (x + if output { NODE_W * g.zoom } else { 0.0 }, y + (HEADER_H + 8.0 + i * ROW_H + ROW_H / 2.0) * g.zoom)
}

fn bezier(a: (f32, f32), b: (f32, f32), zoom: f32) -> Vec<(f32, f32)> {
    let reach = 60.0 * zoom;
    let (c1, c2) = ((a.0 + reach, a.1), (b.0 - reach, b.1));
    (0..=24)
        .map(|i| {
            let t = i as f32 / 24.0;
            let u = 1.0 - t;
            (
                u * u * u * a.0 + 3.0 * u * u * t * c1.0 + 3.0 * u * t * t * c2.0 + t * t * t * b.0,
                u * u * u * a.1 + 3.0 * u * u * t * c1.1 + 3.0 * u * t * t * c2.1 + t * t * t * b.1,
            )
        })
        .collect()
}

fn wire(a: (f32, f32), b: (f32, f32), zoom: f32, width: f32, color: Color) -> El {
    let pts = bezier(a, b, zoom);
    let minx = pts.iter().map(|p| p.0).fold(f32::MAX, f32::min) - 4.0;
    let miny = pts.iter().map(|p| p.1).fold(f32::MAX, f32::min) - 4.0;
    let maxx = pts.iter().map(|p| p.0).fold(f32::MIN, f32::max) + 4.0;
    let maxy = pts.iter().map(|p| p.1).fold(f32::MIN, f32::max) + 4.0;
    let local: Vec<[f32; 2]> = pts.iter().map(|p| [p.0 - minx, p.1 - miny]).collect();
    canvas(Shape::new().stroke(local, width, color)).absolute(minx, miny).w(maxx - minx).h(maxy - miny).inert()
}

/// What is under a canvas point.
enum Hit {
    Out(String, String),
    In(String, String),
    Node(String),
    Wire(String, String),
    None,
}

fn hit(g: &GraphEditor, x: f32, y: f32) -> Hit {
    let reach = (PORT_R + 4.0) * g.zoom.max(0.6);
    for node in g.graph.nodes.iter().rev() {
        let (ins, outs) = ports_of(node);
        for (p, _) in &outs {
            let (px, py) = port_pos(g, node, p, true);
            if (px - x).abs() <= reach && (py - y).abs() <= reach {
                return Hit::Out(node.id.clone(), p.to_string());
            }
        }
        for (p, _) in &ins {
            let (px, py) = port_pos(g, node, p, false);
            if (px - x).abs() <= reach && (py - y).abs() <= reach {
                return Hit::In(node.id.clone(), p.to_string());
            }
        }
        let (nx, ny) = g.to_screen(node.x, node.y);
        if x >= nx && x <= nx + NODE_W * g.zoom && y >= ny && y <= ny + node_height(node) * g.zoom {
            return Hit::Node(node.id.clone());
        }
    }
    for e in &g.graph.edges {
        let (Some(a), Some(b)) = (g.node(&e.from_node_id), g.node(&e.to_node_id)) else { continue };
        let pts = bezier(port_pos(g, a, &e.from_port, true), port_pos(g, b, &e.to_port, false), g.zoom);
        if pts.windows(2).any(|w| dist_to_segment((x, y), w[0], w[1]) <= 6.0) {
            return Hit::Wire(e.to_node_id.clone(), e.to_port.clone());
        }
    }
    Hit::None
}

fn dist_to_segment(p: (f32, f32), a: (f32, f32), b: (f32, f32)) -> f32 {
    let (dx, dy) = (b.0 - a.0, b.1 - a.1);
    let len = dx * dx + dy * dy;
    let t = if len > 0.0 { (((p.0 - a.0) * dx + (p.1 - a.1) * dy) / len).clamp(0.0, 1.0) } else { 0.0 };
    ((p.0 - a.0 - t * dx).powi(2) + (p.1 - a.1 - t * dy).powi(2)).sqrt()
}

pub fn view(_app: &AppState, _e: &Editor, g: &GraphEditor, t: &Tokens) -> El {
    let compiled = fig_fx::graph::compile(&g.graph);
    let valid = compiled.is_valid();
    let errors: HashSet<String> = compiled.errors.iter().filter_map(|e| e.node_id.clone()).collect();
    let unused: HashSet<String> = compiled.unused_node_ids.iter().cloned().collect();
    let toolbar = row()
        .w_full()
        .h(48.0)
        .px(8.0)
        .gap(8.0)
        .fill(t.panel_bg)
        .center_y()
        .child(widgets::button("‹ Effects", Kind::Ghost, t).help("Back to the editor").on_press(back))
        .child(
            text_input(&g.name, font(13.0, 500.0))
                .single_line()
                .placeholder("Effect name", t.text_faint)
                .w(210.0)
                .h(28.0)
                .px(8.0)
                .radius(6.0)
                .fill(hex("#1b1d22"))
                .border(1.0, t.border)
                .color(t.text_primary)
                .on_edit(|s: &mut AppState| &mut s.editor.as_mut().unwrap().graph.as_mut().unwrap().name)
                .on_input_change(|s: &mut AppState, _tx: &mut Tx| {
                    if let Some(g) = ge(s) {
                        let name = g.name.text.clone();
                        if g.graph.name != name {
                            g.checkpoint(Some("name"));
                            g.graph.name = name;
                        }
                    }
                }),
        )
        .child(widgets::button("Add", Kind::Normal, t).key("graph.add").help("Add a node (Shift+A to search)").on_press(|s: &mut AppState| {
            let at = canvas_center(s);
            let items = add_items(at);
            crate::ui::menu::open_below(s, "graph.add", items);
        }))
        .child(widgets::button("Edit", Kind::Ghost, t).key("graph.edit").on_press(|s: &mut AppState| {
            let Some(g) = ge(s) else { return };
            let (undo, redo, clip, picked) = (!g.undo.is_empty(), !g.redo.is_empty(), !g.clipboard.0.is_empty(), !g.selected.is_empty());
            let at = canvas_center(s);
            let items = vec![
                Item::new("Undo", |s, _| with(s, GraphEditor::undo)).hint("Ctrl+Z").enabled(undo),
                Item::new("Redo", |s, _| with(s, GraphEditor::redo)).hint("Ctrl+Y").enabled(redo),
                Item::Separator,
                Item::new("Copy", |s, _| with(s, GraphEditor::copy)).hint("Ctrl+C").enabled(picked),
                Item::new("Paste", move |s, _| with(s, |g| g.paste(at))).hint("Ctrl+V").enabled(clip),
                Item::new("Duplicate", |s, _| with(s, GraphEditor::duplicate)).hint("Ctrl+D").enabled(picked),
                Item::new("Delete", |s, _| with(s, GraphEditor::delete_selection)).hint("Delete").enabled(picked).danger(),
                Item::Separator,
                Item::new("Select all", |s, _| with(s, |g| g.selected = g.graph.nodes.iter().map(|n| n.id.clone()).collect())).hint("Ctrl+A"),
            ];
            crate::ui::menu::open_below(s, "graph.edit", items);
        }))
        .child(widgets::button("View", Kind::Ghost, t).key("graph.view").on_press(|s: &mut AppState| {
            let items = vec![
                Item::new("Frame all", |s, _| frame_all(s)).hint("F"),
                Item::new("Zoom in", |s, _| zoom_by(s, 1.2)),
                Item::new("Zoom out", |s, _| zoom_by(s, 1.0 / 1.2)),
            ];
            crate::ui::menu::open_below(s, "graph.view", items);
        }))
        .child(el().grow(1.0))
        .child(
            el().w(28.0)
                .h(28.0)
                .radius(14.0)
                .fill(if valid { hex("#17302a") } else { hex("#2a2510") })
                .border(1.0, if valid { hex("#589b74") } else { hex("#eab308") })
                .center()
                .help(if valid { "Valid".to_string() } else { compiled.errors.iter().map(|e| e.message.clone()).collect::<Vec<_>>().join("\n") })
                .child(text(if valid { "✓" } else { "!" }).font(font(13.0, 700.0)).color(if valid { hex("#589b74") } else { hex("#eab308") })),
        )
        .child(widgets::icon_button(&crate::ui::icons::UNDO_2, 15.0, 30.0, t).help("Undo (Ctrl+Z)").opacity(if g.undo.is_empty() { 0.4 } else { 1.0 }).on_press(|s: &mut AppState| with(s, GraphEditor::undo)))
        .child(widgets::icon_button(&crate::ui::icons::REDO_2, 15.0, 30.0, t).help("Redo (Ctrl+Y)").opacity(if g.redo.is_empty() { 0.4 } else { 1.0 }).on_press(|s: &mut AppState| with(s, GraphEditor::redo)))
        .child(widgets::icon_button(&crate::ui::icons::ROTATE_CCW, 15.0, 30.0, t).help("Revert to the saved version").opacity(if g.dirty { 1.0 } else { 0.4 }).on_press(|s: &mut AppState| {
            if let Some(g) = ge(s)
                && g.dirty
            {
                g.revert();
            }
        }))
        .child(widgets::button("Save", Kind::Primary, t).help("Save (Ctrl+S)").opacity(if valid { 1.0 } else { 0.45 }).on_press(|s: &mut AppState| {
            save(s);
        }));

    let mut cv = el()
        .key("graph.canvas")
        // zoom and scroll move everything at once: no layout springs (they ghost)
        .instant_changes()
        .w_full()
        .h_full()
        .fill(hex("#141517"))
        .clip_rounded(0.0)
        .pointer_buttons([PointerButton::Primary, PointerButton::Middle])
        .on_layout(|s: &mut AppState, r: [f32; 4]| {
            crate::ui::layout::remember("graph.canvas", r);
            if let Some(g) = ge(s)
                && !g.framed
            {
                g.frame_all([r[2], r[3]]);
            }
        })
        .on_drag(canvas_drag)
        .on_wheel(|s: &mut AppState, w: rune::WheelEvent, _tx: &mut Tx| {
            let Some(g) = ge(s) else { return false };
            let factor = if w.dy > 0.0 { 1.1 } else { 1.0 / 1.1 };
            let (wx, wy) = g.to_world(w.x, w.y);
            g.zoom = (g.zoom * factor).clamp(0.25, 2.5);
            g.pan = (w.x - wx as f32 * g.zoom, w.y - wy as f32 * g.zoom);
            true
        })
        .on_secondary_press_local(|s: &mut AppState, p: rune::PointerEvent, _tx: &mut Tx| context_menu(s, p.x, p.y, p.window_x, p.window_y));
    // a faint grid
    let step = 24.0 * g.zoom;
    if step >= 8.0 {
        let size = rune::memo::layout_size("graph.canvas").unwrap_or([1200.0, 800.0]);
        let mut x = g.pan.0.rem_euclid(step);
        while x < size[0] {
            cv = cv.child(el().absolute(x, 0.0).w(1.0).h(size[1]).fill(Color::WHITE.alpha(0.025)).inert());
            x += step;
        }
        let mut y = g.pan.1.rem_euclid(step);
        while y < size[1] {
            cv = cv.child(el().absolute(0.0, y).w(size[0]).h(1.0).fill(Color::WHITE.alpha(0.025)).inert());
            y += step;
        }
    }
    for edge in &g.graph.edges {
        let (Some(a), Some(b)) = (g.node(&edge.from_node_id), g.node(&edge.to_node_id)) else { continue };
        let picked = g.edge.as_ref().is_some_and(|(n, p)| *n == edge.to_node_id && *p == edge.to_port);
        let mask = fig_fx::graph::find_primitive(&a.primitive_id)
            .and_then(|p| p.output(&edge.from_port))
            .is_some_and(|o| o.kind == fig_timeline::GraphPortType::Mask);
        let color = if picked { Color::WHITE } else if mask { hex("#b98cf0") } else { hex("#5f9cf8") };
        cv = cv.child(wire(port_pos(g, a, &edge.from_port, true), port_pos(g, b, &edge.to_port, false), g.zoom, if picked { 3.0 } else { 2.0 }, color));
    }
    if let Some((node, port, from_out, x, y)) = &g.wiring
        && let Some(n) = g.node(node)
    {
        let p = port_pos(g, n, port, *from_out);
        let (a, b) = if *from_out { (p, (*x, *y)) } else { ((*x, *y), p) };
        cv = cv.child(wire(a, b, g.zoom, 2.0, hex("#9cc0ff")));
    }
    for node in &g.graph.nodes {
        cv = cv.child(node_view(g, node, errors.contains(&node.id), unused.contains(&node.id)));
    }
    if let CanvasDrag::Marquee { x0, y0, x1, y1, .. } = &g.drag {
        cv = cv.child(el().absolute(x0.min(*x1), y0.min(*y1)).w((x1 - x0).abs()).h((y1 - y0).abs()).fill(hex("#285f9cf8")).border(1.0, hex("#5f9cf8")).inert());
    }
    let status = {
        let (n, w) = (g.graph.nodes.len(), g.graph.edges.len());
        if !valid {
            format!("{n} nodes  ·  {w} wires  ·  {} problem{}", compiled.errors.len(), if compiled.errors.len() == 1 { "" } else { "s" })
        } else if !unused.is_empty() {
            format!("{n} nodes  ·  {w} wires  ·  {} not connected to the output", unused.len())
        } else {
            format!("{n} nodes  ·  {w} wires  ·  no problems")
        }
    };
    let mut page = col()
        .w_full()
        .grow(1.0)
        .min_h(0.0)
        .child(toolbar)
        .child(row().w_full().grow(1.0).min_h(0.0).child(el().grow(1.0).h_full().child(cv)).child(side_panel(g, &compiled, t)))
        .child(
            row()
                .w_full()
                .h(28.0)
                .px(10.0)
                .gap(12.0)
                .center_y()
                .fill(t.panel_bg)
                .child(
                    text(g.message.clone().unwrap_or_else(|| "Drag from a port to another to wire them · right-click for more · Shift+A searches · F frames all".into()))
                        .font(font(11.0, 400.0))
                        .color(t.text_secondary)
                        .grow(1.0)
                        .truncate(),
                )
                .child(text(status).font(font(10.5, 400.0)).color(t.text_muted)),
        );
    if let Some(search) = &g.search {
        page = page.child(search_box(search, t));
    }
    if g.confirm_close {
        page = page.child(confirm_card(valid, t));
    }
    el().w_full().grow(1.0).min_h(0.0).child(page)
}

fn node_view(g: &GraphEditor, node: &GraphNode, error: bool, unused: bool) -> El {
    let z = g.zoom;
    let (x, y) = g.to_screen(node.x, node.y);
    let picked = g.selected.contains(&node.id);
    let border = if picked {
        hex("#5f9cf8")
    } else if error {
        hex("#ff6b6b")
    } else {
        hex("#34373c")
    };
    let mut title = node_title(node);
    if node.bypass {
        title = format!("{title} (bypassed)");
    }
    let mut card = el()
        .absolute(x, y)
        .w(NODE_W * z)
        .h(node_height(node) * z)
        .radius(8.0 * z)
        .fill(hex("#232427"))
        .border(if picked { 2.0 } else { 1.0 }, border)
        .opacity(if unused || node.bypass { 0.55 } else { 1.0 })
        .inert()
        .child(
            el().absolute(0.0, 0.0)
                .w(NODE_W * z)
                .h(HEADER_H * z)
                .radius(8.0 * z)
                .fill(if node.primitive_id == OUTPUT || node.primitive_id == INPUT { hex("#2c3a52") } else { hex("#2b2d31") })
                .child(text(title).font(font(12.0 * z, 600.0)).color(hex("#f0f0f0")).truncate().absolute(8.0 * z, 5.0 * z).max_w((NODE_W - 16.0) * z)),
        );
    let (ins, outs) = ports_of(node);
    for (i, (_, label)) in ins.iter().enumerate() {
        let py = (HEADER_H + 8.0 + i as f32 * ROW_H) * z;
        card = card
            .child(text(*label).font(font(11.0 * z, 400.0)).color(hex("#c8c8c8")).absolute(12.0 * z, py + 3.0 * z))
            .child(el().absolute(-PORT_R * z, py + (ROW_H / 2.0 - PORT_R) * z).w(PORT_R * 2.0 * z).h(PORT_R * 2.0 * z).radius(PORT_R * z).fill(hex("#8a8a8a")));
    }
    for (i, (_, label)) in outs.iter().enumerate() {
        let py = (HEADER_H + 8.0 + i as f32 * ROW_H) * z;
        card = card
            .child(text(*label).font(font(11.0 * z, 400.0)).color(hex("#c8c8c8")).absolute((NODE_W - 60.0) * z, py + 3.0 * z))
            .child(el().absolute((NODE_W - PORT_R) * z, py + (ROW_H / 2.0 - PORT_R) * z).w(PORT_R * 2.0 * z).h(PORT_R * 2.0 * z).radius(PORT_R * z).fill(hex("#5f9cf8")));
    }
    card
}

// ---- the canvas's pointer ----------------------------------------------------------------------

fn canvas_drag(s: &mut AppState, d: &mut Drag, tx: &mut Tx) {
    let mods = tx.mods();
    let middle = tx.button() == PointerButton::Middle;
    let Some(g) = ge(s) else { return };
    let (x, y) = (d.x, d.y);
    match d.phase {
        Phase::Down => {
            g.message = None;
            g.search = None;
            if middle {
                g.drag = CanvasDrag::Pan(g.pan.0 - x, g.pan.1 - y);
                return;
            }
            let additive = mods.shift || mods.cmd;
            match hit(g, x, y) {
                Hit::Out(n, p) => g.wiring = Some((n, p, true, x, y)),
                Hit::In(n, p) => {
                    // pulling on a wired input picks its wire up; an empty one starts a wire backwards
                    match g.graph.edges.iter().find(|e| e.to_node_id == n && e.to_port == p).cloned() {
                        Some(e) => {
                            g.disconnect(&n, &p);
                            g.wiring = Some((e.from_node_id, e.from_port, true, x, y));
                        }
                        None => g.wiring = Some((n, p, false, x, y)),
                    }
                }
                Hit::Node(id) => {
                    g.edge = None;
                    if additive {
                        if let Some(i) = g.selected.iter().position(|s| *s == id) {
                            g.selected.remove(i);
                        } else {
                            g.selected.push(id);
                        }
                    } else if !g.selected.contains(&id) {
                        g.selected = vec![id];
                    }
                    g.checkpoint(None);
                    g.drag = CanvasDrag::Nodes(g.selected.iter().filter_map(|id| g.node(id).map(|n| (id.clone(), n.x, n.y))).collect());
                }
                Hit::Wire(n, p) => {
                    g.edge = Some((n, p));
                    g.selected.clear();
                }
                Hit::None => {
                    g.edge = None;
                    let base = if additive { g.selected.clone() } else { Vec::new() };
                    if !additive {
                        g.selected.clear();
                    }
                    g.drag = CanvasDrag::Marquee { x0: x, y0: y, x1: x, y1: y, base };
                }
            }
        }
        Phase::Move => {
            if let Some(w) = g.wiring.as_mut() {
                (w.3, w.4) = (x, y);
            }
            let z = f64::from(g.zoom);
            match &mut g.drag {
                CanvasDrag::Pan(ox, oy) => g.pan = (*ox + x, *oy + y),
                CanvasDrag::Nodes(start) => {
                    let (dx, dy) = (f64::from(d.dx) / z, f64::from(d.dy) / z);
                    let start = start.clone();
                    for (id, x0, y0) in start {
                        if let Some(n) = g.graph.nodes.iter_mut().find(|n| n.id == id) {
                            n.x = x0 + dx;
                            n.y = y0 + dy;
                        }
                    }
                }
                CanvasDrag::Marquee { x0, y0, x1, y1, base } => {
                    (*x1, *y1) = (x, y);
                    let (l, r, top, bottom) = (x0.min(*x1), x0.max(*x1), y0.min(*y1), y0.max(*y1));
                    let mut picked = base.clone();
                    let boxes: Vec<(String, (f32, f32), f32)> = g.graph.nodes.iter().map(|n| (n.id.clone(), (n.x as f32, n.y as f32), node_height(n))).collect();
                    for (id, (nx, ny), h) in boxes {
                        let (sx, sy) = (nx * g.zoom + g.pan.0, ny * g.zoom + g.pan.1);
                        if sx + NODE_W * g.zoom >= l && sx <= r && sy + h * g.zoom >= top && sy <= bottom && !picked.contains(&id) {
                            picked.push(id);
                        }
                    }
                    g.selected = picked;
                }
                CanvasDrag::None => {}
            }
        }
        Phase::Up => {
            if let CanvasDrag::Nodes(_) = &g.drag
                && d.dx.abs() < 1.0
                && d.dy.abs() < 1.0
            {
                // a click, not a move: nothing to undo
                g.undo.pop();
            }
            g.drag = CanvasDrag::None;
            if let Some((node, port, from_out, _, _)) = g.wiring.take() {
                match hit(g, x, y) {
                    Hit::In(n, p) if from_out => g.connect(&node, &port, &n, &p),
                    Hit::Out(n, p) if !from_out => g.connect(&n, &p, &node, &port),
                    Hit::None => {
                        // dropped on empty canvas: offer a node there
                        let at = g.to_world(x, y);
                        g.search = Some((focused(TextEdit::new("")), (at.0 as f32, at.1 as f32)));
                    }
                    _ => {}
                }
            }
        }
    }
    tx.instant();
}

fn focused(mut edit: TextEdit) -> TextEdit {
    edit.focused = true;
    edit
}

fn with(s: &mut AppState, f: impl FnOnce(&mut GraphEditor)) {
    if let Some(g) = ge(s) {
        f(g);
    }
}

/// The world point in the middle of the canvas.
fn canvas_center(s: &mut AppState) -> (f64, f64) {
    let size = crate::ui::layout::rect("graph.canvas").map_or([800.0, 600.0], |r| [r[2], r[3]]);
    ge(s).map_or((200.0, 200.0), |g| {
        let (x, y) = g.to_world(size[0] / 2.0, size[1] / 2.0);
        (x - f64::from(NODE_W) / 2.0, y - 20.0)
    })
}

fn frame_all(s: &mut AppState) {
    let size = crate::ui::layout::rect("graph.canvas").map_or([800.0, 600.0], |r| [r[2], r[3]]);
    with(s, |g| g.frame_all(size));
}

fn zoom_by(s: &mut AppState, factor: f32) {
    let size = crate::ui::layout::rect("graph.canvas").map_or([800.0, 600.0], |r| [r[2], r[3]]);
    with(s, |g| {
        let (cx, cy) = (size[0] / 2.0, size[1] / 2.0);
        let (wx, wy) = g.to_world(cx, cy);
        g.zoom = (g.zoom * factor).clamp(0.25, 2.5);
        g.pan = (cx - wx as f32 * g.zoom, cy - wy as f32 * g.zoom);
    });
}

/// The "Add node" categories, primitives then stock effects, each placing its node at `at`.
fn add_items(at: (f64, f64)) -> Vec<Item> {
    let mut by_cat: Vec<(String, Vec<Item>)> = Vec::new();
    for p in fig_fx::graph::primitives() {
        if p.id == OUTPUT || p.id == fig_fx::graph::STOCK_EFFECT {
            continue;
        }
        let id = p.id;
        let item = Item::new(p.display_name, move |s, _| with(s, |g| {
            g.add_node(id, None, at);
        }));
        match by_cat.iter_mut().find(|(c, _)| c == p.category) {
            Some((_, l)) => l.push(item),
            None => by_cat.push((p.category.to_string(), vec![item])),
        }
    }
    by_cat.sort_by(|a, b| a.0.cmp(&b.0));
    let mut stock: Vec<(String, Vec<Item>)> = Vec::new();
    for fx in fig_fx::effects::all().into_iter().filter(|e| !e.is_custom && e.kind != fig_fx::effects::EffectKind::Audio) {
        let tid = fx.type_id.clone();
        let item = Item::new(fx.display_name.clone(), move |s, _| with(s, |g| {
            g.add_node(fig_fx::graph::STOCK_EFFECT, Some(&tid), at);
        }));
        match stock.iter_mut().find(|(c, _)| *c == fx.category) {
            Some((_, l)) => l.push(item),
            None => stock.push((fx.category.clone(), vec![item])),
        }
    }
    let mut items: Vec<Item> = by_cat.into_iter().map(|(c, l)| Item::submenu(c, l)).collect();
    items.push(Item::submenu("Stock effects", stock.into_iter().map(|(c, l)| Item::submenu(c, l)).collect()));
    items
}

fn context_menu(s: &mut AppState, x: f32, y: f32, wx: f32, wy: f32) {
    let Some(g) = ge(s) else { return };
    let at = g.to_world(x, y);
    let items = match hit(g, x, y) {
        Hit::Node(id) | Hit::In(id, _) | Hit::Out(id, _) => {
            if !g.selected.contains(&id) {
                g.selected = vec![id.clone()];
            }
            let Some(node) = g.node(&id).cloned() else { return };
            let output = node.primitive_id == OUTPUT;
            let has_params = !fig_fx::graph::param_schema_for(&node).is_empty();
            let (i1, i2, i3) = (id.clone(), id.clone(), id);
            vec![
                Item::new(if node.bypass { "Turn bypass off" } else { "Bypass" }, move |s, _| with(s, |g| g.toggle_bypass(&i1))).hint("B").enabled(GraphEditor::can_bypass(&node)),
                Item::new("Duplicate", |s, _| with(s, GraphEditor::duplicate)).hint("Ctrl+D").enabled(!output),
                Item::new("Pin all parameters", move |s, _| with(s, |g| g.promote_all(&i2))).enabled(has_params),
                Item::new("Disconnect all wires", move |s, _| with(s, |g| g.disconnect_node(&i3))),
                Item::Separator,
                Item::new("Copy", |s, _| with(s, GraphEditor::copy)).hint("Ctrl+C").enabled(!output),
                Item::new("Delete", |s, _| with(s, GraphEditor::delete_selection)).hint("Delete").enabled(!output).danger(),
            ]
        }
        Hit::Wire(n, p) => {
            g.edge = Some((n.clone(), p.clone()));
            let kind = g
                .graph
                .edges
                .iter()
                .find(|e| e.to_node_id == n && e.to_port == p)
                .and_then(|e| g.node(&e.from_node_id).and_then(|a| fig_fx::graph::find_primitive(&a.primitive_id)).and_then(|q| q.output(&e.from_port)).map(|o| o.kind));
            let mut by_cat: Vec<(String, Vec<Item>)> = Vec::new();
            for prim in fig_fx::graph::primitives().iter().filter(|q| {
                ![OUTPUT, INPUT, fig_fx::graph::STOCK_EFFECT].contains(&q.id)
                    && q.inputs.iter().any(|i| Some(i.kind) == kind)
                    && q.outputs.iter().any(|o| Some(o.kind) == kind)
            }) {
                let (n, p, id) = (n.clone(), p.clone(), prim.id);
                let item = Item::new(prim.display_name, move |s, _| with(s, |g| g.insert_on_edge(&n, &p, id)));
                match by_cat.iter_mut().find(|(c, _)| c == prim.category) {
                    Some((_, l)) => l.push(item),
                    None => by_cat.push((prim.category.to_string(), vec![item])),
                }
            }
            let (n2, p2) = (n, p);
            vec![
                Item::submenu("Insert node", by_cat.into_iter().map(|(c, l)| Item::submenu(c, l)).collect()),
                Item::Separator,
                Item::new("Delete wire", move |s, _| with(s, |g| g.disconnect(&n2, &p2))).hint("Delete").danger(),
            ]
        }
        Hit::None => {
            let clip = !g.clipboard.0.is_empty();
            let world = (at.0 as f32, at.1 as f32);
            vec![
                Item::submenu("Add node", add_items(at)),
                Item::new("Quick search…", move |s, _| with(s, |g| g.search = Some((focused(TextEdit::new("")), world)))).hint("Shift+A"),
                Item::Separator,
                Item::new("Paste", move |s, _| with(s, |g| g.paste(at))).hint("Ctrl+V").enabled(clip),
                Item::new("Select all", |s, _| with(s, |g| g.selected = g.graph.nodes.iter().map(|n| n.id.clone()).collect())).hint("Ctrl+A"),
                Item::new("Frame all", |s, _| frame_all(s)).hint("F"),
            ]
        }
    };
    crate::ui::menu::open(s, Popup::at(wx, wy, items));
}

/// Shift+A: a search box over the canvas; Enter adds the first match where the search was opened.
fn search_box(search: &(TextEdit, (f32, f32)), t: &Tokens) -> El {
    let q = search.0.text.trim().to_lowercase();
    let mut found: Vec<(String, &'static str, Option<String>)> = fig_fx::graph::primitives()
        .iter()
        .filter(|p| p.id != OUTPUT && p.id != fig_fx::graph::STOCK_EFFECT)
        .map(|p| (p.display_name.to_string(), p.id, None))
        .chain(fig_fx::effects::all().into_iter().filter(|e| !e.is_custom && e.kind != fig_fx::effects::EffectKind::Audio).map(|e| (e.display_name.clone(), fig_fx::graph::STOCK_EFFECT, Some(e.type_id.clone()))))
        .filter(|(name, _, _)| q.is_empty() || name.to_lowercase().contains(&q))
        .collect();
    found.truncate(12);
    let at = search.1;
    let mut list = col().w_full().gap(2.0);
    for (name, id, fx) in found {
        list = list.child(
            row()
                .w_full()
                .h(26.0)
                .px(8.0)
                .radius(5.0)
                .hover(t.hover_bg)
                .center_y()
                .cursor(Cursor::Pointer)
                .child(text(name).font(font(12.0, 400.0)).color(t.text_primary))
                .on_press(move |s: &mut AppState| {
                    with(s, |g| {
                        g.add_node(id, fx.as_deref(), (f64::from(at.0), f64::from(at.1)));
                        g.search = None;
                    })
                }),
        );
    }
    el().absolute(0.0, 0.0).w_full().h_full().on_press(|s: &mut AppState| with(s, |g| g.search = None)).child(
        col()
            .absolute(0.0, 70.0)
            .w(320.0)
            .ml(40.0)
            .padding(10.0)
            .gap(8.0)
            .radius(10.0)
            .fill(t.card_bg)
            .border(1.0, t.border)
            .shadow(8.0, 24.0, Color::BLACK.alpha(0.4))
            .on_press(|_: &mut AppState| {})
            .child(
                text_input(&search.0, font(13.0, 400.0))
                    .single_line()
                    .placeholder("Search nodes…", t.text_faint)
                    .w_full()
                    .h(30.0)
                    .px(8.0)
                    .radius(6.0)
                    .fill(hex("#1b1d22"))
                    .border(1.0, t.accent)
                    .color(t.text_primary)
                    .on_edit(|s: &mut AppState| &mut s.editor.as_mut().unwrap().graph.as_mut().unwrap().search.as_mut().unwrap().0)
                    .on_submit(|s: &mut AppState, _tx: &mut Tx| {
                        let Some(g) = ge(s) else { return };
                        let Some((edit, at)) = g.search.take() else { return };
                        let q = edit.text.trim().to_lowercase();
                        let at = (f64::from(at.0), f64::from(at.1));
                        if let Some(p) = fig_fx::graph::primitives().iter().find(|p| p.id != OUTPUT && p.id != fig_fx::graph::STOCK_EFFECT && p.display_name.to_lowercase().contains(&q)) {
                            g.add_node(p.id, None, at);
                        } else if let Some(fx) = fig_fx::effects::all().into_iter().find(|e| !e.is_custom && e.display_name.to_lowercase().contains(&q)) {
                            g.add_node(fig_fx::graph::STOCK_EFFECT, Some(&fx.type_id), at);
                        }
                    }),
            )
            .child(list),
    )
}

fn confirm_card(valid: bool, t: &Tokens) -> El {
    el().absolute(0.0, 0.0).w_full().h_full().fill(Color::BLACK.alpha(0.45)).center().child(
        col()
            .w(380.0)
            .padding(20.0)
            .gap(10.0)
            .radius(12.0)
            .fill(hex("#1d1e20"))
            .border(1.0, t.border)
            .child(text("Save changes to this effect?").font(font(14.0, 600.0)).color(t.text_primary))
            .child(widgets::note("Clips that use it will update when you save.", t))
            .child(
                row()
                    .w_full()
                    .gap(8.0)
                    .justify_end()
                    .child(widgets::button("Keep editing", Kind::Ghost, t).on_press(|s: &mut AppState| with(s, |g| g.confirm_close = false)))
                    .child(widgets::button("Discard", Kind::Danger, t).on_press(|s: &mut AppState| {
                        if let Some(e) = s.editor.as_mut() {
                            e.graph = None;
                        }
                    }))
                    .child(widgets::button("Save", Kind::Primary, t).opacity(if valid { 1.0 } else { 0.45 }).on_press(|s: &mut AppState| {
                        if save(s)
                            && let Some(e) = s.editor.as_mut()
                        {
                            e.graph = None;
                        }
                    })),
            ),
    )
}

// ---- the right panel -------------------------------------------------------------------------

/// The test picture: sky, a sun, hills and high-contrast bars, so blur, shifts and colour changes all show
/// (`GraphPreviewSource`).
pub fn test_picture(w: usize, h: usize) -> fig_fx::Frame {
    let mut px = vec![0u8; w * h * 4];
    for y in 0..h {
        for x in 0..w {
            let (u, v) = (x as f64 / w as f64, y as f64 / h as f64);
            let (mut r, mut g, mut b) = (60.0 + 70.0 * v, 110.0 + 70.0 * v, 170.0 + 40.0 * v);
            let hill = 0.66 + 0.06 * (u * 9.0).sin() + 0.04 * (u * 21.0 + 1.0).sin();
            if v > hill {
                (r, g, b) = (30.0, 70.0 + 30.0 * (v - hill) * 6.0, 55.0);
            }
            let (dx, dy) = ((u - 0.72) * w as f64, (v - 0.28) * h as f64);
            if dx * dx + dy * dy < (h as f64 * 0.14).powi(2) {
                (r, g, b) = (250.0, 226.0, 150.0);
            }
            if v > 0.9 {
                let on = ((u * 24.0) as i32) % 2 == 0;
                let c = if on { 235.0 } else { 25.0 };
                (r, g, b) = (c, c, c);
            }
            let i = (y * w + x) * 4;
            px[i..i + 4].copy_from_slice(&[b.clamp(0.0, 255.0) as u8, g.clamp(0.0, 255.0) as u8, r.clamp(0.0, 255.0) as u8, 255]);
        }
    }
    fig_fx::Frame::from_pixels(w, h, px)
}

pub fn to_pixels(f: &fig_fx::Frame) -> rune::Pixels {
    let mut rgba = f.pixels.clone();
    for p in rgba.as_chunks_mut::<4>().0 {
        p.swap(0, 2);
    }
    let pixels = rune::Pixels::new();
    pixels.set(f.width as u32, f.height as u32, rgba);
    pixels
}

/// The test picture before and after the effect, made again only when the graph changed.
fn preview(g: &GraphEditor) -> (rune::Pixels, rune::Pixels) {
    let key = serde_json::to_string(&g.graph).unwrap_or_default();
    let mut cache = g.preview.borrow_mut();
    if let Some((k, before, after)) = cache.as_ref()
        && *k == key
    {
        return (before.clone(), after.clone());
    }
    let source = test_picture(320, 180);
    let before = cache.as_ref().map_or_else(|| to_pixels(&source), |c| c.1.clone());
    let effect = fig_fx::graph::GraphEffect::new(&g.graph);
    let after = effect.apply(&source, &Default::default(), 0.5).map_or_else(|| to_pixels(&source), |f| to_pixels(&f));
    *cache = Some((key, before.clone(), after.clone()));
    (before, after)
}

fn side_panel(g: &GraphEditor, compiled: &fig_fx::graph::CompileResult, t: &Tokens) -> El {
    let (before, after) = preview(g);
    let mut c = col()
        .w(340.0)
        .h_full()
        .padding(12.0)
        .gap(8.0)
        .fill(t.panel_bg)
        .scroll(rune::ScrollSpec { stick_bottom: false })
        .child(text("Preview").font(font(13.0, 600.0)).color(t.text_primary))
        .child(
            row()
                .w_full()
                .gap(6.0)
                .child(col().grow(1.0).gap(2.0).child(el().w_full().h(90.0).radius(6.0).clip_rounded(6.0).pixels(&before)).child(widgets::note("Before", t)))
                .child(col().grow(1.0).gap(2.0).child(el().w_full().h(90.0).radius(6.0).clip_rounded(6.0).pixels(&after)).child(widgets::note("After", t))),
        );
    for e in &compiled.errors {
        c = c.child(text(e.message.clone()).font(font(11.0, 400.0)).color(hex("#ff6b6b")).wrap());
    }
    c = c.child(widgets::rule(t)).child(text("Selected node").font(font(13.0, 600.0)).color(t.text_primary));
    let node = (g.selected.len() == 1).then(|| g.node(&g.selected[0])).flatten();
    match node {
        None => {
            c = c.child(widgets::note("Select a node to edit its values. Click the pin beside a value to expose it as a slider on the effect.", t));
        }
        Some(node) => {
            c = c.child(text(node_title(node)).font(font(12.0, 600.0)).color(t.accent_text));
            for def in fig_fx::graph::param_schema_for(node) {
                let v = node.params.get(&def.key).copied().map_or(def.default, |v| v.as_number());
                let (nid, key, kind) = (node.id.clone(), def.key.clone(), def.kind);
                let set: widgets::OnValue = std::rc::Rc::new(move |s: &mut AppState, v: f64, _p: Phase| {
                    let value = match kind {
                        ParamKind::Bool => ParamValue::Bool(v != 0.0),
                        ParamKind::Color => ParamValue::Color(v as u32),
                        ParamKind::List => ParamValue::Choice(v as i32),
                        _ => ParamValue::Double(v),
                    };
                    with(s, |g| g.set_param(&nid, &key, value));
                });
                let control = match def.kind {
                    ParamKind::Bool => {
                        let set = set.clone();
                        widgets::switch(v != 0.0, t).on_press(move |s: &mut AppState| set(s, if v != 0.0 { 0.0 } else { 1.0 }, Phase::Up))
                    }
                    ParamKind::List => {
                        let key = format!("g.{}.{}", node.id, def.key);
                        let choices = def.choices.clone();
                        let k = key.clone();
                        widgets::dropdown(key, choices.get(v as usize).cloned().unwrap_or_default(), t).on_press(move |s: &mut AppState| {
                            let items = choices
                                .iter()
                                .enumerate()
                                .map(|(i, ch)| {
                                    let set = set.clone();
                                    Item::new(ch.clone(), move |s, _| set(s, i as f64, Phase::Up))
                                })
                                .collect();
                            crate::ui::menu::open_below(s, &k, items);
                        })
                    }
                    _ => widgets::slider(format!("g.{}.{}", node.id, def.key), v, def.min, if def.max > def.min { def.max } else { def.min + 1.0 }, def.default, set, t),
                };
                let (nid, key) = (node.id.clone(), def.key.clone());
                let promoted = g.is_promoted(&node.id, &def.key);
                c = c.child(
                    row()
                        .w_full()
                        .gap(6.0)
                        .center_y()
                        .child(text(def.label.clone()).font(font(11.5, 400.0)).color(t.text_muted).w(84.0).truncate())
                        .child(el().grow(1.0).child(control))
                        .child(widgets::toggle_button(&crate::ui::icons::PIN, promoted, 24.0, t).help("Expose as a control on the effect").on_press(move |s: &mut AppState| with(s, |g| g.toggle_promote(&nid, &key)))),
                );
            }
        }
    }
    // the controls the effect shows on a clip
    c = c.child(widgets::rule(t)).child(
        row()
            .w_full()
            .center_y()
            .child(text("Controls").font(font(13.0, 600.0)).color(t.text_primary).grow(1.0))
            .child(text(g.graph.promoted.len().to_string()).font(font(10.0, 400.0)).color(t.text_secondary)),
    );
    if g.graph.promoted.is_empty() {
        c = c.child(widgets::note("Nothing is exposed yet. Pin a value on a node to make it a slider.", t));
    }
    for (i, p) in g.graph.promoted.iter().enumerate() {
        let target = p.targets.first();
        let source = target
            .and_then(|tg| g.node(&tg.node_id).map(|n| (n, tg)))
            .and_then(|(n, tg)| fig_fx::graph::param_schema_for(n).into_iter().find(|d| d.key == tg.param).map(|d| format!("{}  ·  {}", node_title(n), d.label)))
            .unwrap_or_else(|| "Missing parameter".into());
        let more = if p.targets.len() > 1 { format!(" (+{})", p.targets.len() - 1) } else { String::new() };
        let (k1, k2, k3, k4, label) = (p.key.clone(), p.key.clone(), p.key.clone(), p.key.clone(), p.label.clone());
        let last = i + 1 == g.graph.promoted.len();
        c = c.child(
            row()
                .w_full()
                .padding(6.0)
                .gap(4.0)
                .radius(6.0)
                .fill(t.card_bg)
                .center_y()
                .child(
                    col()
                        .grow(1.0)
                        .min_w(0.0)
                        .child(text(p.label.clone()).font(font(12.0, 600.0)).color(t.text_primary).truncate())
                        .child(text(format!("{source}{more}")).font(font(10.0, 400.0)).color(t.text_muted).truncate()),
                )
                .child(widgets::icon_button(&crate::ui::icons::PENCIL, 12.0, 22.0, t).help("Rename").on_press(move |s: &mut AppState| {
                    let key = k1.clone();
                    crate::dialogs::prompt(s, "Rename control", "Label", &label, "Rename", move |s, name| {
                        let name = name.trim().to_string();
                        with(s, |g| {
                            if !name.is_empty() && g.graph.promoted.iter().any(|p| p.key == key && p.label != name) {
                                g.checkpoint(None);
                                if let Some(p) = g.graph.promoted.iter_mut().find(|p| p.key == key) {
                                    p.label = name;
                                }
                            }
                        });
                    });
                }))
                .child(widgets::icon_button(&crate::ui::icons::CHEVRON_UP, 12.0, 22.0, t).opacity(if i == 0 { 0.35 } else { 1.0 }).on_press(move |s: &mut AppState| with(s, |g| g.move_control(&k2, -1))))
                .child(widgets::icon_button(&crate::ui::icons::CHEVRON_DOWN, 12.0, 22.0, t).opacity(if last { 0.35 } else { 1.0 }).on_press(move |s: &mut AppState| with(s, |g| g.move_control(&k3, 1))))
                .child(widgets::icon_button(&crate::ui::icons::X, 12.0, 22.0, t).help("Stop exposing this value").on_press(move |s: &mut AppState| with(s, |g| g.remove_control(&k4)))),
        );
    }
    c.child(widgets::note("These are the sliders the effect shows on a clip. They can be keyframed and saved in presets.", t))
}

// ---- saving and leaving ----------------------------------------------------------------------

/// Saves the graph into the project's library. False when it isn't complete.
fn save(app: &mut AppState) -> bool {
    let Some(e) = app.editor.as_mut() else { return false };
    let Some(g) = e.graph.as_mut() else { return false };
    g.graph.name = if g.name.text.trim().is_empty() { "Untitled effect".into() } else { g.name.text.trim().to_string() };
    let result = e.graphs.save(&g.graph);
    if !result.is_valid() {
        g.message = Some("Fix the problems before saving.".into());
        g.confirm_close = false;
        return false;
    }
    g.baseline = g.graph.clone();
    g.dirty = false;
    g.message = Some(format!("Saved \u{201c}{}\u{201d}: it's in the Effects library under Custom", g.graph.name));
    e.media_rev += 1;
    true
}

/// Back to the editor; with unsaved changes it asks first.
pub fn back(app: &mut AppState) {
    let Some(e) = app.editor.as_mut() else { return };
    match e.graph.as_mut() {
        Some(g) if g.dirty => g.confirm_close = true,
        _ => e.graph = None,
    }
}

/// Escape: closes the search, then the leave prompt, then the editor (asking about unsaved changes).
pub fn escape(app: &mut AppState) -> bool {
    let Some(g) = ge(app) else { return false };
    if g.search.take().is_some() {
        return true;
    }
    if g.confirm_close {
        g.confirm_close = false;
        return true;
    }
    back(app);
    true
}

pub fn key(app: &mut AppState, ev: &KeyEvent) -> bool {
    use rune::Key;
    if ev.key == Key::Escape {
        return escape(app);
    }
    let Some(g) = ge(app) else { return false };
    if g.search.is_some() {
        return false;
    }
    let at = (f64::from(80.0 - g.pan.0) / f64::from(g.zoom), f64::from(80.0 - g.pan.1) / f64::from(g.zoom));
    match &ev.key {
        Key::Delete | Key::Backspace => g.delete_selection(),
        Key::Command('z') if ev.mods.shift => g.redo(),
        Key::Command('z') => g.undo(),
        Key::Command('y') => g.redo(),
        Key::Command('c') => g.copy(),
        Key::Command('v') => {
            let at = canvas_center(app);
            with(app, |g| g.paste(at));
        }
        Key::Command('d') => g.duplicate(),
        Key::Command('a') => g.selected = g.graph.nodes.iter().map(|n| n.id.clone()).collect(),
        Key::Command('s') => {
            save(app);
        }
        Key::Text(t) if t == "A" || (t == "a" && ev.mods.shift) => g.search = Some((focused(TextEdit::new("")), (at.0 as f32, at.1 as f32))),
        Key::Text(t) if t == "f" || t == "F" => frame_all(app),
        Key::Text(t) if t == "b" || t == "B" => {
            if let Some(id) = g.selected.first().cloned() {
                g.toggle_bypass(&id);
            }
        }
        _ => return false,
    }
    true
}
