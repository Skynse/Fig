//! Captions as cues: grouping timed words into captions, and captions into subtitle files.

use crate::TimedWord;

/// One caption: what it says, when it shows on the timeline, and its words (timed from the timeline's start).
#[derive(Clone, Debug, PartialEq)]
pub struct CaptionCue {
    pub start_sec: f64,
    pub end_sec: f64,
    pub text: String,
    pub words: Vec<TimedWord>,
}

/// How words are grouped into captions.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct CaptionGrouping {
    pub max_words: usize,
    pub max_line_chars: usize,
    pub max_lines: usize,
    pub max_pause_sec: f64,
}

impl Default for CaptionGrouping {
    fn default() -> Self {
        CaptionGrouping { max_words: 4, max_line_chars: 24, max_lines: 2, max_pause_sec: 0.6 }
    }
}

impl CaptionGrouping {
    /// The choices the captions dialog offers, quickest first.
    pub fn choices() -> [(&'static str, CaptionGrouping); 4] {
        let d = CaptionGrouping::default();
        [
            ("One word at a time", CaptionGrouping { max_words: 1, max_lines: 1, ..d }),
            ("A few words", CaptionGrouping { max_words: 3, max_lines: 1, ..d }),
            ("Short phrases", CaptionGrouping { max_words: 6, ..d }),
            ("Full lines", CaptionGrouping { max_words: 14, max_line_chars: 36, ..d }),
        ]
    }
}

/// A caption that would otherwise blink off for less than this before the next one stays up until it.
const HOLD_GAP_SEC: f64 = 0.5;

fn chars(s: &str) -> usize {
    s.chars().count()
}

/// The words as one line, spaced as they were said (no spaces are added where the language has none).
fn join(words: &[TimedWord]) -> String {
    words.iter().map(|w| w.text.as_str()).collect::<String>().trim().to_string()
}

fn ends_sentence(word: &str) -> bool {
    let w = word.trim_end_matches(['"', '\'', '”', '’', ')', ' ']);
    w.chars().last().is_some_and(|c| matches!(c, '.' | '!' | '?' | '…' | '。' | '！' | '？'))
}

/// The caption's text, broken into two lines when it is longer than one line holds, at the word that balances them.
fn lines(words: &[TimedWord], g: &CaptionGrouping) -> String {
    let text = join(words);
    if g.max_lines < 2 || chars(&text) <= g.max_line_chars || words.len() < 2 {
        return text;
    }
    let best = (1..words.len())
        .min_by_key(|&k| (chars(&join(&words[..k])) as i64 - chars(&join(&words[k..])) as i64).abs())
        .unwrap();
    format!("{}\n{}", join(&words[..best]), join(&words[best..]))
}

/// Groups words (timed from the timeline's start, in order) into captions: a caption ends at a sentence end, a pause,
/// or when it is full. Each lasts until its last word ends, or until the next one when the gap is short; none overlap.
pub fn group(words: &[TimedWord], g: &CaptionGrouping) -> Vec<CaptionCue> {
    let max_chars = g.max_line_chars * g.max_lines.max(1);
    let mut groups: Vec<Vec<TimedWord>> = Vec::new();
    let mut current: Vec<TimedWord> = Vec::new();
    for word in words {
        if let Some(last) = current.last() {
            let full = current.len() >= g.max_words || chars(&join(&current)) + 1 + chars(word.text.trim()) > max_chars;
            if full || ends_sentence(&last.text) || word.start_sec - last.end_sec > g.max_pause_sec {
                groups.push(std::mem::take(&mut current));
            }
        }
        current.push(word.clone());
    }
    if !current.is_empty() {
        groups.push(current);
    }
    let mut cues = Vec::with_capacity(groups.len());
    for (i, words) in groups.iter().enumerate() {
        let start = words[0].start_sec;
        let mut end = words.last().unwrap().end_sec.max(start + 0.1);
        if let Some(next) = groups.get(i + 1) {
            let next = next[0].start_sec;
            end = if next - end < HOLD_GAP_SEC { next } else { end.min(next) };
        }
        cues.push(CaptionCue { start_sec: start, end_sec: end, text: lines(words, g), words: words.clone() });
    }
    cues
}

/// `hh:mm:ss,mmm`, rounded to the millisecond.
pub fn srt_time(seconds: f64) -> String {
    let ms = (seconds.max(0.0) * 1000.0).round_ties_even() as u64;
    format!("{:02}:{:02}:{:02},{:03}", ms / 3_600_000, ms / 60_000 % 60, ms / 1000 % 60, ms % 1000)
}

/// SubRip: numbered cues, `hh:mm:ss,mmm --> hh:mm:ss,mmm`, the text, a blank line.
pub fn to_srt(cues: &[CaptionCue]) -> String {
    let mut out = String::new();
    for (n, cue) in cues.iter().enumerate() {
        out.push_str(&format!(
            "{}\n{} --> {}\n{}\n\n",
            n + 1,
            srt_time(cue.start_sec),
            srt_time(cue.end_sec),
            cue.text.replace("\r\n", "\n").trim()
        ));
    }
    out
}

/// The transcript: one caption a line, without times.
pub fn to_plain_text(cues: &[CaptionCue]) -> String {
    cues.iter().map(|c| format!("{}\n", c.text.replace("\r\n", " ").replace('\n', " ").trim())).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn groups_at_sentence_ends_and_writes_srt() {
        let w = |t: &str, s: f64| TimedWord::new(t, s, s + 0.3);
        let words = [w("Hi.", 0.0), w(" there", 0.4), w(" you", 0.8)];
        let cues = group(&words, &CaptionGrouping::default());
        assert_eq!(cues.len(), 2);
        assert_eq!(cues[0].text, "Hi.");
        assert_eq!(cues[0].end_sec, 0.4); // held up to the next
        assert_eq!(cues[1].text, "there you");
        assert_eq!(srt_time(3723.4567), "01:02:03,457");
        assert!(to_srt(&cues).starts_with("1\n00:00:00,000 --> 00:00:00,400\nHi.\n\n2\n"));
    }
}
