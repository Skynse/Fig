//! Pictures and what is done to them: decoded frames, the pixel kernels, effects and transitions.
//!
//! One CPU implementation, parallel over rows, serves both the preview and export, so they always match.

// per-channel loops read better indexed
#![allow(clippy::needless_range_loop)]

pub mod effects;
pub mod frame;
mod params;
pub mod transitions;

pub use frame::Frame;
pub use params::ParamSet;

#[doc(hidden)]
pub use fig_timeline as __timeline;
