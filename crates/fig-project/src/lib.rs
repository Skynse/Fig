//! Projects: what is saved, where, and how older saves are brought up to date.

pub mod migrations;
mod package;
mod paths;
mod store;
pub mod templates;

use chrono::{DateTime, FixedOffset};
use serde::{Deserialize, Serialize};

pub use package::*;
pub use store::*;

use fig_timeline::MediaAsset;
use fig_timeline::serde_util::{Metadata, datetime, new_id};
use fig_timeline::{EffectGraph, Timeline};

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct Project {
    #[serde(default = "new_id")]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default = "app_version")]
    pub app_version: String,

    /// What shape the saved timeline has, so a project from an earlier version is upgraded when it opens. 0: a
    /// video's sound is a linked audio clip. 1: it is part of the video clip (`migrations::embedded_audio`). 2: clips
    /// play in real time, not retimed to the timeline's frame rate (`migrations::real_time_media`). A file without
    /// the field is 0.
    #[serde(default)]
    pub format_version: i32,

    #[serde(default = "datetime::now", with = "datetime")]
    pub created_at: DateTime<FixedOffset>,
    #[serde(default = "datetime::now", with = "datetime")]
    pub updated_at: DateTime<FixedOffset>,

    /// Absolute path to the project card thumbnail (first composited timeline frame).
    #[serde(default)]
    pub thumbnail: Option<String>,

    #[serde(default)]
    pub media: Vec<MediaAsset>,
    #[serde(default)]
    pub timelines: Vec<Timeline>,
    #[serde(default)]
    pub export: ExportSettings,

    /// Effects the user built from nodes. Stored with the project so it opens anywhere.
    #[serde(default)]
    pub effect_graphs: Vec<EffectGraph>,

    /// Source-format provenance preserved across imports.
    #[serde(default)]
    pub metadata: Metadata,
}

fn app_version() -> String {
    "1.0".into()
}

impl Default for Project {
    fn default() -> Self {
        Project {
            id: new_id(),
            name: String::new(),
            app_version: app_version(),
            format_version: 0,
            created_at: datetime::now(),
            updated_at: datetime::now(),
            thumbnail: None,
            media: Vec::new(),
            timelines: Vec::new(),
            export: ExportSettings::default(),
            effect_graphs: Vec::new(),
            metadata: Metadata::new(),
        }
    }
}

impl Project {
    pub const CURRENT_FORMAT_VERSION: i32 = migrations::real_time_media::VERSION;

    pub fn create(name: impl Into<String>) -> Project {
        Project { name: name.into(), format_version: Self::CURRENT_FORMAT_VERSION, ..Default::default() }
    }

    pub fn find_media(&self, id: &str) -> Option<&MediaAsset> {
        self.media.iter().find(|m| m.id == id)
    }

    /// Points the asset at `old_path` (any case) to `new_path` and marks it online. False when none has that path.
    pub fn relink_media(&mut self, old_path: &str, new_path: &str) -> bool {
        let old = old_path.to_lowercase();
        let Some(asset) = self.media.iter_mut().find(|m| m.url.to_lowercase() == old) else {
            return false;
        };
        asset.url = new_path.into();
        asset.offline = false;
        true
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct ExportSettings {
    pub width: i32,
    pub height: i32,
    pub fps: i32,
    pub codec: String,
}

impl Default for ExportSettings {
    fn default() -> Self {
        ExportSettings { width: 1920, height: 1080, fps: 30, codec: "h264".into() }
    }
}
