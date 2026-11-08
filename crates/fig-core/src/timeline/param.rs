use std::fmt;

use serde::de::{self, MapAccess, Visitor};
use serde::ser::SerializeMap;
use serde::{Deserialize, Deserializer, Serialize, Serializer};

/// Kind of a parameter value, driving both the schema and the editor control.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Default, Serialize, Deserialize)]
pub enum ParamKind {
    #[default]
    Double,
    Int,
    Bool,
    Color,
    List,
}

/// A typed parameter value (double / int / bool / color / list choice), validated against the owning schema.
/// Keyframe tracks wrap the same value type.
///
/// Saved as `{"kind": "...", "value": ...}`. Legacy bare numbers (and booleans) read as the matching kind, so old
/// project files keep loading.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum ParamValue {
    Double(f64),
    Int(i32),
    Bool(bool),
    /// ARGB.
    Color(u32),
    /// An index into the parameter's choices.
    Choice(i32),
}

impl Default for ParamValue {
    fn default() -> Self {
        ParamValue::Double(0.0)
    }
}

impl ParamValue {
    pub fn kind(self) -> ParamKind {
        match self {
            ParamValue::Double(_) => ParamKind::Double,
            ParamValue::Int(_) => ParamKind::Int,
            ParamValue::Bool(_) => ParamKind::Bool,
            ParamValue::Color(_) => ParamKind::Color,
            ParamValue::Choice(_) => ParamKind::List,
        }
    }

    /// The number of a Double or Int value. Other kinds have none: 0 (the C# build threw here).
    pub fn as_number(self) -> f64 {
        match self {
            ParamValue::Double(v) => v,
            ParamValue::Int(v) => v as f64,
            _ => {
                debug_assert!(false, "ParamValue is {:?}, not numeric", self.kind());
                0.0
            }
        }
    }

    pub fn as_bool(self) -> bool {
        match self {
            ParamValue::Bool(v) => v,
            _ => {
                debug_assert!(false, "ParamValue is {:?}, not Bool", self.kind());
                false
            }
        }
    }

    pub fn as_color(self) -> u32 {
        match self {
            ParamValue::Color(v) => v,
            _ => {
                debug_assert!(false, "ParamValue is {:?}, not Color", self.kind());
                0
            }
        }
    }

    pub fn as_choice(self) -> i32 {
        match self {
            ParamValue::Choice(v) => v,
            _ => {
                debug_assert!(false, "ParamValue is {:?}, not List", self.kind());
                0
            }
        }
    }

    pub fn is_numeric(self) -> bool {
        matches!(self, ParamValue::Double(_) | ParamValue::Int(_))
    }
}

impl fmt::Display for ParamValue {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match *self {
            ParamValue::Bool(v) => write!(f, "{}", if v { "True" } else { "False" }),
            ParamValue::Color(v) => write!(f, "{v:08X}"),
            ParamValue::Choice(v) | ParamValue::Int(v) => write!(f, "{v}"),
            ParamValue::Double(v) => {
                // "0.###"
                let s = format!("{:.3}", v);
                let s = s.trim_end_matches('0').trim_end_matches('.');
                write!(f, "{}", if s == "-0" { "0" } else { s })
            }
        }
    }
}

impl Serialize for ParamValue {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut map = serializer.serialize_map(Some(2))?;
        match *self {
            ParamValue::Double(v) => {
                map.serialize_entry("kind", "double")?;
                map.serialize_entry("value", &v)?;
            }
            ParamValue::Int(v) => {
                map.serialize_entry("kind", "int")?;
                map.serialize_entry("value", &v)?;
            }
            ParamValue::Bool(v) => {
                map.serialize_entry("kind", "bool")?;
                map.serialize_entry("value", &v)?;
            }
            ParamValue::Color(v) => {
                map.serialize_entry("kind", "color")?;
                map.serialize_entry("value", &v)?;
            }
            ParamValue::Choice(v) => {
                map.serialize_entry("kind", "list")?;
                map.serialize_entry("value", &v)?;
            }
        }
        map.end()
    }
}

impl<'de> Deserialize<'de> for ParamValue {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        struct V;

        impl<'de> Visitor<'de> for V {
            type Value = ParamValue;

            fn expecting(&self, f: &mut fmt::Formatter) -> fmt::Result {
                f.write_str("a parameter value")
            }

            fn visit_f64<E: de::Error>(self, v: f64) -> Result<ParamValue, E> {
                Ok(ParamValue::Double(v))
            }
            fn visit_i64<E: de::Error>(self, v: i64) -> Result<ParamValue, E> {
                Ok(ParamValue::Double(v as f64))
            }
            fn visit_u64<E: de::Error>(self, v: u64) -> Result<ParamValue, E> {
                Ok(ParamValue::Double(v as f64))
            }
            fn visit_bool<E: de::Error>(self, v: bool) -> Result<ParamValue, E> {
                Ok(ParamValue::Bool(v))
            }
            fn visit_unit<E: de::Error>(self) -> Result<ParamValue, E> {
                Ok(ParamValue::default())
            }
            fn visit_none<E: de::Error>(self) -> Result<ParamValue, E> {
                Ok(ParamValue::default())
            }
            fn visit_str<E: de::Error>(self, _: &str) -> Result<ParamValue, E> {
                Ok(ParamValue::default())
            }

            fn visit_map<A: MapAccess<'de>>(self, mut map: A) -> Result<ParamValue, A::Error> {
                // "kind" is written first, but read it wherever it is
                let mut kind: Option<String> = None;
                let mut value: Option<serde_json::Value> = None;
                while let Some(key) = map.next_key::<String>()? {
                    match key.as_str() {
                        "kind" => kind = map.next_value::<Option<String>>()?,
                        "value" => value = Some(map.next_value()?),
                        _ => {
                            map.next_value::<de::IgnoredAny>()?;
                        }
                    }
                }
                let value = value.unwrap_or(serde_json::Value::Null);
                let number = value.as_f64().unwrap_or(0.0);
                Ok(match kind.as_deref() {
                    Some("int") => ParamValue::Int(number as i32),
                    Some("bool") => ParamValue::Bool(value.as_bool().unwrap_or(false)),
                    Some("color") => ParamValue::Color(value.as_u64().unwrap_or(0) as u32),
                    Some("list") => ParamValue::Choice(value.as_i64().unwrap_or(0) as i32),
                    _ => ParamValue::Double(number),
                })
            }
        }

        deserializer.deserialize_any(V)
    }
}

/// The schema of one parameter: its key, label, kind, range and default.
#[derive(Clone, Debug, PartialEq)]
pub struct ParamDef {
    pub key: String,
    pub label: String,
    pub kind: ParamKind,
    pub default: f64,
    pub min: f64,
    pub max: f64,
    pub choices: Vec<String>,
    /// How the value reads beside its slider, as the C# format string it came from ("{0:P0}" for 50%,
    /// "{0:0.0} s" for seconds...). See [`ParamDef::format_value`].
    pub format: String,
}

impl Default for ParamDef {
    fn default() -> Self {
        ParamDef {
            key: String::new(),
            label: String::new(),
            kind: ParamKind::Double,
            default: 0.0,
            min: 0.0,
            max: 0.0,
            choices: Vec::new(),
            format: "{0:0.##}".into(),
        }
    }
}

impl ParamDef {
    /// The schema default as a typed value.
    pub fn default_value(&self) -> ParamValue {
        match self.kind {
            ParamKind::Int => ParamValue::Int(self.default.round() as i32),
            ParamKind::Bool => ParamValue::Bool(self.default != 0.0),
            ParamKind::Color => ParamValue::Color(self.default as u32),
            ParamKind::List => ParamValue::Choice(self.default.round() as i32),
            ParamKind::Double => ParamValue::Double(self.default),
        }
    }

    /// A number as it reads beside its slider. Understands the formats the catalogs use: `{0:P0}` (percent),
    /// `{0:0.##}`-style digit patterns, and literal text around them.
    pub fn format_value(&self, value: f64) -> String {
        format_number(&self.format, value)
    }
}

/// Formats `value` with a .NET composite format string of one `{0:...}` hole.
pub fn format_number(format: &str, value: f64) -> String {
    let (Some(open), Some(close)) = (format.find("{0"), format.find('}')) else {
        return format.to_string();
    };
    let spec = format[open + 2..close].strip_prefix(':').unwrap_or("");
    let body = if let Some(digits) = spec.strip_prefix('P') {
        let places: usize = digits.parse().unwrap_or(2);
        format!("{:.*}%", places, value * 100.0)
    } else if spec.is_empty() {
        trim_pattern(value, 0, 15)
    } else {
        let frac = spec.split_once('.').map(|(_, f)| f).unwrap_or("");
        let required = frac.chars().filter(|&c| c == '0').count();
        let optional = frac.chars().filter(|&c| c == '#').count();
        trim_pattern(value, required, required + optional)
    };
    format!("{}{}{}", &format[..open], body, &format[close + 1..])
}

fn trim_pattern(value: f64, required: usize, max: usize) -> String {
    let mut s = format!("{:.*}", max, value);
    if max > required && s.contains('.') {
        let keep = s.find('.').unwrap() + 1 + required;
        while s.len() > keep && s.ends_with('0') {
            s.pop();
        }
        if s.ends_with('.') {
            s.pop();
        }
    }
    if s.starts_with('-') && s[1..].chars().all(|c| c == '0' || c == '.') {
        s.remove(0);
    }
    s
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_tagged_and_legacy_values() {
        let v: Vec<ParamValue> =
            serde_json::from_str(r#"[{"kind":"int","value":4},{"kind":"color","value":4279312947},{"value":true,"kind":"bool"},2.5,true,null]"#)
                .unwrap();
        assert_eq!(
            v,
            [
                ParamValue::Int(4),
                ParamValue::Color(0xFF112233),
                ParamValue::Bool(true),
                ParamValue::Double(2.5),
                ParamValue::Bool(true),
                ParamValue::Double(0.0)
            ]
        );
        assert_eq!(serde_json::to_string(&ParamValue::Choice(2)).unwrap(), r#"{"kind":"list","value":2}"#);
    }

    #[test]
    fn formats_like_dotnet() {
        assert_eq!(format_number("{0:P0}", 0.5), "50%");
        assert_eq!(format_number("{0:0.0} s", 1.26), "1.3 s");
        assert_eq!(format_number("{0:0.#}%", 8.0), "8%");
        assert_eq!(format_number("{0:0.00}×", 1.0), "1.00×");
        assert_eq!(format_number("{0:0}°", -0.2), "0°");
    }
}
