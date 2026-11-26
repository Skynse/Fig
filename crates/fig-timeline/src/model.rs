use serde::{Deserialize, Serialize};

use super::{Clip, EffectInstance, Facet, FrameRate, FrameShape, Marker, Selectable, SelectionGroup, SelectionKey};
use crate::serde_util::{Metadata, effect_list, new_id, yes};

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct Timeline {
    #[serde(default = "new_id")]
    pub id: String,

    /// What the timeline's tab says; none shows "Timeline 1", "Timeline 2"… by its place in the project.
    #[serde(default)]
    pub name: Option<String>,

    /// The project frame rate: the frame grid (snapping, timecode, frame steps) and the export rate. Clips play in
    /// real time whatever it is.
    #[serde(default)]
    pub rate: FrameRate,

    /// The user set `rate`, so importing media no longer picks it.
    #[serde(default)]
    pub rate_chosen: bool,

    /// The picture's shape; Auto follows the first video (every project before this setting).
    #[serde(default)]
    pub shape: FrameShape,
    #[serde(default)]
    pub revision: i32,
    #[serde(default)]
    pub is_autosave: bool,
    #[serde(default)]
    pub tracks: Vec<Track>,

    /// The main track (the lowest video track) keeps its clips end to end from the start: deleting, trimming or
    /// moving a clip there closes the gap, and a clip put there pushes the later ones along.
    #[serde(default = "yes")]
    pub main_track_magnet: bool,

    /// The song's tempo, as last given to match a clip's speed to it (none until then).
    #[serde(default)]
    pub song_bpm: Option<f64>,

    /// Timeline start timecode (seconds); 0 when the program starts at zero.
    #[serde(default)]
    pub global_start_sec: f64,

    /// Editorial annotations pinned to the timeline (absolute seconds).
    #[serde(default)]
    pub markers: Vec<Marker>,

    /// Source-format provenance preserved across imports (reels, comments, etc.).
    #[serde(default)]
    pub metadata: Metadata,
}

impl Default for Timeline {
    fn default() -> Self {
        Timeline {
            id: new_id(),
            name: None,
            rate: FrameRate::default(),
            rate_chosen: false,
            shape: FrameShape::Auto,
            revision: 0,
            is_autosave: false,
            tracks: Vec::new(),
            main_track_magnet: true,
            song_bpm: None,
            global_start_sec: 0.0,
            markers: Vec::new(),
            metadata: Metadata::new(),
        }
    }
}

impl Timeline {
    /// Where the last clip ends.
    pub fn duration_sec(&self) -> f64 {
        self.clips().map(Clip::end_sec).fold(0.0, f64::max)
    }

    /// Every clip on every track.
    pub fn clips(&self) -> impl Iterator<Item = &Clip> {
        self.tracks.iter().flat_map(|t| t.clips.iter())
    }

    pub fn clips_mut(&mut self) -> impl Iterator<Item = &mut Clip> {
        self.tracks.iter_mut().flat_map(|t| t.clips.iter_mut())
    }

    pub fn find_clip(&self, id: &str) -> Option<&Clip> {
        self.clips().find(|c| c.id == id)
    }

    pub fn find_clip_mut(&mut self, id: &str) -> Option<&mut Clip> {
        self.clips_mut().find(|c| c.id == id)
    }

    /// The track holding the clip, and the clip's index on it.
    pub fn locate_clip(&self, id: &str) -> Option<(usize, usize)> {
        self.tracks
            .iter()
            .enumerate()
            .find_map(|(t, track)| track.clips.iter().position(|c| c.id == id).map(|c| (t, c)))
    }

    pub fn find_track(&self, id: &str) -> Option<&Track> {
        self.tracks.iter().find(|t| t.id == id)
    }

    pub fn find_track_mut(&mut self, id: &str) -> Option<&mut Track> {
        self.tracks.iter_mut().find(|t| t.id == id)
    }

    /// Visible video tracks with their list index, top of the picture first. A track drawn higher covers the ones
    /// below it, so the first track is the top of the picture and the last the bottom.
    pub fn top_down(&self) -> impl Iterator<Item = (usize, &Track)> {
        self.tracks.iter().enumerate().filter(|(_, t)| t.kind == TrackKind::Video && t.visible)
    }

    /// Visible video tracks with their list index, bottom of the picture first (paint order).
    pub fn bottom_up(&self) -> impl Iterator<Item = (usize, &Track)> {
        self.tracks.iter().enumerate().rev().filter(|(_, t)| t.kind == TrackKind::Video && t.visible)
    }
}

/// Sorts layers that know their track index into paint order: the lowest track first.
pub fn paint_key(track_index: usize) -> i64 {
    -(track_index as i64)
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Default, Serialize, Deserialize)]
pub enum TrackKind {
    #[default]
    Video,
    Audio,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct Track {
    #[serde(default = "new_id")]
    pub id: String,
    #[serde(default)]
    pub kind: TrackKind,
    #[serde(default)]
    pub index: i32,
    #[serde(default)]
    pub name: Option<String>,

    /// Video tracks: hides/shows the video clip content in the preview.
    #[serde(default = "yes")]
    pub visible: bool,

    /// Mutes the track's sound: an audio track's clips, or a video track's clips' own sound.
    #[serde(default)]
    pub muted: bool,

    /// Independent effect regions on this track, timed in timeline seconds.
    #[serde(default, deserialize_with = "effect_list")]
    pub effects: Vec<EffectInstance>,

    #[serde(default)]
    pub clips: Vec<Clip>,

    /// Editorial annotations pinned to the track (absolute seconds).
    #[serde(default)]
    pub markers: Vec<Marker>,

    /// Source-format provenance preserved across imports.
    #[serde(default)]
    pub metadata: Metadata,
}

impl Track {
    pub fn new(kind: TrackKind) -> Track {
        Track {
            id: new_id(),
            kind,
            index: 0,
            name: None,
            visible: true,
            muted: false,
            effects: Vec::new(),
            clips: Vec::new(),
            markers: Vec::new(),
            metadata: Metadata::new(),
        }
    }

    /// A track effect's time domain starts at the timeline's 0.
    pub fn start_sec(&self) -> f64 {
        0.0
    }

    /// Long enough to hold every clip and effect region (at least 0.1 s).
    pub fn dur_sec(&self) -> f64 {
        let clips = self.clips.iter().map(Clip::end_sec).fold(0.0, f64::max);
        let effects =
            self.effects.iter().map(|e| e.start_sec.unwrap_or(0.0) + e.dur_sec.unwrap_or(0.0)).fold(0.0, f64::max);
        clips.max(effects).max(0.1)
    }
}

impl Selectable for Track {
    fn key(&self) -> SelectionKey {
        SelectionKey::new(SelectionGroup::Track, &self.id)
    }

    fn display_name(&self) -> &'static str {
        "Track"
    }

    fn facets(&self) -> Facet {
        Facet::TRACK | Facet::EFFECTS
    }
}
