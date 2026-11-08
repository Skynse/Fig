use fig_timeline::{ParamDef, ParamKind, ParamMap};

/// The parameters an effect or a transition runs with, read through the schema it declares: a missing value is the
/// declared default, a number is held to the declared range, a choice to the declared choices.
#[derive(Clone, Copy)]
pub struct ParamSet<'a> {
    schema: &'a [ParamDef],
    values: &'a ParamMap,
}

impl<'a> ParamSet<'a> {
    pub fn new(schema: &'a [ParamDef], values: &'a ParamMap) -> Self {
        ParamSet { schema, values }
    }

    /// Whether the schema declares `key`.
    pub fn declares(&self, key: &str) -> bool {
        self.find(key).is_some()
    }

    pub fn number(&self, key: &str) -> f64 {
        let def = self.def(key);
        let value = match self.values.get(key) {
            Some(v) if v.is_numeric() => v.as_number(),
            _ => def.default,
        };
        let value = if value.is_finite() { value } else { def.default };
        if def.max > def.min { value.clamp(def.min, def.max) } else { value }
    }

    pub fn int(&self, key: &str) -> i32 {
        self.number(key).round_ties_even() as i32
    }

    pub fn bool(&self, key: &str) -> bool {
        match self.values.get(key) {
            Some(v) if v.kind() == ParamKind::Bool => v.as_bool(),
            _ => self.def(key).default != 0.0,
        }
    }

    /// ARGB.
    pub fn color(&self, key: &str) -> u32 {
        match self.values.get(key) {
            Some(v) if v.kind() == ParamKind::Color => v.as_color(),
            _ => self.def(key).default as u32,
        }
    }

    /// The index of the picked choice.
    pub fn choice(&self, key: &str) -> i32 {
        let def = self.def(key);
        let index = match self.values.get(key) {
            Some(v) if v.kind() == ParamKind::List => v.as_choice(),
            _ => def.default.round_ties_even() as i32,
        };
        if def.choices.is_empty() { index } else { index.clamp(0, def.choices.len() as i32 - 1) }
    }

    fn def(&self, key: &str) -> &ParamDef {
        self.find(key).unwrap_or_else(|| panic!("parameter '{key}' is read but not declared"))
    }

    fn find(&self, key: &str) -> Option<&ParamDef> {
        self.schema.iter().find(|d| d.key == key)
    }
}

/// Declares a parameter: `param!("amount", "Amount", 0.15, -1.0, 1.0)` for a number, or with a kind first for the
/// others (`param!(Bool "radial", "Radial", 0.0)`).
#[macro_export]
#[doc(hidden)]
macro_rules! param {
    ($key:literal, $label:literal, $default:expr, $min:expr, $max:expr) => {
        $crate::__timeline::ParamDef {
            key: $key.into(),
            label: $label.into(),
            kind: $crate::__timeline::ParamKind::Double,
            default: $default as f64,
            min: $min as f64,
            max: $max as f64,
            ..Default::default()
        }
    };
    (Int $key:literal, $label:literal, $default:expr, $min:expr, $max:expr) => {
        $crate::__timeline::ParamDef {
            kind: $crate::__timeline::ParamKind::Int,
            ..$crate::param!($key, $label, $default, $min, $max)
        }
    };
    (Bool $key:literal, $label:literal, $default:expr) => {
        $crate::__timeline::ParamDef {
            kind: $crate::__timeline::ParamKind::Bool,
            ..$crate::param!($key, $label, $default, 0, 0)
        }
    };
    (Color $key:literal, $label:literal, $default:expr) => {
        $crate::__timeline::ParamDef {
            kind: $crate::__timeline::ParamKind::Color,
            ..$crate::param!($key, $label, $default as u32, 0, 0)
        }
    };
    (List $key:literal, $label:literal, $default:expr, [$($choice:literal),*]) => {
        $crate::__timeline::ParamDef {
            kind: $crate::__timeline::ParamKind::List,
            choices: vec![$($choice.into()),*],
            ..$crate::param!($key, $label, $default, 0, 0)
        }
    };
}
