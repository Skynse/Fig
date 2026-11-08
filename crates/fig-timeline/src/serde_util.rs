//! Serde helpers for reading project files the C# build wrote.

use chrono::{DateTime, FixedOffset, Local, NaiveDateTime, TimeZone};
use serde::{Deserialize, Deserializer, Serializer};

/// A fresh id, as the C# build made them (`Guid.NewGuid().ToString()`).
pub fn new_id() -> String {
    uuid::Uuid::new_v4().to_string()
}

/// A fresh id without dashes (`Guid.NewGuid().ToString("N")`).
pub fn new_id_n() -> String {
    uuid::Uuid::new_v4().simple().to_string()
}

/// Source-format provenance preserved across imports: any JSON, kept as it was read.
pub type Metadata = indexmap::IndexMap<String, serde_json::Value>;

pub(crate) fn yes() -> bool {
    true
}

pub(crate) fn one() -> f64 {
    1.0
}

/// A clip's effect stack: an array now; a legacy object bag (or null) reads as empty.
pub(crate) fn effect_list<'de, D, T>(d: D) -> Result<Vec<T>, D::Error>
where
    D: Deserializer<'de>,
    T: Deserialize<'de>,
{
    #[derive(Deserialize)]
    #[serde(untagged)]
    enum Shape<T> {
        List(Vec<T>),
        Legacy(serde::de::IgnoredAny),
    }
    Ok(match Option::<Shape<T>>::deserialize(d)? {
        Some(Shape::List(list)) => list,
        _ => Vec::new(),
    })
}

/// Times as .NET writes them: ISO 8601 with an offset, or without one (local time).
pub mod datetime {
    use super::*;

    pub fn now() -> DateTime<FixedOffset> {
        Local::now().fixed_offset()
    }

    pub fn utc_now() -> DateTime<FixedOffset> {
        chrono::Utc::now().fixed_offset()
    }

    pub fn parse(s: &str) -> Option<DateTime<FixedOffset>> {
        if let Ok(t) = DateTime::parse_from_rfc3339(s) {
            return Some(t);
        }
        let naive = NaiveDateTime::parse_from_str(s, "%Y-%m-%dT%H:%M:%S%.f").ok()?;
        Local.from_local_datetime(&naive).earliest().map(|t| t.fixed_offset())
    }

    /// As .NET writes it: up to 7 fractional digits (trailing zeros dropped), `Z` for UTC.
    pub fn format(t: &DateTime<FixedOffset>) -> String {
        let mut out = t.format("%Y-%m-%dT%H:%M:%S").to_string();
        let ticks = t.timestamp_subsec_nanos() / 100;
        if ticks > 0 {
            let frac = format!("{ticks:07}");
            out.push('.');
            out.push_str(frac.trim_end_matches('0'));
        }
        if t.offset().local_minus_utc() == 0 {
            out.push('Z');
        } else {
            out.push_str(&t.format("%:z").to_string());
        }
        out
    }

    pub fn serialize<S: Serializer>(t: &DateTime<FixedOffset>, s: S) -> Result<S::Ok, S::Error> {
        s.serialize_str(&format(t))
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(d: D) -> Result<DateTime<FixedOffset>, D::Error> {
        let s = Option::<String>::deserialize(d)?;
        Ok(s.as_deref().and_then(parse).unwrap_or_else(now))
    }
}
