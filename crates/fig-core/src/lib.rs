//! Fig's core: the timeline model, editing, projects and media, with no UI.
//!
//! A port of the C# `Fig.Core`. Project files are read and written in the same format, so a project opens in
//! either build.

pub mod media;
pub mod project;
mod serde_util;
pub mod timeline;

pub use serde_util::{Metadata, new_id};
