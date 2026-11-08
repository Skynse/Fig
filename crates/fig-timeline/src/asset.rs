use std::path::Path;

use serde::{Deserialize, Serialize};

use crate::serde_util::{Metadata, new_id};
use crate::{Facet, FrameRate, Selectable, SelectionGroup, SelectionKey};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Default, Serialize, Deserialize)]
pub enum ProxyStatus {
    #[default]
    None,
    Pending,
    Ready,
    Failed,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Default, Serialize, Deserialize)]
pub enum MediaKind {
    #[default]
    Video,
    Audio,
    Image,
}

/// A file in the project's media library.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct MediaAsset {
    #[serde(default = "new_id")]
    pub id: String,
    #[serde(default)]
    pub kind: MediaKind,
    #[serde(default)]
    pub url: String,
    #[serde(default)]
    pub hash: String,
    #[serde(default)]
    pub duration_sec: f64,
    #[serde(default)]
    pub width: i32,
    #[serde(default)]
    pub height: i32,
    #[serde(default)]
    pub thumbnail: Option<String>,
    #[serde(default)]
    pub filmstrip: Option<String>,
    #[serde(default)]
    pub filmstrip_frame_width: i32,
    #[serde(default)]
    pub filmstrip_frame_height: i32,
    #[serde(default)]
    pub filmstrip_frame_count: i32,
    #[serde(default)]
    pub filmstrip_frame_interval_sec: f64,
    #[serde(default)]
    pub has_audio: bool,

    /// The media's native frame rate (probe fps), for information: clips play in real time whatever it is.
    #[serde(default)]
    pub source_rate: Option<FrameRate>,

    /// Normalized (0..1) audio peak magnitudes covering the full source duration, decoded once at import and drawn
    /// directly. Not saved: regenerated on open if missing.
    #[serde(skip)]
    pub waveform_peaks: Option<std::sync::Arc<[f32]>>,

    #[serde(default)]
    pub proxy_url: Option<String>,
    #[serde(default)]
    pub proxy_status: ProxyStatus,

    /// Set on a reversed copy (made when a clip is reversed): the id of the media it is a backwards copy of. Copies
    /// stay out of the media bin; a reversed clip plays its copy.
    #[serde(default)]
    pub reverse_of: Option<String>,

    /// The file is inside the project's own folder (collected, or made there), so it travels with the project; false
    /// for one the project points at elsewhere. Worked out by the editor, not saved.
    #[serde(skip)]
    pub in_project: bool,

    #[serde(default)]
    pub offline: bool,
    #[serde(default)]
    pub tags: Vec<String>,

    /// Offset into the source where usable content begins (OTIO available_range start). Previews and playback
    /// start from here rather than from frame zero.
    #[serde(default)]
    pub source_start_sec: f64,

    /// Source-format provenance preserved across imports.
    #[serde(default)]
    pub metadata: Metadata,
}

impl Default for MediaAsset {
    fn default() -> Self {
        MediaAsset {
            id: new_id(),
            kind: MediaKind::Video,
            url: String::new(),
            hash: String::new(),
            duration_sec: 0.0,
            width: 0,
            height: 0,
            thumbnail: None,
            filmstrip: None,
            filmstrip_frame_width: 0,
            filmstrip_frame_height: 0,
            filmstrip_frame_count: 0,
            filmstrip_frame_interval_sec: 0.0,
            has_audio: false,
            source_rate: None,
            waveform_peaks: None,
            proxy_url: None,
            proxy_status: ProxyStatus::None,
            reverse_of: None,
            in_project: false,
            offline: false,
            tags: Vec::new(),
            source_start_sec: 0.0,
            metadata: Metadata::new(),
        }
    }
}

impl MediaAsset {
    pub fn file_name(&self) -> &str {
        Path::new(&self.url).file_name().and_then(|n| n.to_str()).unwrap_or("")
    }

    /// True when a usable playback proxy file is ready.
    pub fn has_proxy(&self) -> bool {
        self.proxy_status == ProxyStatus::Ready
            && self.proxy_url.as_deref().is_some_and(|p| !p.is_empty() && Path::new(p).exists())
    }

    /// Path to use for preview video decode: the proxy when ready, otherwise the original. Audio and derived
    /// artifacts (filmstrip/peaks) always use `url`.
    pub fn playback_video_path(&self) -> &str {
        match &self.proxy_url {
            Some(p) if self.has_proxy() => p,
            _ => &self.url,
        }
    }
}

impl Selectable for MediaAsset {
    fn key(&self) -> SelectionKey {
        SelectionKey::new(SelectionGroup::Media, &self.id)
    }

    fn display_name(&self) -> &'static str {
        "Media"
    }

    fn facets(&self) -> Facet {
        Facet::SOURCE
    }
}
