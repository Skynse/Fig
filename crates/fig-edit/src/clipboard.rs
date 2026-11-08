//! Copy, cut, paste and duplicate for clips. The clipboard lives on the editor, in memory.

use std::collections::{HashMap, HashSet};

use super::magnet::{in_time_order, insert_index, pack_track};
use super::{TimelineEditor, insert_clip};
use fig_timeline::serde_util::new_id_n;
use fig_timeline::{Clip, ClipBody, Timeline, Track, TrackKind, split, time};

#[derive(Clone, Debug)]
struct ClipboardEntry {
    track_id: String,
    kind: TrackKind,
    clip: Clip,
    offset_sec: f64,
}

#[derive(Clone, Debug, Default)]
pub(crate) struct Clipboard {
    clips: Vec<ClipboardEntry>,
    pub(crate) effects: Vec<super::effects::CopiedEffect>,
    pub(crate) holds_effects: bool,
}

/// Puts `clip` on the track at `pos_sec` (snapped to a frame), over whatever is there: the parts of the clips it
/// covers that stick out either side stay, as new clips.
pub(crate) fn overwrite_insert(doc: &mut Timeline, track: usize, mut clip: Clip, pos_sec: f64) {
    let pos = time::snap_to_frame(pos_sec, doc.rate);
    clip.start_sec = pos;
    let end = pos + clip.dur_sec;
    let track = &mut doc.tracks[track];
    let (covered, kept): (Vec<Clip>, Vec<Clip>) =
        track.clips.drain(..).partition(|c| c.start_sec < end && c.end_sec() > pos);
    track.clips = kept;
    for c in covered {
        let before = pos - c.start_sec;
        if before > 0.0001 {
            insert_clip(track, split::before(&c, before));
        }
        let from = end - c.start_sec;
        if c.dur_sec - from > 0.0001 {
            insert_clip(track, split::after(&c, from));
        }
    }
    insert_clip(track, clip);
}

/// New link groups for copies: clips linked to each other stay linked to each other only; a group left with one
/// member is no group.
fn relink(copies: &mut [Clip]) {
    let mut groups: HashMap<String, String> = HashMap::new();
    for c in copies.iter_mut() {
        if let Some(g) = c.link_group_id.as_ref().filter(|g| !g.is_empty()) {
            c.link_group_id = Some(groups.entry(g.clone()).or_insert_with(new_id_n).clone());
        }
    }
    let mut counts: HashMap<String, usize> = HashMap::new();
    for g in copies.iter().filter_map(|c| c.link_group_id.clone()) {
        *counts.entry(g).or_default() += 1;
    }
    for c in copies.iter_mut() {
        if c.link_group_id.as_ref().is_some_and(|g| counts[g] == 1) {
            c.link_group_id = None;
        }
    }
}

fn fresh_copy(clip: &Clip) -> Clip {
    let mut copy = clip.duplicate();
    copy.transition_in = None;
    copy.transition_out = None;
    copy
}

impl TimelineEditor {
    pub fn has_clipboard(&self) -> bool {
        !self.clipboard.clips.is_empty()
    }

    /// The selected clips and everything linked to them.
    fn selected_with_linked(&self) -> Vec<String> {
        let mut seen = HashSet::new();
        let mut ids = Vec::new();
        for seed in self.selected_group_seeds() {
            for id in self.link_group(&seed) {
                if seen.insert(id.clone()) {
                    ids.push(id);
                }
            }
        }
        ids
    }

    /// Copies the selected clips (and everything linked to them). False when nothing is selected.
    pub fn copy_selected(&mut self) -> bool {
        let ids = self.selected_with_linked();
        if ids.is_empty() {
            return false;
        }
        let clips: Vec<&Clip> = ids.iter().filter_map(|id| self.find_clip(id)).collect();
        let origin = clips.iter().map(|c| c.start_sec).fold(f64::INFINITY, f64::min);
        self.clipboard.clips = clips
            .iter()
            .map(|c| {
                let track = self.find_clip_track(&c.id).unwrap();
                ClipboardEntry {
                    track_id: track.id.clone(),
                    kind: track.kind,
                    clip: c.duplicate(),
                    offset_sec: c.start_sec - origin,
                }
            })
            .collect();
        self.clipboard.holds_effects = false;
        true
    }

    /// Copies the selection, then removes it and leaves a gap (closed on the magnetic main track).
    pub fn cut_selected(&mut self) -> bool {
        if !self.copy_selected() {
            return false;
        }
        self.lift_selected();
        true
    }

    /// Pastes the clipboard so its earliest clip starts at `at_sec`. Each clip goes back on the track it came from (a
    /// new track of its kind if that one is gone) and overwrites whatever it lands on; on the magnetic main track
    /// they go in at the nearest cut instead, and the rest shift to match. A single copied clip goes on
    /// `target_track_id` when that track is the same kind. One undo step; the pasted clips become the selection.
    pub fn paste(&mut self, at_sec: f64, target_track_id: Option<&str>) -> Vec<String> {
        if self.clipboard.clips.is_empty() {
            return Vec::new();
        }
        let entries = self.clipboard.clips.clone();
        let target = target_track_id.and_then(|id| self.find_track(id)).map(|t| (t.id.clone(), t.kind));
        let pasted = self.edit(|e| {
            let mut copies: Vec<Clip> = entries.iter().map(|en| fresh_copy(&en.clip)).collect();
            relink(&mut copies);
            let mut placed: Vec<(Clip, String, f64)> = Vec::new();
            for (en, clip) in entries.iter().zip(copies) {
                let track = match &target {
                    Some((id, kind)) if entries.len() == 1 && *kind == en.kind => id.clone(),
                    _ => match e.find_track(&en.track_id) {
                        Some(t) => t.id.clone(),
                        None => e.ensure_track(en.kind),
                    },
                };
                placed.push((clip, track, en.offset_sec));
            }
            let ids: Vec<String> = placed.iter().map(|p| p.0.id.clone()).collect();
            let mut at = at_sec;
            let main = e.main_track_id().map(str::to_string).filter(|m| e.is_magnetic(m));
            if let Some(main) = main.filter(|m| placed.iter().any(|p| &p.1 == m)) {
                // everything shifts so the first clip for the main track starts at the cut nearest where it would have
                let mi = e.doc().tracks.iter().position(|t| t.id == main).unwrap();
                let first = placed.iter().filter(|p| p.1 == main).map(|p| p.2).fold(f64::INFINITY, f64::min);
                let mut order = in_time_order(&e.doc().tracks[mi]);
                let durs: Vec<f64> = order.iter().map(|id| e.find_clip(id).unwrap().dur_sec).collect();
                let index = insert_index(durs.iter().copied(), at + first);
                let cut = if index == 0 { 0.0 } else { e.find_clip(&order[index - 1]).unwrap().end_sec() };
                at = cut - first;
                let mut into_main: Vec<(Clip, f64)> = Vec::new();
                placed.retain(|p| {
                    if p.1 == main {
                        into_main.push((p.0.clone(), p.2));
                        false
                    } else {
                        true
                    }
                });
                into_main.sort_by(|a, b| a.1.total_cmp(&b.1));
                let doc = e.doc_mut();
                for (i, (mut clip, _)) in into_main.into_iter().enumerate() {
                    clip.start_sec = cut;
                    order.insert(index + i, clip.id.clone());
                    insert_clip(&mut doc.tracks[mi], clip);
                }
                pack_track(doc, mi, &order, None, 0.0);
            }
            for (clip, track, offset) in placed {
                let doc = e.doc_mut();
                let ti = doc.tracks.iter().position(|t| t.id == track).unwrap();
                overwrite_insert(doc, ti, clip, (at + offset).max(0.0));
            }
            ids
        });
        self.selection.select_clips(&pasted);
        pasted
    }

    /// Puts a copy of the selection straight after it. Leaves the clipboard alone.
    pub fn duplicate_selected(&mut self) -> Vec<String> {
        let ids = self.selected_with_linked();
        if ids.is_empty() {
            return Vec::new();
        }
        let clips: Vec<&Clip> = ids.iter().filter_map(|id| self.find_clip(id)).collect();
        let end = clips.iter().map(|c| c.end_sec()).fold(f64::MIN, f64::max);
        let saved = std::mem::take(&mut self.clipboard.clips);
        let holds = self.clipboard.holds_effects;
        self.copy_selected();
        let pasted = self.paste(end, None);
        self.clipboard.clips = saved;
        self.clipboard.holds_effects = holds;
        pasted
    }

    /// Copies clips dragged with Ctrl (`ids`, `anchor_id` the one under the pointer) so the anchor starts at
    /// `anchor_start_sec` on `target_track_id`; the others keep their places relative to it, track by track. Linked
    /// sound goes on a free lane of its kind (made if needed). Nothing happens when a copy would land on another
    /// clip. One undo step; the copies become the selection.
    pub fn duplicate_clips_at(
        &mut self,
        ids: &[String],
        anchor_id: &str,
        anchor_start_sec: f64,
        target_track_id: &str,
    ) -> Vec<String> {
        let (Some(anchor), Some(target), Some(anchor_track)) =
            (self.find_clip(anchor_id), self.find_track(target_track_id), self.find_clip_track(anchor_id))
        else {
            return Vec::new();
        };
        if target.kind != anchor_track.kind || !anchor_start_sec.is_finite() {
            return Vec::new();
        }
        let mut sources: Vec<&Clip> = Vec::new();
        for id in ids {
            if let Some(c) = self.find_clip(id)
                && !sources.iter().any(|s| s.id == c.id)
            {
                sources.push(c);
            }
        }
        if !sources.iter().any(|c| c.id == anchor_id) {
            return Vec::new();
        }
        let delta = (self.snap_time(anchor_start_sec.max(0.0)) - anchor.start_sec)
            .max(-sources.iter().map(|c| c.start_sec).fold(f64::INFINITY, f64::min));
        let doc = self.doc();
        let same_kind: Vec<&str> =
            doc.tracks.iter().filter(|t| t.kind == anchor_track.kind).map(|t| t.id.as_str()).collect();
        let pos = |id: &str| same_kind.iter().position(|t| *t == id).unwrap() as i64;
        let track_offset = pos(&target.id) - pos(&anchor_track.id);
        let mut new_tracks: Vec<Track> = Vec::new();
        let mut destinations: HashMap<String, String> = HashMap::new();
        let mut placements: Vec<(&Clip, String, f64)> = Vec::new();
        for source in &sources {
            let original = self.find_clip_track(&source.id).unwrap();
            let start = self.snap_time(source.start_sec + delta);
            let destination = match destinations.get(&original.id) {
                Some(d) => d.clone(),
                None => {
                    let d = if original.kind == anchor_track.kind {
                        let index = pos(&original.id) + track_offset;
                        if index < 0 || index >= same_kind.len() as i64 {
                            return Vec::new();
                        }
                        same_kind[index as usize].to_string()
                    } else {
                        // linked sound needs a free lane when the video is copied above itself
                        let mut candidates: Vec<&Track> =
                            doc.tracks.iter().filter(|t| t.kind == original.kind).collect();
                        candidates.sort_by_key(|t| if t.id == original.id { 0 } else { 1 });
                        let free = candidates.into_iter().find(|t| {
                            sources
                                .iter()
                                .filter(|c| self.find_clip_track_id(&c.id).as_deref() == Some(original.id.as_str()))
                                .all(|c| {
                                    !self.would_overlap(&t.id, self.snap_time(c.start_sec + delta), c.dur_sec, None)
                                })
                                && placements.iter().all(|p| p.1 != t.id)
                        });
                        match free {
                            Some(t) => t.id.clone(),
                            None => {
                                let t = Track::new(original.kind);
                                let id = t.id.clone();
                                new_tracks.push(t);
                                id
                            }
                        }
                    };
                    destinations.insert(original.id.clone(), d.clone());
                    d
                }
            };
            // the magnetic main track makes room instead (below)
            if !self.is_magnetic(&destination)
                && (self.would_overlap(&destination, start, source.dur_sec, None)
                    || placements
                        .iter()
                        .any(|p| p.1 == destination && p.2 < start + source.dur_sec && start < p.2 + p.0.dur_sec))
            {
                return Vec::new();
            }
            placements.push((source, destination, start));
        }
        // copies for the magnetic main track go in at the nearest cut; everything copied with them shifts to match
        let main = self.main_track_id().map(str::to_string);
        if let Some(main) = &main
            && self.is_magnetic(main)
            && placements.iter().any(|p| &p.1 == main)
        {
            let first = placements.iter().filter(|p| &p.1 == main).map(|p| p.2).fold(f64::INFINITY, f64::min);
            let track = self.find_track(main).unwrap();
            let order = in_time_order(track);
            let index = insert_index(order.iter().map(|id| self.find_clip(id).unwrap().dur_sec), first);
            let cut = if index == 0 { 0.0 } else { self.find_clip(&order[index - 1]).unwrap().end_sec() };
            for p in &mut placements {
                p.2 = (p.2 + cut - first).max(0.0);
            }
        }
        let mut copies: Vec<(Clip, String)> = placements
            .iter()
            .map(|(src, track, start)| {
                let mut c = fresh_copy(src);
                c.start_sec = *start;
                (c, track.clone())
            })
            .collect();
        let mut clips: Vec<Clip> = copies.iter().map(|c| c.0.clone()).collect();
        relink(&mut clips);
        for (c, linked) in copies.iter_mut().zip(clips) {
            c.0.link_group_id = linked.link_group_id;
        }
        let ids: Vec<String> = copies.iter().map(|c| c.0.id.clone()).collect();
        self.edit(|e| {
            e.doc_mut().tracks.extend(new_tracks);
            // the main track makes room first, so the copies beside it land in the room it made
            let main_copies: Vec<Clip> = copies
                .iter()
                .filter(|c| main.as_ref().is_some_and(|m| m == &c.1 && e.is_magnetic(m)))
                .map(|c| c.0.clone())
                .collect();
            if !main_copies.is_empty() {
                let mi = e.doc().tracks.iter().position(|t| Some(&t.id) == main.as_ref()).unwrap();
                let mut order = in_time_order(&e.doc().tracks[mi]);
                let first = main_copies.iter().map(|c| c.start_sec).fold(f64::INFINITY, f64::min);
                let durs: Vec<f64> = order.iter().map(|id| e.find_clip(id).unwrap().dur_sec).collect();
                let index = insert_index(durs, first + 1e-6);
                let mut sorted = main_copies;
                sorted.sort_by(|a, b| a.start_sec.total_cmp(&b.start_sec));
                let doc = e.doc_mut();
                for (i, c) in sorted.into_iter().enumerate() {
                    order.insert(index + i, c.id.clone());
                    insert_clip(&mut doc.tracks[mi], c);
                }
                pack_track(doc, mi, &order, None, 0.0);
            }
            for (clip, track) in copies {
                if main.as_ref().is_some_and(|m| m == &track && e.is_magnetic(m)) {
                    continue;
                }
                let doc = e.doc_mut();
                let ti = doc.tracks.iter().position(|t| t.id == track).unwrap();
                let start = clip.start_sec;
                overwrite_insert(doc, ti, clip, start);
            }
        });
        self.selection.select_clips(&ids);
        ids
    }

    /// Copies an effect onto another owner, its range and keyframes shifted by `local_delta` (held inside the
    /// target). Only between owners of the same kind of sound/picture. Returns the copy's id.
    pub fn duplicate_effect_at(
        &mut self,
        source_id: &str,
        effect_id: &str,
        target_id: &str,
        local_delta: f64,
    ) -> Option<String> {
        let doc = self.doc();
        let is_audio = |id: &str| match doc.find_track(id) {
            Some(t) => t.kind == TrackKind::Audio,
            None => doc.find_clip(id).is_some_and(|c| matches!(c.body, ClipBody::Audio(_))),
        };
        let limit = if doc.find_track(target_id).is_some() { f64::INFINITY } else { doc.find_clip(target_id)?.dur_sec };
        let effect = self.find_effect(source_id, effect_id)?;
        if limit <= 0.0 || is_audio(source_id) != is_audio(target_id) || !local_delta.is_finite() {
            return None;
        }
        let mut delta = local_delta;
        let mut copy = effect.duplicate();
        if effect.has_range() {
            let duration = effect.dur_sec.unwrap().min(limit);
            let start = (effect.start_sec.unwrap() + delta).clamp(0.0, limit - duration);
            delta = start - effect.start_sec.unwrap();
            copy.start_sec = Some(start);
            copy.dur_sec = Some(duration);
        } else {
            let times: Vec<f64> = copy.keyframes.values().flatten().map(|k| k.time_sec).collect();
            if let (Some(min), Some(max)) =
                (times.iter().copied().reduce(f64::min), times.iter().copied().reduce(f64::max))
                && max - min <= limit
            {
                delta = delta.clamp(-min, limit - max);
            }
        }
        for keys in copy.keyframes.values_mut() {
            for k in keys.iter_mut() {
                k.time_sec = (k.time_sec + delta).clamp(0.0, limit);
            }
            // one key per time: the last one there
            let mut kept: Vec<fig_timeline::KeyframePoint> = Vec::new();
            for k in keys.drain(..) {
                kept.retain(|p| p.time_sec != k.time_sec);
                kept.push(k);
            }
            kept.sort_by(|a, b| a.time_sec.total_cmp(&b.time_sec));
            *keys = kept;
        }
        let id = copy.id.clone();
        self.add_effect(target_id, copy).then_some(id)
    }
}
