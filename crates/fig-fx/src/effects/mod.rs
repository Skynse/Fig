//! Effects: the catalog of what there is (built-in kernels and the user's graph effects), and running a stack of
//! them over a frame.

mod basic;
mod creative;

use std::sync::{Arc, LazyLock, RwLock};

use super::{Frame, ParamSet};
use fig_timeline::{EffectInstance, KeyframePoint, ParamDef, ParamKind, ParamMap, ParamValue};

pub use basic::{TransformView, transform_view, transform_view_at};

/// Built-in effect type ids the code refers to by name.
pub mod ids {
    pub const BRIGHTNESS: &str = "brightness";
    pub const GRAYSCALE: &str = "grayscale";
    pub const TINT: &str = "tint";
    pub const CONTRAST: &str = "contrast";
    pub const SATURATION: &str = "saturation";
    pub const HUE: &str = "hue";
    pub const INVERT: &str = "invert";
    pub const SEPIA: &str = "sepia";
    pub const VIGNETTE: &str = "vignette";
    pub const SHARPEN: &str = "sharpen";
    pub const PIXELATE: &str = "pixelate";
    pub const FLIP: &str = "flip";
    pub const POSTERIZE: &str = "posterize";
    pub const CROP_WITH_PADDING: &str = "cropWithPadding";
    pub const TRANSFORM: &str = "transform";
    pub const CHROMATIC_ABERRATION: &str = "chromaticAberration";
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Default)]
pub enum EffectKind {
    #[default]
    Video,
    Audio,
    Both,
}

/// A built-in effect's kernel: the frame changed, or none when there is nothing to do.
pub type Kernel = fn(&Frame, &ParamSet, f64) -> Option<Frame>;

/// A user effect's renderer (a compiled node graph), given the effect's resolved parameters.
pub type CustomKernel = Arc<dyn Fn(&Frame, &ParamMap, f64) -> Option<Frame> + Send + Sync>;

#[derive(Clone)]
pub enum Render {
    Builtin(Kernel),
    Custom(CustomKernel),
}

/// One effect the user can add.
#[derive(Clone)]
pub struct EffectEntry {
    pub type_id: String,
    pub display_name: String,
    pub category: String,
    pub icon: String,
    pub description: String,
    pub kind: EffectKind,
    /// True for effects the user built (graph effects) rather than ones that ship with Fig.
    pub is_custom: bool,
    pub params: Vec<ParamDef>,
    pub render: Render,
}

impl EffectEntry {
    pub(crate) fn builtin(
        type_id: &str,
        display_name: &str,
        category: &str,
        icon: &str,
        description: &str,
        params: Vec<ParamDef>,
        kernel: Kernel,
    ) -> EffectEntry {
        EffectEntry {
            type_id: type_id.into(),
            display_name: display_name.into(),
            category: category.into(),
            icon: icon.into(),
            description: description.into(),
            kind: EffectKind::Video,
            is_custom: false,
            params,
            render: Render::Builtin(kernel),
        }
    }

    pub fn default_params(&self) -> ParamMap {
        self.params.iter().map(|p| (p.key.clone(), p.default_value())).collect()
    }

    /// A new instance of this effect with every parameter at its default.
    pub fn create_instance(&self) -> EffectInstance {
        EffectInstance { type_id: self.type_id.clone(), params: self.default_params(), ..Default::default() }
    }

    pub fn param(&self, key: &str) -> Option<&ParamDef> {
        self.params.iter().find(|p| p.key == key)
    }

    /// Runs the effect; none when it leaves the frame as it is.
    pub fn apply(&self, frame: &Frame, params: &ParamMap, local_t: f64) -> Option<Frame> {
        match &self.render {
            Render::Builtin(kernel) => kernel(frame, &ParamSet::new(&self.params, params), local_t),
            Render::Custom(kernel) => kernel(frame, params, local_t),
        }
    }
}

static BUILTIN: LazyLock<Vec<Arc<EffectEntry>>> = LazyLock::new(|| {
    let mut all: Vec<EffectEntry> = basic::entries().into_iter().chain(creative::entries()).collect();
    all.sort_by(|a, b| a.type_id.cmp(&b.type_id));
    all.into_iter().map(Arc::new).collect()
});

/// User effects, by the library that published them.
static USER: RwLock<Vec<(String, Arc<EffectEntry>)>> = RwLock::new(Vec::new());

/// Built-in effects, then user effects (graph effects).
pub fn all() -> Vec<Arc<EffectEntry>> {
    let mut list = BUILTIN.clone();
    list.extend(USER.read().unwrap().iter().map(|(_, e)| e.clone()));
    list
}

pub fn find(type_id: &str) -> Option<Arc<EffectEntry>> {
    if let Some(e) = BUILTIN.iter().find(|e| e.type_id == type_id) {
        return Some(e.clone());
    }
    USER.read().unwrap().iter().find(|(_, e)| e.type_id == type_id).map(|(_, e)| e.clone())
}

/// Publishes (or replaces) the user effects owned by `owner`.
pub fn set_user_effects(owner: &str, effects: Vec<EffectEntry>) {
    let mut user = USER.write().unwrap();
    user.retain(|(o, _)| o != owner);
    user.extend(effects.into_iter().map(|e| (owner.to_string(), Arc::new(e))));
}

pub fn clear_user_effects(owner: &str) {
    USER.write().unwrap().retain(|(o, _)| o != owner);
}

/// A parameter's value as stored: an integer parameter rounded.
pub fn to_value(type_id: &str, key: &str, number: f64) -> ParamValue {
    match find(type_id).and_then(|e| e.param(key).map(|p| p.kind)) {
        Some(ParamKind::Int) => ParamValue::Int(number.round_ties_even() as i32),
        _ => ParamValue::Double(number),
    }
}

// ---- running a stack --------------------------------------------------------------------------

/// One effect in force at a moment, with its parameters sampled there.
#[derive(Clone)]
pub struct EffectCall {
    pub entry: Arc<EffectEntry>,
    pub params: ParamMap,
    pub local_t: f64,
}

/// The effects of a stack that are in force at `local_t`, in stack order, with their parameters sampled at that
/// time. Disabled effects, effects outside their range and unknown types drop out.
pub fn resolve_stack(effects: &[EffectInstance], local_t: f64) -> Vec<EffectCall> {
    let mut ordered: Vec<&EffectInstance> = effects.iter().collect();
    ordered.sort_by_key(|e| e.order);
    ordered
        .into_iter()
        .filter(|fx| fx.enabled && !fx.type_id.is_empty() && fx.is_active_at(local_t))
        .filter_map(|fx| Some(EffectCall { entry: find(&fx.type_id)?, params: resolve_params(fx, local_t), local_t }))
        .collect()
}

/// Runs resolved effects in order. None when none changed the frame.
pub fn apply_calls(frame: &Frame, calls: &[EffectCall]) -> Option<Frame> {
    let mut current: Option<Frame> = None;
    for call in calls {
        let input = current.as_ref().unwrap_or(frame);
        if let Some(next) = call.entry.apply(input, &call.params, call.local_t) {
            current = Some(next);
        }
    }
    current
}

/// Runs a stack of effects over a frame at a time in their owner's time domain.
pub fn apply_stack(frame: &Frame, effects: &[EffectInstance], local_t: f64) -> Option<Frame> {
    apply_calls(frame, &resolve_stack(effects, local_t))
}

/// Every parameter at a time: constants pass through; keyframed ones are evaluated from their track (eased for
/// numbers, stepped otherwise).
pub fn resolve_params(fx: &EffectInstance, local_t: f64) -> ParamMap {
    let mut resolved = fx.params.clone();
    for (key, track) in &fx.keyframes {
        if !track.is_empty() {
            resolved.insert(key.clone(), evaluate(track, local_t));
        }
    }
    resolved
}

/// A keyframe track's value at a time.
pub fn evaluate(track: &[KeyframePoint], t: f64) -> ParamValue {
    let (Some(first), Some(last)) = (track.first(), track.last()) else {
        return ParamValue::default();
    };
    if t <= first.time_sec {
        return first.value;
    }
    if t >= last.time_sec {
        return last.value;
    }
    for pair in track.windows(2) {
        let (a, b) = (&pair[0], &pair[1]);
        if t >= a.time_sec && t <= b.time_sec {
            let span = b.time_sec - a.time_sec;
            let u = if span <= 1e-9 { 0.0 } else { (t - a.time_sec) / span };
            return interpolate(a.value, b.value, a.easing.apply(u));
        }
    }
    last.value
}

/// Linear for numbers, stepped for bools, colours and choices.
pub fn interpolate(a: ParamValue, b: ParamValue, u: f64) -> ParamValue {
    match (a, b) {
        (ParamValue::Int(x), ParamValue::Int(y)) => {
            ParamValue::Int((x as f64 + (y - x) as f64 * u).round_ties_even() as i32)
        }
        (ParamValue::Double(x), ParamValue::Double(y)) => ParamValue::Double(x + (y - x) * u),
        _ => {
            if u < 0.5 {
                a
            } else {
                b
            }
        }
    }
}

/// The numeric parameters of an effect at a time (what a new "current look" keyframe holds).
pub fn current_numeric_values(fx: &EffectInstance, local_t: f64) -> indexmap::IndexMap<String, f64> {
    let Some(entry) = find(&fx.type_id) else {
        return Default::default();
    };
    let resolved = resolve_params(fx, local_t);
    entry
        .params
        .iter()
        .filter(|d| matches!(d.kind, ParamKind::Double | ParamKind::Int))
        .filter_map(|d| resolved.get(&d.key).map(|v| (d.key.clone(), v.as_number())))
        .collect()
}
