use std::path::Path;
use std::sync::Arc;

use fig_timeline::serde_util::{datetime, new_id_n};
use fig_timeline::{EffectGraph, ParamDef, ParamValue};

use super::{CompileResult, GraphEffect, compile, param_schema_for};
use crate::effects::{self, EffectEntry, EffectKind, Render};

#[derive(Debug, thiserror::Error)]
pub enum LibraryError {
    #[error("no such effect")]
    NotFound,
    #[error("the effect in this file is not valid")]
    Invalid,
    #[error(transparent)]
    Io(#[from] std::io::Error),
    #[error("the file is not an effect: {0}")]
    Json(#[from] serde_json::Error),
}

/// The user's graph effects for the open project. Saving checks a graph and publishes it to the effect catalog under
/// "My effects", so it can be dropped on a clip, keyframed and saved in presets like a built-in. The project owns the
/// list; take it back with [`into_graphs`](Self::into_graphs).
pub struct GraphEffectLibrary {
    owner: String,
    graphs: Vec<EffectGraph>,
}

impl GraphEffectLibrary {
    pub const CATEGORY: &str = "My effects";

    /// A library over the project's graphs, published at once.
    pub fn attach(graphs: Vec<EffectGraph>) -> GraphEffectLibrary {
        let lib = GraphEffectLibrary { owner: new_id_n(), graphs };
        lib.publish();
        lib
    }

    pub fn graphs(&self) -> &[EffectGraph] {
        &self.graphs
    }

    /// Stops publishing and gives the graphs back.
    pub fn into_graphs(self) -> Vec<EffectGraph> {
        effects::clear_user_effects(&self.owner);
        let mut me = std::mem::ManuallyDrop::new(self);
        std::mem::take(&mut me.graphs)
    }

    pub fn find(&self, id: &str) -> Option<&EffectGraph> {
        self.graphs.iter().find(|g| g.id == id)
    }

    /// Checks and stores a copy of the graph, replacing any saved graph with its id. An invalid graph is not stored;
    /// the result says why.
    pub fn save(&mut self, graph: &EffectGraph) -> CompileResult {
        let result = compile(graph);
        if !result.is_valid() {
            return result;
        }
        let mut copy = graph.clone();
        if copy.name.trim().is_empty() {
            copy.name = "Untitled effect".into();
        }
        copy.updated_at = datetime::utc_now();
        match self.graphs.iter().position(|g| g.id == copy.id) {
            Some(i) => self.graphs[i] = copy,
            None => self.graphs.push(copy),
        }
        self.publish();
        result
    }

    pub fn delete(&mut self, id: &str) -> bool {
        let before = self.graphs.len();
        self.graphs.retain(|g| g.id != id);
        let removed = self.graphs.len() != before;
        if removed {
            self.publish();
        }
        removed
    }

    /// Copies a saved graph under a new id and name.
    pub fn duplicate(&mut self, id: &str) -> Option<&EffectGraph> {
        let mut copy = self.find(id)?.clone();
        copy.id = new_id_n();
        copy.name = format!("{} copy", copy.name);
        self.graphs.push(copy);
        self.publish();
        self.graphs.last()
    }

    pub fn export(&self, id: &str, path: &Path) -> Result<(), LibraryError> {
        let graph = self.find(id).ok_or(LibraryError::NotFound)?;
        std::fs::write(path, serde_json::to_string_pretty(graph)?)?;
        Ok(())
    }

    /// Reads an exported graph. One whose id is already taken comes in as a new one.
    pub fn import(&mut self, path: &Path) -> Result<&EffectGraph, LibraryError> {
        let mut graph: EffectGraph = serde_json::from_str(&std::fs::read_to_string(path)?)?;
        if !compile(&graph).is_valid() {
            return Err(LibraryError::Invalid);
        }
        if self.find(&graph.id).is_some() {
            graph.id = new_id_n();
        }
        self.graphs.push(graph);
        self.publish();
        Ok(self.graphs.last().unwrap())
    }

    fn publish(&self) {
        effects::set_user_effects(&self.owner, self.graphs.iter().map(build_entry).collect());
    }
}

impl Drop for GraphEffectLibrary {
    fn drop(&mut self) {
        effects::clear_user_effects(&self.owner);
    }
}

/// The catalog entry a graph publishes: its promoted controls become the effect's parameters.
pub fn build_entry(graph: &EffectGraph) -> EffectEntry {
    let mut params = Vec::new();
    for promoted in &graph.promoted {
        let Some(target) = promoted.targets.first() else { continue };
        let Some(node) = graph.find_node(&target.node_id) else { continue };
        let Some(def) = param_schema_for(node).into_iter().find(|p| p.key == target.param) else { continue };
        let current = node.params.get(&def.key).map_or(def.default, |v| match *v {
            ParamValue::Bool(b) => b as u8 as f64,
            ParamValue::Color(c) => c as f64,
            ParamValue::Choice(i) | ParamValue::Int(i) => i as f64,
            ParamValue::Double(d) => d,
        });
        params.push(ParamDef {
            key: promoted.key.clone(),
            label: if promoted.label.trim().is_empty() { def.label.clone() } else { promoted.label.clone() },
            kind: def.kind,
            min: promoted.min.unwrap_or(def.min),
            max: promoted.max.unwrap_or(def.max),
            default: promoted.default.unwrap_or(current),
            choices: def.choices.clone(),
            format: def.format.clone(),
        });
    }
    let runner = Arc::new(GraphEffect::new(graph));
    EffectEntry {
        type_id: graph.type_id(),
        display_name: graph.name.clone(),
        category: GraphEffectLibrary::CATEGORY.into(),
        icon: "wand-sparkles".into(),
        description: "A custom effect.".into(),
        kind: EffectKind::Video,
        is_custom: true,
        params,
        render: Render::Custom(Arc::new(move |frame, params, t| runner.apply(frame, params, t))),
    }
}
