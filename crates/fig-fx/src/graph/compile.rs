use std::collections::{HashMap, HashSet};

use fig_timeline::{EffectGraph, GraphNode};

use super::{OUTPUT, Primitive, STOCK_EFFECT, find_primitive, param_schema_for};
use crate::effects::{self, EffectKind};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum GraphErrorCode {
    NoOutput,
    MultipleOutputs,
    UnknownPrimitive,
    UnknownEffect,
    DuplicateNode,
    BadEdge,
    TypeMismatch,
    InputAlreadyConnected,
    MissingInput,
    Cycle,
    BadPromotedParam,
}

/// A problem the editor can point at: a node when `node_id` is set, the whole graph otherwise.
#[derive(Clone, Debug, PartialEq)]
pub struct GraphError {
    pub code: GraphErrorCode,
    pub message: String,
    pub node_id: Option<String>,
}

fn err(code: GraphErrorCode, message: impl Into<String>, node: Option<&str>) -> GraphError {
    GraphError { code, message: message.into(), node_id: node.map(Into::into) }
}

/// One node to run, in dependency order.
pub struct PlanStep {
    pub node: GraphNode,
    pub primitive: &'static Primitive,
    /// Input port id → the node output feeding it.
    pub inputs: Vec<(&'static str, (String, &'static str))>,
    /// The node is bypassed and its input passes straight through.
    pub bypassed: bool,
}

impl PlanStep {
    pub fn is_output(&self) -> bool {
        self.node.primitive_id == OUTPUT
    }
}

pub struct GraphPlan {
    pub steps: Vec<PlanStep>,
}

#[derive(Default)]
pub struct CompileResult {
    pub plan: Option<GraphPlan>,
    pub errors: Vec<GraphError>,
    /// Nodes that do not feed the output. They are ignored when rendering.
    pub unused_node_ids: Vec<String>,
}

impl CompileResult {
    pub fn is_valid(&self) -> bool {
        self.plan.is_some() && self.errors.is_empty()
    }
}

/// Checks a graph and turns it into an ordered plan. Pure: no rendering, no side effects.
pub fn compile(graph: &EffectGraph) -> CompileResult {
    use GraphErrorCode::*;
    let mut errors = Vec::new();
    let mut by_id: HashMap<&str, &GraphNode> = HashMap::new();
    let mut prims: HashMap<&str, &'static Primitive> = HashMap::new();
    for node in &graph.nodes {
        if by_id.insert(&node.id, node).is_some() {
            errors.push(err(DuplicateNode, "Two nodes share the same id.", Some(&node.id)));
            continue;
        }
        let Some(p) = find_primitive(&node.primitive_id) else {
            errors.push(err(UnknownPrimitive, format!("Unknown node type \"{}\".", node.primitive_id), Some(&node.id)));
            continue;
        };
        prims.insert(&node.id, p);
        if node.primitive_id == STOCK_EFFECT {
            let entry = node
                .effect_type_id
                .as_deref()
                .filter(|t| !EffectGraph::is_graph_type_id(Some(t)))
                .and_then(effects::find);
            if entry.is_none_or(|e| e.kind == EffectKind::Audio) {
                errors.push(err(UnknownEffect, "This effect node has no usable effect.", Some(&node.id)));
            }
        }
    }
    let outputs: Vec<&GraphNode> = graph.nodes.iter().filter(|n| n.primitive_id == OUTPUT).collect();
    match outputs.len() {
        0 => errors.push(err(NoOutput, "The graph has no Output node.", None)),
        1 => {}
        _ => {
            for extra in &outputs[1..] {
                errors.push(err(MultipleOutputs, "Only one Output node is allowed.", Some(&extra.id)));
            }
        }
    }
    // wires: endpoints, ports, types, one wire per input
    let mut incoming: HashMap<(&str, &str), (&str, &'static str)> = HashMap::new();
    for edge in &graph.edges {
        let (Some(from), Some(to)) = (prims.get(edge.from_node_id.as_str()), prims.get(edge.to_node_id.as_str()))
        else {
            errors.push(err(BadEdge, "A wire points at a node that does not exist.", Some(&edge.to_node_id)));
            continue;
        };
        let (Some(out), Some(inp)) = (from.output(&edge.from_port), to.input(&edge.to_port)) else {
            errors.push(err(BadEdge, "A wire uses a port that does not exist.", Some(&edge.to_node_id)));
            continue;
        };
        if out.kind != inp.kind {
            let msg = format!("{:?} output cannot connect to a {:?} input.", out.kind, inp.kind);
            errors.push(err(TypeMismatch, msg, Some(&edge.to_node_id)));
            continue;
        }
        if incoming.insert((&edge.to_node_id, inp.id), (&edge.from_node_id, out.id)).is_some() {
            errors.push(err(
                InputAlreadyConnected,
                format!("\"{}\" already has a wire.", inp.label),
                Some(&edge.to_node_id),
            ));
        }
    }
    // reachability from the output; post-order is dependency order, a back edge is a cycle
    let mut order: Vec<&GraphNode> = Vec::new();
    if let Some(output) = outputs.first() {
        let mut state: HashMap<&str, u8> = HashMap::new();
        let mut reported = false;
        fn visit<'g>(
            node: &'g GraphNode,
            state: &mut HashMap<&'g str, u8>,
            reported: &mut bool,
            prims: &HashMap<&str, &'static Primitive>,
            incoming: &HashMap<(&str, &str), (&'g str, &'static str)>,
            by_id: &HashMap<&str, &'g GraphNode>,
            order: &mut Vec<&'g GraphNode>,
            errors: &mut Vec<GraphError>,
        ) {
            if let Some(&s) = state.get(node.id.as_str()) {
                if s == 1 && !*reported {
                    *reported = true;
                    errors.push(err(GraphErrorCode::Cycle, "The wires form a loop.", Some(&node.id)));
                }
                return;
            }
            state.insert(&node.id, 1);
            if let Some(p) = prims.get(node.id.as_str()) {
                for port in &p.inputs {
                    if let Some((src, _)) = incoming.get(&(node.id.as_str(), port.id))
                        && let Some(up) = by_id.get(src)
                    {
                        visit(up, state, reported, prims, incoming, by_id, order, errors);
                    }
                }
            }
            state.insert(&node.id, 2);
            order.push(node);
        }
        visit(output, &mut state, &mut reported, &prims, &incoming, &by_id, &mut order, &mut errors);
    }
    // required inputs on nodes that matter
    for node in &order {
        if let Some(p) = prims.get(node.id.as_str()) {
            for port in p.inputs.iter().filter(|p| p.required) {
                if !incoming.contains_key(&(node.id.as_str(), port.id)) {
                    errors.push(err(MissingInput, format!("\"{}\" is not connected.", port.label), Some(&node.id)));
                }
            }
        }
    }
    // promoted controls
    let mut keys = HashSet::new();
    for promoted in &graph.promoted {
        if promoted.key.trim().is_empty() || !keys.insert(promoted.key.as_str()) {
            errors.push(err(BadPromotedParam, format!("Control \"{}\" needs a unique key.", promoted.label), None));
            continue;
        }
        if promoted.targets.is_empty() {
            errors.push(err(
                BadPromotedParam,
                format!("Control \"{}\" is not linked to anything.", promoted.label),
                None,
            ));
            continue;
        }
        for t in &promoted.targets {
            let ok =
                by_id.get(t.node_id.as_str()).is_some_and(|n| param_schema_for(n).iter().any(|p| p.key == t.param));
            if !ok {
                let msg = format!("Control \"{}\" points at a parameter that no longer exists.", promoted.label);
                errors.push(err(BadPromotedParam, msg, Some(&t.node_id)));
            }
        }
    }
    let used: HashSet<&str> = order.iter().map(|n| n.id.as_str()).collect();
    let unused: Vec<String> =
        graph.nodes.iter().filter(|n| !used.contains(n.id.as_str())).map(|n| n.id.clone()).collect();
    if !errors.is_empty() || order.is_empty() {
        return CompileResult { plan: None, errors, unused_node_ids: unused };
    }
    let steps = order
        .into_iter()
        .map(|node| {
            let p = prims[node.id.as_str()];
            let inputs = p
                .inputs
                .iter()
                .filter_map(|port| {
                    incoming.get(&(node.id.as_str(), port.id)).map(|(n, o)| (port.id, (n.to_string(), *o)))
                })
                .collect();
            let bypassed =
                node.bypass && p.inputs.len() == 1 && p.outputs.len() == 1 && p.inputs[0].kind == p.outputs[0].kind;
            PlanStep { node: node.clone(), primitive: p, inputs, bypassed }
        })
        .collect();
    CompileResult { plan: Some(GraphPlan { steps }), errors, unused_node_ids: unused }
}
