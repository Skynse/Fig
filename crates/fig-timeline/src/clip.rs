use serde::{Deserialize, Serialize};

use super::{
    EffectInstance, Facet, FrameRate, KeyframeMap, Marker, Selectable, SelectionGroup, SelectionKey, TransitionRef,
};
use crate::serde_util::{Metadata, effect_list, new_id, one, yes};

/// A clip on a track: what every kind shares, and its kind's own fields in `body`.
///
/// Saved as the C# build saved it: one object whose `"kind"` (first) says which body it has, the body's fields and
/// the shared ones side by side.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct Clip {
    #[serde(flatten)]
    pub body: ClipBody,

    #[serde(default = "new_id")]
    pub id: String,

    /// Clips sharing the same group id are linked and move/resize/cut/delete together (e.g. a video clip and its
    /// companion audio clip).
    #[serde(default)]
    pub link_group_id: Option<String>,

    /// When false the clip is ignored by playback, mixing, and compositing.
    #[serde(default = "yes")]
    pub enabled: bool,

    /// The source media's frame rate, for information. A clip plays in real time whatever it is: its source time
    /// advances by `speed` alone.
    #[serde(default)]
    pub source_rate: Option<FrameRate>,

    #[serde(default)]
    pub start_sec: f64,
    #[serde(default)]
    pub dur_sec: f64,
    #[serde(default = "one")]
    pub speed: f64,
    #[serde(default = "one")]
    pub volume: f64,
    #[serde(default = "one")]
    pub opacity: f64,

    /// Seconds of linear fade-in from the clip start (0 = none). Multiplies opacity for video and volume for audio.
    #[serde(default)]
    pub fade_in_sec: f64,
    /// Seconds of linear fade-out to the clip end (0 = none). Multiplies opacity for video and volume for audio.
    #[serde(default)]
    pub fade_out_sec: f64,

    /// Ordered filter stack (legacy: effects now live on tracks; see `project::migrations`).
    #[serde(default, deserialize_with = "effect_list")]
    pub effects: Vec<EffectInstance>,

    /// Optional transition into this clip from the previous abutting clip.
    #[serde(default)]
    pub transition_in: Option<TransitionRef>,
    /// Optional transition out of this clip into the next abutting clip.
    #[serde(default)]
    pub transition_out: Option<TransitionRef>,

    /// Editorial annotations pinned to the clip (seconds relative to clip start).
    #[serde(default)]
    pub markers: Vec<Marker>,

    /// Optional keyframe tracks for clip automation properties (see `AutomationKeys`), keyed by property name. When
    /// a track exists for a key, its value is sampled at render time instead of the constant property. Times are
    /// seconds relative to the clip start.
    #[serde(default)]
    pub keyframes: KeyframeMap,

    /// Source-format provenance preserved across imports.
    #[serde(default)]
    pub metadata: Metadata,
}

/// What kind of clip it is, with that kind's own fields.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "lowercase")]
pub enum ClipBody {
    Video(VideoBody),
    Audio(AudioBody),
    Text(TextBody),
    Image(ImageBody),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum ClipKind {
    Video,
    Audio,
    Text,
    Image,
}

#[derive(Clone, Debug, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct VideoBody {
    pub source_id: String,
    pub src_in_sec: f64,
    pub src_out_sec: f64,
    /// True when the clip plays its file's sound, drawn as a strip under the picture. False when the file is
    /// silent, or its sound was detached to an audio clip of its own.
    pub plays_audio: bool,
    /// Slow motion glides: where the clip plays slower than its file's frames, the two real frames around each
    /// moment are blended instead of one being repeated.
    pub blend_frames: bool,
    /// Fast motion smears: where the clip plays faster than its file's frames, the frames each step passes over are
    /// averaged. 0 is off, 1 is a full shutter (every frame passed).
    pub motion_blur: f64,
    /// Seconds the sound fades in from the clip start, apart from the picture's fade (0 = none).
    pub audio_fade_in_sec: f64,
    /// Seconds the sound fades out to the clip end, apart from the picture's fade (0 = none).
    pub audio_fade_out_sec: f64,
    /// Normalized crop insets from each edge (0..1).
    pub crop_l: f64,
    pub crop_t: f64,
    pub crop_r: f64,
    pub crop_b: f64,
}

impl VideoBody {
    /// True when the clip smooths its frames for its speed at all.
    pub fn smooths(&self) -> bool {
        self.blend_frames || self.motion_blur > 0.0
    }
}

#[derive(Clone, Debug, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct AudioBody {
    pub source_id: String,
    pub src_in_sec: f64,
    pub src_out_sec: f64,
}

/// Words on the frame: drawn, not decoded, and composited over the layers below.
#[derive(Clone, Debug, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct TextBody {
    pub text: String,
    pub style: TextStyle,
    pub layout: LayerTransform,
    /// A caption: written from speech, and what a subtitle export (SRT, TXT) collects.
    pub is_caption: bool,
    /// When each word of `text` is said, for lighting the spoken word (`TextStyle::highlight`). Empty for typed
    /// text.
    pub words: Vec<TimedWord>,
}

/// A still picture from the library, held for as long as the clip lasts.
#[derive(Clone, Debug, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct ImageBody {
    pub source_id: String,
    pub layout: LayerTransform,
    pub crop_l: f64,
    pub crop_t: f64,
    pub crop_r: f64,
    pub crop_b: f64,
}

impl ImageBody {
    /// How long a still lasts when it is first placed.
    pub const DEFAULT_DURATION_SEC: f64 = 5.0;
}

/// Where a layer sits on the frame. Centre is a fraction of the frame; rotation is in degrees.
#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct LayerTransform {
    pub center_x: f64,
    pub center_y: f64,
    pub scale: f64,
    pub rotation: f64,
}

impl Default for LayerTransform {
    fn default() -> Self {
        LayerTransform { center_x: 0.5, center_y: 0.5, scale: 1.0, rotation: 0.0 }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Default, Serialize, Deserialize)]
pub enum TextAlign {
    Left,
    #[default]
    Center,
    Right,
}

impl TextAlign {
    pub const NAMES: [&str; 3] = ["Left", "Center", "Right"];

    pub fn from_index(i: i32) -> TextAlign {
        match i.clamp(0, 2) {
            0 => TextAlign::Left,
            1 => TextAlign::Center,
            _ => TextAlign::Right,
        }
    }
}

/// How a text clip is set. Size is a share of the frame height and the wrap width a share of the frame width, so
/// text looks the same at every resolution.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct TextStyle {
    pub font_family: String,
    pub bold: bool,
    pub italic: bool,
    pub size_percent: f64,
    /// ARGB.
    pub color: u32,
    pub align: TextAlign,
    pub shadow: bool,
    pub background: bool,
    pub background_color: u32,
    pub outline: bool,
    pub outline_color: u32,
    pub outline_percent: f64,
    pub highlight: bool,
    pub highlight_color: u32,
    pub wrap_percent: f64,
    pub uppercase: bool,
}

impl Default for TextStyle {
    fn default() -> Self {
        TextStyle {
            font_family: "Inter".into(),
            bold: true,
            italic: false,
            size_percent: 8.0,
            color: 0xFFFFFFFF,
            align: TextAlign::Center,
            shadow: true,
            background: false,
            background_color: 0xB0000000,
            outline: false,
            outline_color: 0xFF000000,
            outline_percent: 12.0,
            highlight: false,
            highlight_color: 0xFFFFD60A,
            wrap_percent: 0.0,
            uppercase: false,
        }
    }
}

impl TextStyle {
    /// The look of generated captions: bold, outlined, wrapped short, the spoken word lit.
    pub fn captions() -> TextStyle {
        TextStyle {
            size_percent: 5.5,
            shadow: false,
            outline: true,
            highlight: true,
            wrap_percent: 80.0,
            ..Default::default()
        }
    }
}

/// One spoken word of a text clip, timed in seconds from the clip's start.
#[derive(Clone, Debug, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct TimedWord {
    pub text: String,
    pub start_sec: f64,
    pub end_sec: f64,
}

impl TimedWord {
    pub fn new(text: impl Into<String>, start_sec: f64, end_sec: f64) -> Self {
        TimedWord { text: text.into(), start_sec, end_sec }
    }
}

/// A side of a cropped picture.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum CropSide {
    Left,
    Top,
    Right,
    Bottom,
}

impl Clip {
    /// A clip of `body` with every shared field at its default.
    pub fn new(body: impl Into<ClipBody>) -> Clip {
        Clip {
            body: body.into(),
            id: new_id(),
            link_group_id: None,
            enabled: true,
            source_rate: None,
            start_sec: 0.0,
            dur_sec: 0.0,
            speed: 1.0,
            volume: 1.0,
            opacity: 1.0,
            fade_in_sec: 0.0,
            fade_out_sec: 0.0,
            effects: Vec::new(),
            transition_in: None,
            transition_out: None,
            markers: Vec::new(),
            keyframes: KeyframeMap::new(),
            metadata: Metadata::new(),
        }
    }

    /// Sets the id (for building clips in one expression).
    pub fn with_id(mut self, id: impl Into<String>) -> Clip {
        self.id = id.into();
        self
    }

    /// Sets where the clip sits on the timeline.
    pub fn at(mut self, start_sec: f64, dur_sec: f64) -> Clip {
        self.start_sec = start_sec;
        self.dur_sec = dur_sec;
        self
    }

    pub fn kind(&self) -> ClipKind {
        match self.body {
            ClipBody::Video(_) => ClipKind::Video,
            ClipBody::Audio(_) => ClipKind::Audio,
            ClipBody::Text(_) => ClipKind::Text,
            ClipBody::Image(_) => ClipKind::Image,
        }
    }

    pub fn end_sec(&self) -> f64 {
        self.start_sec + self.dur_sec
    }

    /// The library media behind this clip, or none for clips that are not made from a file (text).
    pub fn media_id(&self) -> Option<&str> {
        match &self.body {
            ClipBody::Video(v) => Some(&v.source_id),
            ClipBody::Audio(a) => Some(&a.source_id),
            ClipBody::Image(i) => Some(&i.source_id),
            ClipBody::Text(_) => None,
        }
    }

    /// True for clips that play a range of a media file (video, audio). Clips without one (text, a still image) can
    /// be made any length.
    pub fn has_source_range(&self) -> bool {
        matches!(self.body, ClipBody::Video(_) | ClipBody::Audio(_))
    }

    /// Where the clip starts in its source. A clip without a source (text, a still) "plays" itself: 0.
    pub fn source_in(&self) -> f64 {
        match &self.body {
            ClipBody::Video(v) => v.src_in_sec,
            ClipBody::Audio(a) => a.src_in_sec,
            _ => 0.0,
        }
    }

    /// Where the clip ends in its source; for a clip without one, its own length.
    pub fn source_out(&self) -> f64 {
        match &self.body {
            ClipBody::Video(v) => v.src_out_sec,
            ClipBody::Audio(a) => a.src_out_sec,
            _ => self.dur_sec,
        }
    }

    /// Sets the played range of the source; ignored by clips that have none.
    pub fn set_source_range(&mut self, src_in_sec: f64, src_out_sec: f64) {
        match &mut self.body {
            ClipBody::Video(v) => {
                v.src_in_sec = src_in_sec;
                v.src_out_sec = src_out_sec;
            }
            ClipBody::Audio(a) => {
                a.src_in_sec = src_in_sec;
                a.src_out_sec = src_out_sec;
            }
            _ => {}
        }
    }

    /// True for a clip whose picture can be cropped.
    pub fn is_croppable(&self) -> bool {
        matches!(self.body, ClipBody::Video(_) | ClipBody::Image(_))
    }

    /// One crop inset, for clips that can be cropped.
    pub fn crop(&self, side: CropSide) -> Option<f64> {
        let (l, t, r, b) = match &self.body {
            ClipBody::Video(v) => (v.crop_l, v.crop_t, v.crop_r, v.crop_b),
            ClipBody::Image(i) => (i.crop_l, i.crop_t, i.crop_r, i.crop_b),
            _ => return None,
        };
        Some(match side {
            CropSide::Left => l,
            CropSide::Top => t,
            CropSide::Right => r,
            CropSide::Bottom => b,
        })
    }

    pub fn crop_mut(&mut self, side: CropSide) -> Option<&mut f64> {
        let (l, t, r, b) = match &mut self.body {
            ClipBody::Video(v) => (&mut v.crop_l, &mut v.crop_t, &mut v.crop_r, &mut v.crop_b),
            ClipBody::Image(i) => (&mut i.crop_l, &mut i.crop_t, &mut i.crop_r, &mut i.crop_b),
            _ => return None,
        };
        Some(match side {
            CropSide::Left => l,
            CropSide::Top => t,
            CropSide::Right => r,
            CropSide::Bottom => b,
        })
    }

    /// True when any crop inset is applied.
    pub fn has_crop(&self) -> bool {
        [CropSide::Left, CropSide::Top, CropSide::Right, CropSide::Bottom]
            .into_iter()
            .any(|s| self.crop(s).is_some_and(|v| v > 1e-6))
    }

    /// Where a drawn layer (text, a still) sits on the frame; none for decoded clips.
    pub fn layout(&self) -> Option<&LayerTransform> {
        match &self.body {
            ClipBody::Text(t) => Some(&t.layout),
            ClipBody::Image(i) => Some(&i.layout),
            _ => None,
        }
    }

    pub fn layout_mut(&mut self) -> Option<&mut LayerTransform> {
        match &mut self.body {
            ClipBody::Text(t) => Some(&mut t.layout),
            ClipBody::Image(i) => Some(&mut i.layout),
            _ => None,
        }
    }

    pub fn as_video(&self) -> Option<&VideoBody> {
        match &self.body {
            ClipBody::Video(v) => Some(v),
            _ => None,
        }
    }

    pub fn as_video_mut(&mut self) -> Option<&mut VideoBody> {
        match &mut self.body {
            ClipBody::Video(v) => Some(v),
            _ => None,
        }
    }

    pub fn as_audio(&self) -> Option<&AudioBody> {
        match &self.body {
            ClipBody::Audio(a) => Some(a),
            _ => None,
        }
    }

    pub fn as_text(&self) -> Option<&TextBody> {
        match &self.body {
            ClipBody::Text(t) => Some(t),
            _ => None,
        }
    }

    pub fn as_text_mut(&mut self) -> Option<&mut TextBody> {
        match &mut self.body {
            ClipBody::Text(t) => Some(t),
            _ => None,
        }
    }

    pub fn as_image(&self) -> Option<&ImageBody> {
        match &self.body {
            ClipBody::Image(i) => Some(i),
            _ => None,
        }
    }

    /// A copy with a new id, its effects and markers with new ids too. Everything else is copied as it is.
    /// (`clone()` keeps the id, for undo snapshots.)
    pub fn duplicate(&self) -> Clip {
        let mut copy = Clip { id: new_id(), ..self.clone() };
        for e in &mut copy.effects {
            e.id = new_id();
        }
        for m in &mut copy.markers {
            m.id = new_id();
        }
        copy
    }
}

impl Selectable for Clip {
    fn key(&self) -> SelectionKey {
        SelectionKey::new(SelectionGroup::Clip, &self.id)
    }

    /// What the inspector calls this clip ("Video clip").
    fn display_name(&self) -> &'static str {
        match self.body {
            ClipBody::Video(_) => "Video clip",
            ClipBody::Audio(_) => "Audio clip",
            ClipBody::Text(_) => "Text clip",
            ClipBody::Image(_) => "Image clip",
        }
    }

    /// What can be edited about this clip; each kind of clip declares its own.
    fn facets(&self) -> Facet {
        match &self.body {
            ClipBody::Video(v) => {
                let sound = if v.plays_audio { Facet::SOUND } else { Facet::empty() };
                Facet::PICTURE | Facet::CROP | sound | Facet::FADES | Facet::RETIME | Facet::EFFECTS | Facet::SOURCE
            }
            ClipBody::Audio(_) => Facet::SOUND | Facet::FADES | Facet::RETIME | Facet::EFFECTS | Facet::SOURCE,
            ClipBody::Text(_) => Facet::TEXT | Facet::PICTURE | Facet::LAYOUT | Facet::FADES | Facet::EFFECTS,
            ClipBody::Image(_) => {
                Facet::PICTURE | Facet::CROP | Facet::LAYOUT | Facet::FADES | Facet::EFFECTS | Facet::SOURCE
            }
        }
    }
}

macro_rules! body_from {
    ($($ty:ident => $variant:ident),*) => {$(
        impl From<$ty> for ClipBody {
            fn from(b: $ty) -> ClipBody {
                ClipBody::$variant(b)
            }
        }
    )*};
}

body_from!(VideoBody => Video, AudioBody => Audio, TextBody => Text, ImageBody => Image);
