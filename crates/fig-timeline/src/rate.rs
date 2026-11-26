use std::fmt;

use serde::{Deserialize, Deserializer, Serialize, Serializer};

/// A frame rate as a reduced fraction.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct FrameRate {
    num: i32,
    den: i32,
}

impl FrameRate {
    /// The rate `num`/`den`, reduced. Panics when `den` is not positive.
    pub fn new(num: i32, den: i32) -> Self {
        Self::try_new(num, den).expect("Denominator must be positive")
    }

    pub fn try_new(num: i32, den: i32) -> Option<Self> {
        if den <= 0 {
            return None;
        }
        let g = gcd(num, den);
        Some(FrameRate { num: num / g, den: den / g })
    }

    pub fn num(self) -> i32 {
        self.num
    }

    pub fn den(self) -> i32 {
        self.den
    }

    pub fn fps(self) -> f64 {
        self.num as f64 / self.den as f64
    }

    pub fn frames_per_second(self) -> i64 {
        let fps = self.fps();
        if fps >= 1.0 { fps as i64 } else { 1 }
    }

    pub fn to_frame(self, seconds: f64) -> i64 {
        round_half_even(seconds * self.num as f64 / self.den as f64) as i64
    }

    pub fn to_seconds(self, frame: i64) -> f64 {
        frame as f64 * self.den as f64 / self.num as f64
    }

    /// The exact rate for a rounded one (29.97 is 30000/1001).
    pub fn common(fps: f64) -> Self {
        let near = |v: f64| (fps - v).abs() < 0.001;
        if near(23.976) {
            FrameRate::new(24000, 1001)
        } else if near(29.97) {
            FrameRate::new(30000, 1001)
        } else if near(59.94) {
            FrameRate::new(60000, 1001)
        } else if near(25.0) {
            FrameRate::new(25, 1)
        } else if near(50.0) {
            FrameRate::new(50, 1)
        } else if near(60.0) {
            FrameRate::new(60, 1)
        } else {
            FrameRate::new(round_half_even(fps) as i32, 1)
        }
    }

    /// The rates a project can be set to, slowest first.
    pub const STANDARD: [FrameRate; 8] = [
        FrameRate { num: 24000, den: 1001 },
        FrameRate { num: 24, den: 1 },
        FrameRate { num: 25, den: 1 },
        FrameRate { num: 30000, den: 1001 },
        FrameRate { num: 30, den: 1 },
        FrameRate { num: 50, den: 1 },
        FrameRate { num: 60000, den: 1001 },
        FrameRate { num: 60, den: 1 },
    ];

    /// A rate fit to edit at, from what a file reports: the nearest standard rate within 1%, else a whole rate from
    /// 10 to 120 fps, else none. Files often report junk (a recorded WebM says 1000/1, a VFR one 13698/457).
    pub fn plausible(reported: Option<FrameRate>) -> Option<FrameRate> {
        let r = reported?;
        let fps = r.fps();
        if r.den <= 0 || !fps.is_finite() || fps <= 0.0 {
            return None;
        }
        let nearest = Self::STANDARD
            .iter()
            .copied()
            .min_by(|a, b| (a.fps() - fps).abs().total_cmp(&(b.fps() - fps).abs()))
            .unwrap();
        if (nearest.fps() - fps).abs() / nearest.fps() < 0.01 {
            return Some(nearest);
        }
        (10.0..=120.0).contains(&fps).then(|| FrameRate::new(round_half_even(fps) as i32, 1))
    }

    /// "29.97 fps", "25 fps".
    pub fn label(self) -> String {
        let v = (self.fps() * 1000.0).round() / 1000.0;
        let s = format!("{v:.3}");
        format!("{} fps", s.trim_end_matches('0').trim_end_matches('.'))
    }
}

impl Default for FrameRate {
    /// `default(FrameRate)` in the C# build was 0/0; 30 fps is what every caller wanted from it.
    fn default() -> Self {
        FrameRate { num: 30, den: 1 }
    }
}

impl fmt::Display for FrameRate {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}/{}", self.num, self.den)
    }
}

fn gcd(mut a: i32, mut b: i32) -> i32 {
    while b != 0 {
        let t = b;
        b = a % b;
        a = t;
    }
    a
}

/// `Math.Round`: halves go to the even number.
pub(crate) fn round_half_even(v: f64) -> f64 {
    v.round_ties_even()
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Wire {
    num: i32,
    den: i32,
}

impl Serialize for FrameRate {
    fn serialize<S: Serializer>(&self, s: S) -> Result<S::Ok, S::Error> {
        Wire { num: self.num, den: self.den }.serialize(s)
    }
}

impl<'de> Deserialize<'de> for FrameRate {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        let w = Wire::deserialize(d)?;
        FrameRate::try_new(w.num, w.den).ok_or_else(|| serde::de::Error::custom("Denominator must be positive"))
    }
}

/// The shape of the picture: follow the footage, or a fixed one for where the video is going.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Default, Serialize, Deserialize)]
pub enum FrameShape {
    /// The first video's own shape (what a project gets until one is picked).
    #[default]
    Auto,
    /// 16:9, for YouTube and screens.
    Wide,
    /// 9:16, for phones: Shorts, Reels, TikTok.
    Tall,
    /// 1:1.
    Square,
    /// 4:5, for feeds.
    Portrait,
}

impl FrameShape {
    pub const ALL: [FrameShape; 5] =
        [FrameShape::Auto, FrameShape::Wide, FrameShape::Tall, FrameShape::Square, FrameShape::Portrait];

    pub fn label(self) -> &'static str {
        match self {
            FrameShape::Wide => "16:9",
            FrameShape::Tall => "9:16",
            FrameShape::Square => "1:1",
            FrameShape::Portrait => "4:5",
            FrameShape::Auto => "Auto",
        }
    }

    /// The frame a shape exports at: its short side 1080 pixels. Auto is the first video's size, or 1920×1080 with
    /// no footage.
    pub fn size(self, footage_width: i32, footage_height: i32) -> (i32, i32) {
        match self {
            FrameShape::Wide => (1920, 1080),
            FrameShape::Tall => (1080, 1920),
            FrameShape::Square => (1080, 1080),
            FrameShape::Portrait => (1080, 1350),
            FrameShape::Auto if footage_width > 0 && footage_height > 0 => (footage_width, footage_height),
            FrameShape::Auto => (1920, 1080),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn plausible_snaps_junk_rates() {
        assert_eq!(FrameRate::plausible(Some(FrameRate::new(1000, 1))), None);
        assert_eq!(FrameRate::plausible(Some(FrameRate::new(13698, 457))), Some(FrameRate::new(30000, 1001)));
        assert_eq!(FrameRate::plausible(Some(FrameRate::new(15, 1))), Some(FrameRate::new(15, 1)));
        assert_eq!(FrameRate::new(60, 2), FrameRate::new(30, 1));
        assert_eq!(FrameRate::new(30000, 1001).label(), "29.97 fps");
    }
}
