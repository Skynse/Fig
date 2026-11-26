//! Editing a timeline: the editor that makes every change, its undo history, the selection, snapping, the main
//! track magnet, keyframes, effects, transitions, markers and the clipboard.

mod captions;
mod clipboard;
mod editor;
pub mod effects;
pub mod history;
mod keyframes;
pub mod magnet;
pub mod markers;
mod quick_edits;
mod selected;
mod selection;
mod snapping;
pub mod transitions;

pub use captions::{CAPTION_LAYOUT, captions_on};
pub use editor::*;
pub use keyframes::{KeyframeHost, KeyframeHostRef, KeyframeRef};
pub use markers::{BeatPreview, MarkerEdit, MarkerLocation, MarkerOwner, PlannedRange};
pub use quick_edits::TempoMatch;
pub use selected::Selected;
pub use selection::SelectionState;
pub use snapping::{SnapExclude, SnapKind, SnapResult, SnapSettings};
pub use transitions::CutTransition;
