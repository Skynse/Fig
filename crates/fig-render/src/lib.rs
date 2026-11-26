//! Turning a timeline moment into a picture: the scene, the compositor, and drawing text and stills.

mod colr;
mod compose;
pub mod layers;
mod scene;

pub use compose::blend;
pub use scene::*;
