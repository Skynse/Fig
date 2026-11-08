//! Effects the user builds from nodes: the node types, checking a graph and putting its nodes in order, running it,
//! and the library that publishes saved graphs to the effect catalog.

mod compile;
mod library;
mod nodes;

use std::collections::HashMap;
use std::sync::{Arc, LazyLock};

use fig_timeline::{EffectGraph, GraphNode, GraphPortType, ParamDef, ParamMap};

use crate::{Frame, ParamSet};

pub use compile::{CompileResult, GraphError, GraphErrorCode, GraphPlan, PlanStep, compile};
pub use library::{GraphEffectLibrary, LibraryError, build_entry};

pub const INPUT: &str = "input";
pub const OUTPUT: &str = "output";
pub const STOCK_EFFECT: &str = "effect";

#[derive(Clone, Debug, PartialEq)]
pub struct PortDef {
    pub id: &'static str,
    pub label: &'static str,
    pub kind: GraphPortType,
    pub required: bool,
}

/// What a node makes: its outputs by port id. An output may be an input passed straight through.
pub type Outputs = Vec<(&'static str, Arc<Frame>)>;

/// One node type. Stateless: everything it needs arrives in the [`NodeContext`].
pub struct Primitive {
    pub id: &'static str,
    pub display_name: &'static str,
    pub category: &'static str,
    pub description: &'static str,
    pub inputs: Vec<PortDef>,
    pub outputs: Vec<PortDef>,
    pub params: Vec<ParamDef>,
    pub(crate) eval: fn(&NodeContext) -> Outputs,
}

impl Primitive {
    pub fn input(&self, id: &str) -> Option<&PortDef> {
        self.inputs.iter().find(|p| p.id == id)
    }

    pub fn output(&self, id: &str) -> Option<&PortDef> {
        self.outputs.iter().find(|p| p.id == id)
    }
}

/// What a node sees for one evaluation.
pub struct NodeContext<'a> {
    pub node: &'a GraphNode,
    /// The picture the effect is applied to (what an Input node gives).
    pub source: &'a Arc<Frame>,
    inputs: HashMap<&'static str, Arc<Frame>>,
    /// The effective parameters: the node's values with promoted controls applied.
    pub params: ParamMap,
    schema: &'a [ParamDef],
    /// Seconds from the clip start (or the timeline's, for track effects).
    pub local_t: f64,
}

impl NodeContext<'_> {
    pub fn input(&self, port: &str) -> &Arc<Frame> {
        &self.inputs[port]
    }

    pub fn optional_input(&self, port: &str) -> Option<&Arc<Frame>> {
        self.inputs.get(port)
    }

    /// The parameters read through the node type's schema (defaulted and clamped).
    pub fn read(&self) -> ParamSet<'_> {
        ParamSet::new(self.schema, &self.params)
    }
}

static PRIMITIVES: LazyLock<Vec<Primitive>> = LazyLock::new(|| {
    let mut all = nodes::all();
    all.sort_by(|a, b| a.category.cmp(b.category).then(a.display_name.cmp(b.display_name)));
    all
});

/// Every node type, by category then name.
pub fn primitives() -> &'static [Primitive] {
    &PRIMITIVES
}

pub fn find_primitive(id: &str) -> Option<&'static Primitive> {
    PRIMITIVES.iter().find(|p| p.id == id)
}

/// The parameters a node has: its type's, except a stock-effect node, which has the wrapped effect's.
pub fn param_schema_for(node: &GraphNode) -> Vec<ParamDef> {
    if node.primitive_id == STOCK_EFFECT {
        return node
            .effect_type_id
            .as_deref()
            .and_then(crate::effects::find)
            .map(|e| e.params.clone())
            .unwrap_or_default();
    }
    find_primitive(&node.primitive_id).map(|p| p.params.clone()).unwrap_or_default()
}

/// A compiled graph, ready to run as an effect. An invalid graph passes the picture through.
pub struct GraphEffect {
    graph: EffectGraph,
    pub compiled: CompileResult,
}

impl GraphEffect {
    pub fn new(graph: &EffectGraph) -> GraphEffect {
        GraphEffect { graph: graph.clone(), compiled: compile(graph) }
    }

    /// Promoted control values (with their scale) by target node id, then parameter.
    fn overrides(&self, params: &ParamMap) -> HashMap<&str, ParamMap> {
        let mut map: HashMap<&str, ParamMap> = HashMap::new();
        for promoted in &self.graph.promoted {
            let Some(&value) = params.get(&promoted.key) else { continue };
            for target in &promoted.targets {
                let v = match value {
                    fig_timeline::ParamValue::Double(d) if target.scale != 1.0 => {
                        fig_timeline::ParamValue::Double(d * target.scale)
                    }
                    fig_timeline::ParamValue::Int(i) if target.scale != 1.0 => {
                        fig_timeline::ParamValue::Int((i as f64 * target.scale).round_ties_even() as i32)
                    }
                    other => other,
                };
                map.entry(target.node_id.as_str()).or_default().insert(target.param.clone(), v);
            }
        }
        map
    }

    /// Every node's effective parameters for one set of effect controls, by node id.
    pub fn resolve_node_params(&self, params: &ParamMap) -> HashMap<String, ParamMap> {
        let overrides = self.overrides(params);
        let Some(plan) = &self.compiled.plan else { return HashMap::new() };
        plan.steps.iter().map(|s| (s.node.id.clone(), merged(&s.node, &overrides))).collect()
    }

    /// Runs the graph over a frame. None when it leaves the frame as it is (or cannot run).
    pub fn apply(&self, frame: &Frame, params: &ParamMap, local_t: f64) -> Option<Frame> {
        let plan = self.compiled.plan.as_ref()?;
        if frame.width == 0 || frame.height == 0 {
            return None;
        }
        let overrides = self.overrides(params);
        let source = Arc::new(frame.clone());
        let mut values: HashMap<(String, &'static str), Arc<Frame>> = HashMap::new();
        for step in &plan.steps {
            let mut inputs: HashMap<&'static str, Arc<Frame>> = HashMap::new();
            for (port, (node, out)) in &step.inputs {
                inputs.insert(port, values.get(&(node.clone(), *out))?.clone());
            }
            if step.is_output() {
                let result = inputs.remove("image")?;
                return (!Arc::ptr_eq(&result, &source))
                    .then(|| Arc::try_unwrap(result).unwrap_or_else(|a| (*a).clone()));
            }
            let produced: Outputs = if step.bypassed {
                vec![(step.primitive.outputs[0].id, inputs[step.primitive.inputs[0].id].clone())]
            } else {
                let schema = param_schema_for(&step.node);
                let ctx = NodeContext {
                    node: &step.node,
                    source: &source,
                    inputs,
                    params: merged(&step.node, &overrides),
                    schema: &schema,
                    local_t,
                };
                (step.primitive.eval)(&ctx)
            };
            for (port, f) in produced {
                values.insert((step.node.id.clone(), port), f);
            }
        }
        None
    }
}

fn merged(node: &GraphNode, overrides: &HashMap<&str, ParamMap>) -> ParamMap {
    let mut p = node.params.clone();
    if let Some(extra) = overrides.get(node.id.as_str()) {
        p.extend(extra.iter().map(|(k, v)| (k.clone(), *v)));
    }
    p
}
