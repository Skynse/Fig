//! Captions on the timeline, text edits, layers (text and stills placed and moved on the frame), and reversing clips.

use std::collections::HashSet;

use indexmap::IndexMap;

use super::history::Coalesce;
use super::{TimelineEditor, insert_clip, overlaps};
use fig_timeline::{
    CaptionCue, Clip, ClipBody, Facet, KeyframeEasing, KeyframePoint, LayerTransform, MediaAsset, ParamValue,
    Selectable, TextBody, TextPreset, TextStyle, TimedWord, Track, TrackKind, clip_time, properties, text_words, time,
};

/// Where generated captions sit: across the middle, low enough to clear the subject, high enough to clear a phone
/// app's buttons.
pub const CAPTION_LAYOUT: LayerTransform = LayerTransform { center_x: 0.5, center_y: 0.72, scale: 1.0, rotation: 0.0 };

/// The caption text clips on a track, in order.
pub fn captions_on(track: &Track) -> Vec<&Clip> {
    let mut c: Vec<&Clip> = track.clips.iter().filter(|c| c.as_text().is_some_and(|t| t.is_caption)).collect();
    c.sort_by(|a, b| a.start_sec.total_cmp(&b.start_sec));
    c
}

/// A caption's text as one line.
fn flat(text: &str) -> String {
    text.split(['\n', '\r']).map(str::trim).filter(|s| !s.is_empty()).collect::<Vec<_>>().join(" ")
}

fn shifted(words: &[TimedWord], by: f64) -> Vec<TimedWord> {
    let mut w = words.to_vec();
    text_words::shift(&mut w, by);
    w
}

impl TimelineEditor {
    /// Puts captions on a new video track on top of the picture, one text clip each, as one undo step. Times are
    /// snapped to frames and the clips never overlap. Returns the new track's id, or none when there was nothing to
    /// add.
    pub fn add_captions(
        &mut self,
        cues: &[CaptionCue],
        style: Option<TextStyle>,
        layout: Option<LayerTransform>,
    ) -> Option<String> {
        let rate = self.doc().rate;
        let frame = 1.0 / rate.fps();
        let mut track = Track::new(TrackKind::Video);
        track.name = Some("Captions".into());
        let mut sorted: Vec<&CaptionCue> = cues.iter().collect();
        sorted.sort_by(|a, b| a.start_sec.total_cmp(&b.start_sec));
        let mut last_end = 0.0f64;
        for cue in sorted {
            let start = last_end.max(time::snap_to_frame(cue.start_sec.max(0.0), rate));
            let mut end = time::snap_to_frame(cue.end_sec, rate);
            if end - start < frame - 1e-9 {
                end = start + frame;
            }
            let words = cue
                .words
                .iter()
                .map(|w| TimedWord::new(w.text.trim(), w.start_sec - start, w.end_sec - start))
                .collect();
            let clip = Clip::new(TextBody {
                text: cue.text.clone(),
                is_caption: true,
                style: style.clone().unwrap_or_else(TextStyle::captions),
                layout: layout.unwrap_or(CAPTION_LAYOUT),
                words,
            })
            .at(start, end - start);
            track.clips.push(clip);
            last_end = end;
        }
        let first = track.clips.first()?.id.clone();
        let id = track.id.clone();
        self.edit(|e| {
            let tracks = &mut e.doc_mut().tracks;
            match tracks.iter().position(|t| t.kind == TrackKind::Video) {
                Some(i) => tracks.insert(i, track),
                None => tracks.push(track),
            }
        });
        self.selection.select_only(&first);
        Some(id)
    }

    /// The timeline's captions (enabled caption text clips on visible tracks), in order, for a subtitle file. Times
    /// are held to `start_sec`..`end_sec` and measured from `start_sec`, so the file matches a video exported from
    /// that range.
    pub fn caption_cues(&self, start_sec: f64, end_sec: f64) -> Vec<CaptionCue> {
        let mut clips: Vec<&Clip> = self
            .doc()
            .tracks
            .iter()
            .filter(|t| t.kind == TrackKind::Video && t.visible)
            .flat_map(|t| t.clips.iter())
            .filter(|c| {
                c.as_text().is_some_and(|t| t.is_caption && !t.text.trim().is_empty())
                    && c.enabled
                    && c.start_sec < end_sec
                    && c.end_sec() > start_sec
            })
            .collect();
        clips.sort_by(|a, b| a.start_sec.total_cmp(&b.start_sec));
        clips
            .into_iter()
            .map(|c| {
                let t = c.as_text().unwrap();
                CaptionCue {
                    start_sec: c.start_sec.max(start_sec) - start_sec,
                    end_sec: c.end_sec().min(end_sec) - start_sec,
                    text: text_words::display_of(t),
                    words: shifted(&t.words, start_sec - c.start_sec),
                }
            })
            .collect()
    }

    /// True when the timeline has any caption to export.
    pub fn has_captions(&self) -> bool {
        self.doc().clips().any(|c| c.as_text().is_some_and(|t| t.is_caption))
    }

    /// The listed clips and, for each caption among them, every caption on its track: what a style change reaches
    /// when it is set for all captions.
    pub fn with_caption_trackmates(&self, clip_ids: &[String]) -> Vec<String> {
        let mut out: Vec<String> = Vec::new();
        let mut seen = HashSet::new();
        let mut push = |id: &String, out: &mut Vec<String>| {
            if seen.insert(id.clone()) {
                out.push(id.clone());
            }
        };
        for id in clip_ids {
            push(id, &mut out);
        }
        for id in clip_ids {
            if self.find_clip(id).and_then(Clip::as_text).is_some_and(|t| t.is_caption)
                && let Some(track) = self.find_clip_track(id)
            {
                for c in captions_on(track) {
                    push(&c.id, &mut out);
                }
            }
        }
        out
    }

    /// Rewrites what text clips say, as one undo step (typing joins into it). A caption's words are retimed, so the
    /// spoken word stays lit through the edit.
    pub fn set_text(&mut self, clip_ids: &[String], text: &str) {
        let targets: Vec<String> = clip_ids
            .iter()
            .filter(|id| self.find_clip(id).and_then(Clip::as_text).is_some_and(|t| t.text != text))
            .cloned()
            .collect();
        if targets.is_empty() {
            return;
        }
        let mut key: Vec<String> = targets.clone();
        key.sort();
        self.edit_coalescing(format!("text:{}", key.join(",")), Coalesce::Restart, |e| {
            for id in &targets {
                if let Some(t) = e.doc_mut().find_clip_mut(id).and_then(Clip::as_text_mut) {
                    t.words = text_words::retime(&t.words, text);
                    t.text = text.into();
                }
            }
        });
    }

    /// Replaces `find` with `replacement` in every caption (on one track, or all), as one undo step. Returns how many
    /// were replaced.
    pub fn replace_in_captions(
        &mut self,
        find: &str,
        replacement: &str,
        match_case: bool,
        track_id: Option<&str>,
    ) -> usize {
        if find.is_empty() {
            return 0;
        }
        let mut changes: Vec<(String, String)> = Vec::new();
        let mut count = 0;
        for track in self.doc().tracks.iter().filter(|t| track_id.is_none_or(|id| id == t.id)) {
            for clip in captions_on(track) {
                let text = &clip.as_text().unwrap().text;
                let (n, replaced) = replace_counting(text, find, replacement, match_case);
                if n > 0 {
                    count += n;
                    changes.push((clip.id.clone(), replaced));
                }
            }
        }
        if count == 0 {
            return 0;
        }
        self.break_coalescing();
        self.edit(|e| {
            for (id, text) in &changes {
                if let Some(t) = e.doc_mut().find_clip_mut(id).and_then(Clip::as_text_mut) {
                    t.words = text_words::retime(&t.words, text);
                    t.text = text.clone();
                }
            }
        });
        count
    }

    /// Joins a caption with the next one on its track: one caption from the first's start to the second's end, saying
    /// both, with both sets of word timings. One undo step. Returns the joined caption's id.
    pub fn merge_caption_with_next(&mut self, clip_id: &str) -> Option<String> {
        let first = self.find_clip(clip_id).filter(|c| c.as_text().is_some_and(|t| t.is_caption))?.clone();
        let track = self.find_clip_track(clip_id)?;
        let next = captions_on(track).into_iter().find(|c| c.start_sec > first.start_sec)?.clone();
        let mut merged = first.clone();
        let (ft, nt) = (first.as_text().unwrap(), next.as_text().unwrap());
        let text = format!("{} {}", flat(&ft.text), flat(&nt.text));
        let words = if !ft.words.is_empty() && !nt.words.is_empty() {
            ft.words.iter().cloned().chain(shifted(&nt.words, first.start_sec - next.start_sec)).collect()
        } else {
            text_words::retime(&ft.words, &text)
        };
        let mt = merged.as_text_mut().unwrap();
        mt.text = text;
        mt.words = words;
        merged.dur_sec = next.end_sec() - first.start_sec;
        merged.fade_out_sec = next.fade_out_sec;
        merged.transition_out = next.transition_out.clone();
        let id = merged.id.clone();
        self.edit(|e| {
            let doc = e.doc_mut();
            let (t, _) = doc.locate_clip(&first.id).unwrap();
            doc.tracks[t].clips.retain(|c| c.id != first.id && c.id != next.id);
            insert_clip(&mut doc.tracks[t], merged);
        });
        self.selection.select_only(&id);
        Some(id)
    }

    /// Splits a caption in two before the word at byte `char_index` of its text (inside a word: at that word's
    /// start). The cut falls where that word is said, or in proportion to the text without word timings. One undo
    /// step. Returns the two halves' ids.
    pub fn split_caption(&mut self, clip_id: &str, char_index: usize) -> Option<(String, String)> {
        let caption = self.find_clip(clip_id).filter(|c| c.as_text().is_some_and(|t| t.is_caption))?.clone();
        let body = caption.as_text().unwrap();
        let spans = text_words::spans(&body.text);
        // the first word that starts at or after the cut, or that the cut falls inside
        let k = spans.iter().position(|s| s.end > char_index)?;
        if k == 0 {
            return None;
        }
        let at = spans[k].start;
        let timed = body.words.len() == spans.len();
        let rate = self.doc().rate;
        let frame = 1.0 / rate.fps();
        let raw = if timed {
            body.words[k].start_sec
        } else {
            caption.dur_sec * body.text[..at].chars().count() as f64 / body.text.chars().count().max(1) as f64
        };
        let cut = (time::snap_to_frame(caption.start_sec + raw, rate) - caption.start_sec)
            .clamp(frame, caption.dur_sec - frame);
        if cut < frame - 1e-9 || caption.dur_sec - cut < frame - 1e-9 {
            return None;
        }
        let mut left = caption.clone();
        left.dur_sec = cut;
        left.fade_out_sec = 0.0;
        left.transition_out = None;
        clip_time::keep_before(&mut left, cut);
        {
            let lt = left.as_text_mut().unwrap();
            lt.text = body.text[..at].trim().into();
            lt.words = if timed { body.words[..k].to_vec() } else { Vec::new() };
        }
        let mut right = caption.duplicate();
        right.start_sec = caption.start_sec + cut;
        right.dur_sec = caption.dur_sec - cut;
        right.fade_in_sec = 0.0;
        right.transition_in = None;
        clip_time::keep_after(&mut right, cut);
        {
            let rt = right.as_text_mut().unwrap();
            rt.text = body.text[at..].trim().into();
            rt.words = if timed { shifted(&body.words[k..], cut) } else { Vec::new() };
        }
        let ids = (left.id.clone(), right.id.clone());
        self.edit(|e| {
            let doc = e.doc_mut();
            let (t, c) = doc.locate_clip(&caption.id).unwrap();
            doc.tracks[t].clips.remove(c);
            insert_clip(&mut doc.tracks[t], left);
            insert_clip(&mut doc.tracks[t], right);
        });
        self.selection.select_only(&ids.1);
        Some(ids)
    }

    /// Takes a caption off its track.
    pub fn remove_caption(&mut self, clip_id: &str) {
        if !self.find_clip(clip_id).and_then(Clip::as_text).is_some_and(|t| t.is_caption) {
            return;
        }
        self.edit(|e| {
            for t in &mut e.doc_mut().tracks {
                t.clips.retain(|c| c.id != clip_id);
            }
        });
        self.selection.clear();
    }

    // ---- layers -----------------------------------------------------------------------------

    /// Adds a text clip at `start_sec`, on `track_id` when it has room there, else on the first video track that does,
    /// else a new one. One undo step; the new clip is selected. Returns its id.
    pub fn add_text(
        &mut self,
        track_id: Option<&str>,
        start_sec: f64,
        text: &str,
        style: TextStyle,
        dur_sec: f64,
        layout: LayerTransform,
    ) -> String {
        let start = time::snap_to_frame(start_sec, self.doc().rate).max(0.0);
        let clip =
            Clip::new(TextBody { text: text.into(), style, layout, ..Default::default() }).at(start, dur_sec.max(0.1));
        self.place_layer(track_id, clip)
    }

    /// Adds a library title at `start_sec`.
    pub fn add_text_preset(&mut self, track_id: Option<&str>, start_sec: f64, preset: &TextPreset) -> String {
        self.add_text(track_id, start_sec, preset.text, preset.style.clone(), preset.dur_sec, preset.layout)
    }

    /// Places a drawn layer on a video track with room for it (a new one if none has) and selects it.
    fn place_layer(&mut self, track_id: Option<&str>, clip: Clip) -> String {
        let fits = |t: &Track| t.kind == TrackKind::Video && !overlaps(t, clip.start_sec, clip.dur_sec, None);
        let wanted = track_id.and_then(|id| self.find_track(id)).filter(|t| fits(t)).map(|t| t.id.clone());
        let track = wanted.or_else(|| self.doc().tracks.iter().find(|t| fits(t)).map(|t| t.id.clone()));
        let id = clip.id.clone();
        self.edit(|e| {
            let track = track.unwrap_or_else(|| e.add_track(TrackKind::Video));
            insert_clip(e.doc_mut().find_track_mut(&track).unwrap(), clip);
        });
        self.selection.select_only(&id);
        id
    }

    /// The clips an inspector edit of `facet` reaches: each listed clip, and the clips linked to it, that have the
    /// facet (fading a video also fades its linked audio).
    pub fn edit_targets(&self, clip_ids: &[String], facet: Facet) -> Vec<String> {
        let mut seen = HashSet::new();
        clip_ids
            .iter()
            .flat_map(|id| self.link_group(id))
            .filter(|id| self.find_clip(id).is_some_and(|c| c.facets().contains(facet)))
            .filter(|id| seen.insert(id.clone()))
            .collect()
    }

    /// Gives each listed layer its own layout at the playhead, as one undo step (a gizmo drag joins into it): a
    /// position value that is animated gets a keyframe there, the rest change the layout itself.
    pub fn set_layouts(&mut self, layouts: &IndexMap<String, LayerTransform>, playhead_sec: f64) {
        let changed = layouts.iter().any(|(id, l)| {
            self.find_clip(id)
                .is_some_and(|c| c.layout().is_some() && properties::layout_at(c, playhead_sec - c.start_sec) != *l)
        });
        if !changed {
            return;
        }
        let mut ids: Vec<&String> = layouts.keys().collect();
        ids.sort();
        let key = format!(
            "layouts:{}@{}",
            ids.iter().map(|s| s.as_str()).collect::<Vec<_>>().join("|"),
            (playhead_sec * 1e4).round()
        );
        self.edit_coalescing(key, Coalesce::Restart, |e| {
            for (id, target) in layouts {
                let Some(clip) = e.doc_mut().find_clip_mut(id) else { continue };
                if clip.layout().is_none() {
                    continue;
                }
                let local = (playhead_sec - clip.start_sec).clamp(0.0, clip.dur_sec);
                for (property, of) in properties::layout_parts() {
                    let value = of(target).clamp(property.def.min, property.def.max);
                    if !property.is_animated(clip) {
                        (property.set)(clip, ParamValue::Double(value));
                        continue;
                    }
                    let keys = clip.keyframes.get_mut(property.key()).unwrap();
                    match keys.iter().position(|k| (k.time_sec - local).abs() < 1e-6) {
                        Some(i) => keys[i].value = ParamValue::Double(value),
                        None => {
                            keys.push(KeyframePoint::eased(local, ParamValue::Double(value), KeyframeEasing::Linear));
                            keys.sort_by(|a, b| a.time_sec.total_cmp(&b.time_sec));
                        }
                    }
                }
            }
        });
    }

    // ---- reversing ----------------------------------------------------------------------------

    /// The clips reversing these would change, linked partners included (a video and its sound reverse together).
    pub fn reverse_targets(&self, clip_ids: &[String]) -> Vec<String> {
        let mut seen = HashSet::new();
        clip_ids.iter().flat_map(|id| self.link_group(id)).filter(|id| seen.insert(id.clone())).collect()
    }

    /// Plays clips backwards, as one undo step. A clip with footage (video, sound) switches to the other direction's
    /// copy of its media, which `counterpart` gives for an asset (the reversed copy for an original, the original for
    /// a copy), its range mirrored so it shows the same stretch the other way round; its keyframes stay where they
    /// are. A clip without footage (text, a still) has its animation reversed. `media` finds an asset by id. False
    /// when a clip with footage has no counterpart yet, changing nothing.
    pub fn reverse(
        &mut self,
        clip_ids: &[String],
        media: impl Fn(&str) -> Option<MediaAsset>,
        counterpart: impl Fn(&MediaAsset) -> Option<MediaAsset>,
    ) -> bool {
        let mut plan: Vec<(String, Option<(String, f64)>)> = Vec::new();
        for id in self.reverse_targets(clip_ids) {
            let clip = self.find_clip(&id).unwrap();
            if !clip.has_source_range() {
                plan.push((id, None));
                continue;
            }
            let Some(asset) = clip.media_id().and_then(&media) else { return false };
            let Some(other) = counterpart(&asset) else { return false };
            // the copy's length is the mirror line: time t in one is length - t in the other
            let length = if asset.reverse_of.is_some() { asset.duration_sec } else { other.duration_sec };
            plan.push((id, Some((other.id, length))));
        }
        if plan.is_empty() {
            return false;
        }
        self.edit(|e| {
            for (id, other) in &plan {
                let clip = e.doc_mut().find_clip_mut(id).unwrap();
                match other {
                    None => mirror_keyframes(clip),
                    Some((source, length)) => {
                        let (i, o) = (clip.source_in(), clip.source_out());
                        match &mut clip.body {
                            ClipBody::Video(v) => v.source_id = source.clone(),
                            ClipBody::Audio(a) => a.source_id = source.clone(),
                            _ => {}
                        }
                        clip.set_source_range((length - o).max(0.0), (length - i).max(0.0));
                    }
                }
            }
        });
        true
    }
}

/// Each animation run backwards: a key at t moves to (length - t), and the movement out of each key is the one that
/// led into it before, its easing turned round (an ease in becomes an ease out).
fn mirror_keyframes(clip: &mut Clip) {
    let dur = clip.dur_sec;
    let turned = |e: KeyframeEasing| match e {
        KeyframeEasing::EaseIn => KeyframeEasing::EaseOut,
        KeyframeEasing::EaseOut => KeyframeEasing::EaseIn,
        other => other,
    };
    for keys in clip.keyframes.values_mut() {
        let mirrored: Vec<KeyframePoint> = (0..keys.len())
            .rev()
            .map(|i| {
                let easing = if i > 0 { turned(keys[i - 1].easing) } else { KeyframeEasing::Linear };
                KeyframePoint::eased((dur - keys[i].time_sec).clamp(0.0, dur), keys[i].value, easing)
            })
            .collect();
        *keys = mirrored;
    }
}

/// `text` with every `find` replaced, and how many there were.
fn replace_counting(text: &str, find: &str, replacement: &str, match_case: bool) -> (usize, String) {
    if match_case {
        return (text.matches(find).count(), text.replace(find, replacement));
    }
    let (lower, needle) = (text.to_lowercase(), find.to_lowercase());
    // byte offsets match only when lowercasing keeps lengths; otherwise fall back to the exact match
    if lower.len() != text.len() || needle.len() != find.len() {
        return (text.matches(find).count(), text.replace(find, replacement));
    }
    let mut out = String::new();
    let mut n = 0;
    let mut last = 0;
    for (i, _) in lower.match_indices(&needle) {
        out.push_str(&text[last..i]);
        out.push_str(replacement);
        last = i + find.len();
        n += 1;
    }
    out.push_str(&text[last..]);
    (n, out)
}
