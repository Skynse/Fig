//! What a text clip shows and which of its words is being said. The words of the text are its whitespace-separated
//! pieces; the i-th piece is the i-th timed word, as long as the counts agree. A typo fixed in the inspector keeps
//! the timing; text rewritten into a different number of words loses the highlight.
//!
//! Spans are byte ranges into the string.

use std::ops::Range;

use super::{TextBody, TextStyle, TimedWord};

/// The text as drawn: line endings made `\n`, capitals when the style asks for them.
pub fn display(text: &str, style: &TextStyle) -> String {
    let normal = text.replace("\r\n", "\n");
    if style.uppercase { normal.to_uppercase() } else { normal }
}

pub fn display_of(body: &TextBody) -> String {
    display(&body.text, &body.style)
}

/// Where each word of `text` is.
pub fn spans(text: &str) -> Vec<Range<usize>> {
    let mut spans = Vec::new();
    let mut start: Option<usize> = None;
    for (i, ch) in text.char_indices() {
        match (ch.is_whitespace(), start) {
            (false, None) => start = Some(i),
            (true, Some(s)) => {
                spans.push(s..i);
                start = None;
            }
            _ => {}
        }
    }
    if let Some(s) = start {
        spans.push(s..text.len());
    }
    spans
}

/// The word being said at `local_t` seconds into the clip, or none: a pause before the first word, no timing, or a
/// style without the highlight. Between two words the last one said stays lit.
pub fn active_index(body: &TextBody, local_t: f64) -> Option<usize> {
    let words = &body.words;
    if !body.style.highlight || words.is_empty() || local_t < words[0].start_sec {
        return None;
    }
    if spans(&display_of(body)).len() != words.len() {
        return None;
    }
    let mut index = 0;
    for (i, w) in words.iter().enumerate().skip(1) {
        if w.start_sec > local_t {
            break;
        }
        index = i;
    }
    Some(index)
}

/// The bytes of the word being said at `local_t` in [`display_of`], or none.
pub fn active_span(body: &TextBody, local_t: f64) -> Option<Range<usize>> {
    let index = active_index(body, local_t)?;
    spans(&display_of(body)).into_iter().nth(index)
}

/// The shortest time a new word is given before it borrows from its neighbours.
const MIN_WORD_SEC: f64 = 0.08;

/// Timed words for `text`, a rewrite of the words `old` timed: a word that is still there (matched in order,
/// ignoring case and punctuation) keeps its time, and new words share the time between the kept ones around them by
/// their length. A typo fixed without changing the count keeps every time. Typed text (no timed words) stays
/// untimed.
pub fn retime(old: &[TimedWord], text: &str) -> Vec<TimedWord> {
    let tokens: Vec<&str> = spans(text).into_iter().map(|s| &text[s]).collect();
    if old.is_empty() || tokens.is_empty() {
        return Vec::new();
    }
    if tokens.len() == old.len() {
        return old.iter().zip(&tokens).map(|(w, t)| TimedWord { text: (*t).into(), ..w.clone() }).collect();
    }
    let mut words: Vec<Option<TimedWord>> = vec![None; tokens.len()];

    // the longest run of words kept in order (LCS), each kept word with its old time
    let a: Vec<String> = old.iter().map(|w| key(&w.text)).collect();
    let b: Vec<String> = tokens.iter().map(|t| key(t)).collect();
    let (n, m) = (a.len(), b.len());
    let mut lcs = vec![vec![0usize; m + 1]; n + 1];
    for i in (0..n).rev() {
        for j in (0..m).rev() {
            lcs[i][j] =
                if a[i] == b[j] && !a[i].is_empty() { lcs[i + 1][j + 1] + 1 } else { lcs[i + 1][j].max(lcs[i][j + 1]) };
        }
    }
    let (mut i, mut j) = (0, 0);
    while i < n && j < m {
        if a[i] == b[j] && !a[i].is_empty() {
            words[j] = Some(TimedWord { text: tokens[j].into(), ..old[i].clone() });
            i += 1;
            j += 1;
        } else if lcs[i + 1][j] >= lcs[i][j + 1] {
            i += 1;
        } else {
            j += 1;
        }
    }

    // each run of new words shares the time between the kept words around it
    let (first, last) = (old[0].start_sec, old[old.len() - 1].end_sec);
    let mut j = 0;
    while j < words.len() {
        if words[j].is_some() {
            j += 1;
            continue;
        }
        let mut k = j;
        while k < words.len() && words[k].is_none() {
            k += 1;
        }
        let mut from = if j > 0 { words[j - 1].as_ref().unwrap().end_sec } else { first };
        let mut to = if k < words.len() { words[k].as_ref().unwrap().start_sec } else { last };
        if to - from < MIN_WORD_SEC * (k - j) as f64 {
            // no room between them: share the kept word before (or after) as well
            if j > 0 {
                j -= 1;
                from = words[j].as_ref().unwrap().start_sec;
            } else if k < words.len() {
                to = words[k].as_ref().unwrap().end_sec;
                k += 1;
            }
        }
        let to = to.max(from);
        let lengths: Vec<f64> = (j..k).map(|i| tokens[i].chars().count().max(1) as f64).collect();
        let total: f64 = lengths.iter().sum();
        let mut at = from;
        for i in j..k {
            let end = at + (to - from) * lengths[i - j] / total;
            words[i] = Some(TimedWord::new(tokens[i], at, end));
            at = end;
        }
        j = k;
    }
    words.into_iter().map(Option::unwrap).collect()
}

/// A word for matching: lower case, without punctuation.
fn key(word: &str) -> String {
    word.chars().filter(|c| c.is_alphanumeric()).flat_map(char::to_lowercase).collect()
}

/// Moves words `offset` seconds earlier, for the right half of a cut. Words said before the cut stay (at negative
/// times): the text still holds them, and each word must keep its place.
pub fn shift(words: &mut [TimedWord], offset: f64) {
    if offset == 0.0 {
        return;
    }
    for w in words {
        w.start_sec -= offset;
        w.end_sec -= offset;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn w(t: &str, s: f64, e: f64) -> TimedWord {
        TimedWord::new(t, s, e)
    }

    #[test]
    fn spans_split_on_whitespace() {
        assert_eq!(spans("  hi there\nyou "), vec![2..4, 5..10, 11..14]);
    }

    #[test]
    fn retime_keeps_matched_words_and_shares_gaps() {
        let old = [w("hello", 0.0, 0.4), w("world", 1.0, 1.4)];
        let new = retime(&old, "Hello big world");
        assert_eq!(new[0], w("Hello", 0.0, 0.4));
        assert_eq!(new[2], w("world", 1.0, 1.4));
        assert_eq!((new[1].start_sec, new[1].end_sec), (0.4, 1.0));
        assert!(retime(&[], "typed").is_empty());
    }
}
