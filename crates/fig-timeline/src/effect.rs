use indexmap::IndexMap;
use serde::{Deserialize, Serialize};

use super::{KeyframeMap, ParamValue};
use crate::serde_util::{datetime, new_id, new_id_n, one, yes};

/// Typed parameters keyed by name (the schema lives in the catalog).
pub type ParamMap = IndexMap<String, ParamValue>;

/// One effect in an ordered track or legacy clip filter stack.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct EffectInstance {
    #[serde(default = "new_id")]
    pub id: String,
    #[serde(default)]
    pub type_id: String,
    #[serde(default = "yes")]
    pub enabled: bool,
    #[serde(default)]
    pub order: i32,
    /// Optional active range in the owner's time domain. Track ranges use absolute timeline seconds; legacy clip
    /// ranges use seconds from the clip start. Both none means the whole owner. Keyframes use the same time domain
    /// and hold their first and last values outside their range.
    #[serde(default)]
    pub start_sec: Option<f64>,
    #[serde(default)]
    pub dur_sec: Option<f64>,
    #[serde(default)]
    pub params: ParamMap,
    /// Optional keyframe tracks keyed by parameter name. When a track exists for a param, its value is evaluated at
    /// render time instead of the constant in `params`.
    #[serde(default)]
    pub keyframes: KeyframeMap,
}

impl Default for EffectInstance {
    fn default() -> Self {
        EffectInstance {
            id: new_id(),
            type_id: String::new(),
            enabled: true,
            order: 0,
            start_sec: None,
            dur_sec: None,
            params: ParamMap::new(),
            keyframes: KeyframeMap::new(),
        }
    }
}

impl EffectInstance {
    pub fn has_range(&self) -> bool {
        self.start_sec.is_some() && self.dur_sec.is_some_and(|d| d > 0.0)
    }

    /// True when the effect is in force at a time in its owner's time domain.
    pub fn is_active_at(&self, local_t: f64) -> bool {
        match (self.start_sec, self.dur_sec) {
            (Some(start), Some(dur)) if dur > 0.0 => local_t >= start - 1e-9 && local_t <= start + dur + 1e-9,
            _ => true,
        }
    }

    /// A copy with a new id. (`clone()` keeps the id, for undo snapshots.)
    pub fn duplicate(&self) -> EffectInstance {
        EffectInstance { id: new_id(), ..self.clone() }
    }
}

/// Optional transition attached to a clip edge.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct TransitionRef {
    #[serde(default)]
    pub type_id: String,
    /// Total transition duration, centered equally around the cut.
    #[serde(default = "half")]
    pub duration_sec: f64,
    #[serde(default)]
    pub params: ParamMap,
}

fn half() -> f64 {
    0.5
}

impl Default for TransitionRef {
    fn default() -> Self {
        TransitionRef { type_id: String::new(), duration_sec: 0.5, params: ParamMap::new() }
    }
}

/// What flows along a wire. Wires only connect ports of the same type.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum GraphPortType {
    Image,
    Mask,
}

/// A user-built effect: primitive nodes wired into an acyclic graph that ends in one Output node. Saved graphs
/// become catalog effects with the type id `graph:<id>`, so clips, keyframes, presets and the inspector treat them
/// like any built-in.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct EffectGraph {
    #[serde(default = "new_id_n")]
    pub id: String,
    #[serde(default = "untitled")]
    pub name: String,
    #[serde(default = "schema_one")]
    pub schema_version: i32,
    #[serde(default = "datetime::utc_now", with = "datetime")]
    pub updated_at: chrono::DateTime<chrono::FixedOffset>,
    #[serde(default)]
    pub nodes: Vec<GraphNode>,
    #[serde(default)]
    pub edges: Vec<GraphEdge>,
    /// The controls this effect shows on a clip, each bound to one or more node parameters.
    #[serde(default)]
    pub promoted: Vec<PromotedParam>,
}

fn untitled() -> String {
    "Untitled effect".into()
}

fn schema_one() -> i32 {
    1
}

impl Default for EffectGraph {
    fn default() -> Self {
        EffectGraph {
            id: new_id_n(),
            name: untitled(),
            schema_version: 1,
            updated_at: datetime::utc_now(),
            nodes: Vec::new(),
            edges: Vec::new(),
            promoted: Vec::new(),
        }
    }
}

impl EffectGraph {
    pub const TYPE_ID_PREFIX: &str = "graph:";

    /// The effect type id clips store while this graph is in the library.
    pub fn type_id(&self) -> String {
        format!("{}{}", Self::TYPE_ID_PREFIX, self.id)
    }

    pub fn is_graph_type_id(type_id: Option<&str>) -> bool {
        type_id.is_some_and(|t| t.starts_with(Self::TYPE_ID_PREFIX))
    }

    pub fn find_node(&self, id: &str) -> Option<&GraphNode> {
        self.nodes.iter().find(|n| n.id == id)
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct GraphNode {
    #[serde(default = "new_id_n")]
    pub id: String,
    /// The primitive this node runs (for example `blur`, `mix`, `effect`).
    #[serde(default)]
    pub primitive_id: String,
    /// For the `effect` primitive only: the stock effect it wraps. `params` are that effect's.
    #[serde(default)]
    pub effect_type_id: Option<String>,
    #[serde(default)]
    pub params: ParamMap,
    /// Passes the input straight through. Only meaningful for nodes with one input and one output of the same type.
    #[serde(default)]
    pub bypass: bool,
    /// Editor position in canvas units. View state only; rendering never reads it.
    #[serde(default)]
    pub x: f64,
    #[serde(default)]
    pub y: f64,
}

impl Default for GraphNode {
    fn default() -> Self {
        GraphNode {
            id: new_id_n(),
            primitive_id: String::new(),
            effect_type_id: None,
            params: ParamMap::new(),
            bypass: false,
            x: 0.0,
            y: 0.0,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct GraphEdge {
    pub from_node_id: String,
    pub from_port: String,
    pub to_node_id: String,
    pub to_port: String,
}

/// One control the saved effect exposes. A single control may drive several node parameters.
#[derive(Clone, Debug, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct PromotedParam {
    /// The key on the effect (the `ParamDef::key` and the key in `EffectInstance::params`).
    pub key: String,
    pub label: String,
    pub targets: Vec<PromotedTarget>,
    /// Optional overrides of the first target's range and default.
    pub min: Option<f64>,
    pub max: Option<f64>,
    pub default: Option<f64>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct PromotedTarget {
    #[serde(default)]
    pub node_id: String,
    #[serde(default)]
    pub param: String,
    /// Multiplier applied to numeric values, so one control can drive a node in the opposite direction (-1).
    #[serde(default = "one")]
    pub scale: f64,
}
