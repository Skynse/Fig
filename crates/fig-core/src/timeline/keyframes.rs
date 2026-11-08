use serde::{Deserialize, Serialize};

use super::ParamValue;

/// How a value moves from a keyframe to the next one.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Default, Serialize, Deserialize)]
pub enum KeyframeEasing {
    /// Constant speed.
    #[default]
    Linear,
    /// Starts slowly.
    EaseIn,
    /// Ends slowly.
    EaseOut,
    /// Starts and ends slowly.
    EaseInOut,
    /// Stays put until the next keyframe, then jumps.
    Hold,
}

impl KeyframeEasing {
    /// Maps linear progress 0..1 through the easing curve (0..1 in, 0..1 out).
    pub fn apply(self, t: f64) -> f64 {
        let t = t.clamp(0.0, 1.0);
        match self {
            KeyframeEasing::EaseIn => t * t,
            KeyframeEasing::EaseOut => 1.0 - (1.0 - t) * (1.0 - t),
            KeyframeEasing::EaseInOut => {
                if t < 0.5 {
                    2.0 * t * t
                } else {
                    1.0 - (-2.0 * t + 2.0).powi(2) / 2.0
                }
            }
            KeyframeEasing::Hold => {
                if t >= 1.0 {
                    1.0
                } else {
                    0.0
                }
            }
            KeyframeEasing::Linear => t,
        }
    }
}

/// One keyframe in its owner's time domain: absolute timeline seconds for track effects, local seconds for clip
/// properties. `easing` shapes the movement from this keyframe to the next.
#[derive(Clone, Copy, Debug, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct KeyframePoint {
    pub time_sec: f64,
    pub value: ParamValue,
    pub easing: KeyframeEasing,
}

impl KeyframePoint {
    pub fn new(time_sec: f64, value: ParamValue) -> Self {
        KeyframePoint { time_sec, value, easing: KeyframeEasing::Linear }
    }

    pub fn eased(time_sec: f64, value: ParamValue, easing: KeyframeEasing) -> Self {
        KeyframePoint { time_sec, value, easing }
    }
}

/// Keyframe tracks keyed by property or parameter name, in the order they were added.
pub type KeyframeMap = indexmap::IndexMap<String, Vec<KeyframePoint>>;
