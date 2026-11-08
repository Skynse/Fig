//! The timeline model: timelines, tracks, clips, effects, keyframes, markers, and the time maths over them.

pub mod automation;
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
pub mod split;
pub mod text_words;

pub use automation::keys as automation_keys;
pub use clip::*;
pub use effect::*;
pub use keyframes::*;
pub use marker::*;
pub use model::*;
pub use param::*;
pub use rate::*;
pub use selection::*;
