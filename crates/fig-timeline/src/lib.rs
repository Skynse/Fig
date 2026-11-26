//! Fig's timeline model: timelines, tracks, clips, effects, keyframes, markers, library media, and the time maths
//! over them. Pure data and functions: saved and loaded in the C# build's project format.

mod asset;
pub mod automation;
pub mod captions;
mod clip;
pub mod clip_time;
mod effect;
pub mod fade;
mod keyframes;
mod marker;
mod model;
mod param;
pub mod properties;
mod rate;
mod selection;
pub mod serde_util;
pub mod split;
mod text_presets;
pub mod text_words;
pub mod time;

pub use asset::*;
pub use automation::keys as automation_keys;
pub use captions::CaptionCue;
pub use clip::*;
pub use effect::*;
pub use keyframes::*;
pub use marker::*;
pub use model::*;
pub use param::*;
pub use rate::*;
pub use selection::*;
pub use serde_util::{Metadata, new_id};
pub use text_presets::{TextPreset, text_presets};
