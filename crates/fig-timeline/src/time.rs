//! Frame grids and timecode.

use super::FrameRate;

/// The time of the frame nearest `sec`.
pub fn snap_to_frame(sec: f64, rate: FrameRate) -> f64 {
    rate.to_seconds(rate.to_frame(sec))
}

/// Non-drop timecode (HH:MM:SS:FF) of a time.
pub fn format_timecode(seconds: f64, rate: FrameRate) -> String {
    let seconds = if seconds.is_finite() && seconds >= 0.0 { seconds } else { 0.0 };
    let fps = (rate.fps().round_ties_even() as i64).max(1);
    let total_frames = (seconds * rate.fps()).round_ties_even() as i64;
    let frames = total_frames % fps;
    let total_seconds = total_frames / fps;
    format!("{:02}:{:02}:{:02}:{:02}", total_seconds / 3600, total_seconds / 60 % 60, total_seconds % 60, frames)
}

/// Reads "HH:MM:SS:FF", "MM:SS:FF" (the last field is always frames), "SS:FF", or plain seconds such as "4" or
/// "4.5".
pub fn parse_timecode(text: &str, rate: FrameRate) -> Option<f64> {
    let text = text.trim();
    if text.is_empty() {
        return None;
    }
    if !text.contains(':') {
        return text.parse::<f64>().ok().filter(|s| *s >= 0.0 && s.is_finite());
    }
    let parts: Vec<&str> = text.split(':').collect();
    if !(2..=4).contains(&parts.len()) {
        return None;
    }
    let mut values = Vec::with_capacity(parts.len());
    for p in &parts {
        if p.is_empty() || !p.bytes().all(|b| b.is_ascii_digit()) {
            return None;
        }
        values.push(p.parse::<i64>().ok()?);
    }
    let fps = (rate.fps().round_ties_even() as i64).max(1);
    let frames = *values.last().unwrap();
    let (mut secs, mut unit) = (0i64, 1i64);
    for v in values[..values.len() - 1].iter().rev() {
        secs += v * unit;
        unit *= 60;
    }
    Some(secs as f64 + frames as f64 / fps as f64)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn timecode_round_trips() {
        let r = FrameRate::new(30, 1);
        assert_eq!(format_timecode(3723.5, r), "01:02:03:15");
        assert_eq!(parse_timecode("01:02:03:15", r), Some(3723.5));
        assert_eq!(parse_timecode("4.5", r), Some(4.5));
        assert_eq!(parse_timecode("1:x", r), None);
        assert_eq!(parse_timecode("-1", r), None);
    }
}
