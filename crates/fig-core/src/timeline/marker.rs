use serde::{Deserialize, Serialize};

use super::{Facet, Selectable, SelectionGroup, SelectionKey};
use crate::serde_util::{Metadata, new_id, yes};

/// An editorial annotation pinned to a point (or short span) of a clip, track, or timeline. `start_sec` is relative
/// to the object it sits on: for a clip marker it is an offset from the clip start; for track and timeline markers
/// it is an absolute position on the timeline.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct Marker {
    #[serde(default = "new_id")]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub start_sec: f64,
    #[serde(default)]
    pub dur_sec: f64,
    #[serde(default = "default_color")]
    pub color: String,
    /// For range markers: whether "Export marked ranges" offers this range.
    #[serde(default = "yes")]
    pub include_in_export: bool,
    /// For range markers: the output file name to use when exporting (none: derived from the name).
    #[serde(default)]
    pub output_name: Option<String>,
    /// Optional source-format provenance (e.g. cmx_3600 reel / color).
    #[serde(default)]
    pub metadata: Option<Metadata>,
}

fn default_color() -> String {
    "#ffd60a".into()
}

impl Default for Marker {
    fn default() -> Self {
        Marker {
            id: new_id(),
            name: String::new(),
            start_sec: 0.0,
            dur_sec: 0.0,
            color: default_color(),
            include_in_export: true,
            output_name: None,
            metadata: None,
        }
    }
}

impl Marker {
    /// Metadata key that tags a marker as produced by beat analysis (so regenerating replaces it).
    pub const BEAT_ANALYSIS_KEY: &str = "fig.beatAnalysis";

    /// A range marker spans time (it has a duration); a point marker (bookmark) does not.
    pub fn is_range(&self) -> bool {
        self.dur_sec > 0.0
    }

    pub fn end_sec(&self) -> f64 {
        self.start_sec + self.dur_sec
    }

    /// True for markers produced by beat analysis rather than placed by hand.
    pub fn is_generated_beat(&self) -> bool {
        self.metadata.as_ref().is_some_and(|m| m.contains_key(Self::BEAT_ANALYSIS_KEY))
    }

    /// A copy with a new id.
    pub fn duplicate(&self) -> Marker {
        Marker { id: new_id(), ..self.clone() }
    }
}

impl Selectable for Marker {
    fn key(&self) -> SelectionKey {
        SelectionKey::new(SelectionGroup::Marker, &self.id)
    }

    fn display_name(&self) -> &'static str {
        if self.is_range() { "Range marker" } else { "Point marker" }
    }

    fn facets(&self) -> Facet {
        Facet::MARKER
    }
}
