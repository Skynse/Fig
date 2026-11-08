//! Effect presets: a named effect with keyframes along its bar, such as "Zoom in" (a Transform with two keys). Pure
//! data, so new ones (built in or saved by the user) need no code.

use std::path::{Path, PathBuf};

use indexmap::IndexMap;
use serde::{Deserialize, Serialize};

use crate::effects::{self, ids};
use fig_timeline::serde_util::new_id_n;
use fig_timeline::{EffectInstance, KeyframeEasing, KeyframePoint, ParamKind, ParamValue};

/// One keyframe of a preset: where it falls along the effect's bar (0..1) and its values.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct PresetKeyframe {
    pub at: f64,
    pub easing: KeyframeEasing,
    pub params: IndexMap<String, f64>,
}

impl Default for PresetKeyframe {
    fn default() -> Self {
        PresetKeyframe { at: 0.0, easing: KeyframeEasing::EaseInOut, params: IndexMap::new() }
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct EffectPreset {
    pub id: String,
    pub name: String,
    pub description: String,
    pub icon: String,
    /// The effect the preset makes.
    pub type_id: String,
    /// Optional browser category; without one a preset takes its effect's.
    pub category: Option<String>,
    pub default_duration_sec: f64,
    pub built_in: bool,
    pub keyframes: Vec<PresetKeyframe>,
}

impl Default for EffectPreset {
    fn default() -> Self {
        EffectPreset {
            id: new_id_n(),
            name: String::new(),
            description: String::new(),
            icon: "focus".into(),
            type_id: ids::TRANSFORM.into(),
            category: None,
            default_duration_sec: 3.0,
            built_in: false,
            keyframes: Vec::new(),
        }
    }
}

fn camera(at: f64, zoom: f64, cx: f64, cy: f64, rotation: f64) -> PresetKeyframe {
    PresetKeyframe {
        at,
        easing: KeyframeEasing::EaseInOut,
        params: IndexMap::from([
            ("zoom".into(), zoom),
            ("centerX".into(), cx),
            ("centerY".into(), cy),
            ("rotation".into(), rotation),
        ]),
    }
}

fn motion(
    id: &str,
    name: &str,
    description: &str,
    icon: &str,
    duration: f64,
    keys: Vec<PresetKeyframe>,
) -> EffectPreset {
    EffectPreset {
        id: id.into(),
        name: name.into(),
        description: description.into(),
        icon: icon.into(),
        default_duration_sec: duration,
        built_in: true,
        keyframes: keys,
        ..Default::default()
    }
}

fn impact(id: &str, name: &str, description: &str, type_id: &str, duration: f64, peak: f64) -> EffectPreset {
    let key = |at: f64, amount: f64| PresetKeyframe {
        at,
        params: IndexMap::from([("amount".into(), amount)]),
        ..Default::default()
    };
    EffectPreset {
        id: id.into(),
        name: name.into(),
        description: description.into(),
        type_id: type_id.into(),
        category: (type_id == ids::BRIGHTNESS).then(|| "Light".into()),
        icon: "zap".into(),
        default_duration_sec: duration,
        built_in: true,
        keyframes: vec![key(0.0, 0.0), key(0.12, peak), key(1.0, 0.0)],
    }
}

/// The presets that ship with Fig: reusable motion and short impact envelopes.
pub fn built_in() -> Vec<EffectPreset> {
    let c = |at, zoom| camera(at, zoom, 0.5, 0.5, 0.0);
    vec![
        motion(
            "zoom-in",
            "Zoom in",
            "Ease in on the middle of the picture.",
            "zoom-in",
            3.0,
            vec![c(0.0, 1.0), c(1.0, 2.0)],
        ),
        motion(
            "zoom-out",
            "Zoom out",
            "Start close and ease back out to the full picture.",
            "zoom-out",
            3.0,
            vec![c(0.0, 2.0), c(1.0, 1.0)],
        ),
        motion(
            "zoom-in-out",
            "Zoom in and out",
            "Zoom in, hold, and zoom back out smoothly.",
            "focus",
            4.0,
            vec![c(0.0, 1.0), c(0.3, 2.0), c(0.7, 2.0), c(1.0, 1.0)],
        ),
        motion(
            "pan",
            "Pan",
            "Slide across the picture while slightly zoomed in.",
            "move-horizontal",
            4.0,
            vec![camera(0.0, 1.5, 0.35, 0.5, 0.0), camera(1.0, 1.5, 0.65, 0.5, 0.0)],
        ),
        motion(
            "ken-burns",
            "Slow push",
            "A gentle, slow drift toward one side.",
            "move",
            6.0,
            vec![camera(0.0, 1.0, 0.5, 0.5, 0.0), camera(1.0, 1.25, 0.58, 0.46, 0.0)],
        ),
        motion(
            "punch-zoom",
            "Punch Zoom",
            "A quick push and smooth recovery for a musical hit.",
            "zoom-in",
            0.45,
            vec![c(0.0, 1.0), c(0.15, 1.3), c(1.0, 1.0)],
        ),
        // zoom stays above cos(r) + (16/9)·sin(r) along the whole move so rotated edges never show
        motion(
            "tilt-bounce",
            "Tilt Bounce",
            "Rock the camera through a short zoomed impact.",
            "rotate-cw",
            0.6,
            vec![c(0.0, 1.0), camera(0.2, 1.24, 0.5, 0.5, 7.0), camera(0.5, 1.12, 0.5, 0.5, -3.0), c(1.0, 1.0)],
        ),
        motion(
            "whip-left",
            "Whip Left",
            "A fast lateral camera move that settles back to center.",
            "move-horizontal",
            0.5,
            vec![c(0.0, 1.15), camera(0.18, 1.4, 0.7, 0.5, 0.0), c(1.0, 1.15)],
        ),
        motion(
            "whip-right",
            "Whip Right",
            "A fast lateral camera move in the opposite direction.",
            "move-horizontal",
            0.5,
            vec![c(0.0, 1.15), camera(0.18, 1.4, 0.3, 0.5, 0.0), c(1.0, 1.15)],
        ),
        impact(
            "white-flash-hit",
            "White Flash Hit",
            "A brief exposure flash that settles to the original picture.",
            ids::BRIGHTNESS,
            0.3,
            0.9,
        ),
        impact("glow-hit", "Glow Hit", "Bloom the highlights briefly on an impact.", "glow", 0.5, 1.8),
        impact(
            "glitch-burst",
            "Glitch Burst",
            "A short burst of digital slice distortion.",
            "digital-glitch",
            0.4,
            0.9,
        ),
        impact("shake-hit", "Shake Hit", "A camera shake that quickly decays after a hit.", "camera-shake", 0.5, 0.065),
    ]
}

/// The effect a preset describes, its keyframes starting at `at_local_sec` on an owner `owner_dur_sec` long. The
/// effect begins there and is shortened to the room left, its bar and animation sharing one range. None for a preset
/// of an effect that doesn't exist.
pub fn instantiate(preset: &EffectPreset, owner_dur_sec: f64, at_local_sec: f64) -> Option<EffectInstance> {
    let entry = effects::find(&preset.type_id)?;
    let mut fx = entry.create_instance();
    let length = owner_dur_sec.max(0.0);
    let start = at_local_sec.clamp(0.0, (length - length.min(0.001)).max(0.0));
    let duration = preset.default_duration_sec.max(0.1).min(length - start);
    fx.start_sec = Some(start);
    fx.dur_sec = Some(duration);
    let mut keys = preset.keyframes.clone();
    keys.sort_by(|a, b| a.at.total_cmp(&b.at));
    let typed = |name: &str, n: f64| {
        let def = entry.param(name).filter(|d| matches!(d.kind, ParamKind::Double | ParamKind::Int))?;
        Some(if def.kind == ParamKind::Int {
            ParamValue::Int(n.round_ties_even() as i32)
        } else {
            ParamValue::Double(n)
        })
    };
    for key in &keys {
        let time = start + key.at.clamp(0.0, 1.0) * duration;
        for (name, &n) in &key.params {
            if let Some(v) = typed(name, n) {
                fx.keyframes.entry(name.clone()).or_default().push(KeyframePoint::eased(time, v, key.easing));
            }
        }
    }
    // the plain parameter values are the starting look, used wherever nothing is keyframed
    if let Some(first) = keys.first() {
        for (name, &n) in &first.params {
            if let Some(v) = typed(name, n) {
                fx.params.insert(name.clone(), v);
            }
        }
    }
    Some(fx)
}

/// An effect's keyframes as a preset (times become 0..1 from its first keyframe to its last). Numbers only.
pub fn from_effect(effect: &EffectInstance, name: &str) -> EffectPreset {
    let mut times: Vec<f64> = effect.keyframes.values().flatten().map(|k| (k.time_sec * 1e6).round() / 1e6).collect();
    times.sort_by(f64::total_cmp);
    times.dedup();
    if times.is_empty() {
        times.push(0.0);
    }
    let start = times[0];
    let duration = (times[times.len() - 1] - start).max(0.1);
    let entry = effects::find(&effect.type_id);
    let keyframes = times
        .iter()
        .map(|&t| {
            let resolved = effects::resolve_params(effect, t);
            let easing = effect
                .keyframes
                .values()
                .flatten()
                .find(|k| (k.time_sec - t).abs() < 1e-6)
                .map_or(KeyframeEasing::Linear, |k| k.easing);
            PresetKeyframe {
                at: if duration <= 1e-9 { 0.0 } else { ((t - start) / duration).clamp(0.0, 1.0) },
                easing,
                params: resolved
                    .iter()
                    .filter(|(_, v)| v.is_numeric())
                    .map(|(k, v)| (k.clone(), v.as_number()))
                    .collect(),
            }
        })
        .collect();
    EffectPreset {
        name: name.into(),
        type_id: effect.type_id.clone(),
        category: entry.as_ref().map(|e| e.category.clone()),
        icon: entry.map_or("wand-sparkles".into(), |e| e.icon.clone()),
        default_duration_sec: duration,
        keyframes,
        ..Default::default()
    }
}

/// User presets saved as small JSON files, one per preset.
pub struct EffectPresetStore {
    root: PathBuf,
}

impl EffectPresetStore {
    pub fn new(root: impl Into<PathBuf>) -> Self {
        EffectPresetStore { root: root.into() }
    }

    pub fn list(&self) -> Vec<EffectPreset> {
        let Ok(entries) = std::fs::read_dir(&self.root) else { return Vec::new() };
        let mut presets: Vec<EffectPreset> = entries
            .flatten()
            .map(|e| e.path())
            .filter(|p| p.to_string_lossy().ends_with(".figpreset.json"))
            .filter_map(|p| serde_json::from_str(&std::fs::read_to_string(p).ok()?).ok())
            .collect();
        presets.sort_by_key(|p| p.name.to_lowercase());
        presets
    }

    pub fn save(&self, preset: &EffectPreset) -> std::io::Result<()> {
        std::fs::create_dir_all(&self.root)?;
        let mut p = preset.clone();
        p.built_in = false;
        std::fs::write(self.path_for(&p.id), serde_json::to_string_pretty(&p).map_err(std::io::Error::other)?)
    }

    pub fn delete(&self, id: &str) -> bool {
        std::fs::remove_file(self.path_for(id)).is_ok()
    }

    fn path_for(&self, id: &str) -> PathBuf {
        let safe: String = id.chars().filter(|c| c.is_alphanumeric()).collect();
        self.root.join(format!("{safe}.figpreset.json"))
    }

    pub fn root(&self) -> &Path {
        &self.root
    }
}
