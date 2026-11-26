//! Copy, cut, paste and duplicate for clips. The clipboard lives on the editor, in memory.

use std::collections::{HashMap, HashSet};

use super::editor::overlaps;
use super::effects::CopiedEffect;
use super::magnet::{in_time_order, insert_index, main_track, pack_track};
use super::{TimelineEditor, insert_clip};
use fig_timeline::serde_util::new_id_n;
use fig_timeline::{Clip, ClipBody, EffectInstance, Timeline, Track, TrackKind, split, time};

#[derive(Clone, Debug)]
pub(crate) struct ClipboardEntry {
    track_id: String,
    kind: TrackKind,
    clip: Clip,
    offset_sec: f64,
}

#[derive(Clone, Debug, Default)]
pub(crate) struct Clipboard {
    pub(crate) clips: Vec<ClipboardEntry>,
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

const EPS: f64 = 1e-6;

/// A track's effect regions that lie within `from..to`: the ones that go with the clips there when they're copied.
fn effects_within(track: &Track, from: f64, to: f64) -> Vec<EffectInstance> {
    track
        .effects
        .iter()
        .filter(|fx| fx.has_range())
        .filter(|fx| fx.start_sec.unwrap() >= from - EPS && fx.start_sec.unwrap() + fx.dur_sec.unwrap() <= to + EPS)
        .cloned()
        .collect()
}

/// A copy of a track effect `delta` seconds later, its keyframes with it.
fn shifted_effect(fx: &EffectInstance, delta: f64) -> EffectInstance {
    let mut copy = fx.duplicate();
    copy.start_sec = copy.start_sec.map(|s| s + delta);
    for keys in copy.keyframes.values_mut() {
        for k in keys {
            k.time_sec += delta;
        }
    }
    copy
}

/// Opens `len` seconds at `cut` across the whole timeline: every clip, track effect, keyframe and marker at or after
/// it moves later, so what plays together stays together.
fn open_gap(doc: &mut Timeline, cut: f64, len: f64) {
    let after = |t: f64| t >= cut - EPS;
    for track in &mut doc.tracks {
        for c in track.clips.iter_mut().filter(|c| after(c.start_sec)) {
            c.start_sec += len;
        }
        for fx in &mut track.effects {
            match fx.start_sec {
                Some(s) if fx.has_range() => {
                    if after(s) {
                        fx.start_sec = Some(s + len);
                        for k in fx.keyframes.values_mut().flatten() {
                            k.time_sec += len;
                        }
                    }
                }
                // a whole-track effect keeps its range; its keys after the cut move with the picture
                _ => {
                    for k in fx.keyframes.values_mut().flatten().filter(|k| after(k.time_sec)) {
                        k.time_sec += len;
                    }
                }
            }
        }
        for m in track.markers.iter_mut().filter(|m| after(m.start_sec)) {
            m.start_sec += len;
        }
    }
    for m in doc.markers.iter_mut().filter(|m| after(m.start_sec)) {
        m.start_sec += len;
    }
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

    /// Copies the selected clips (and everything linked to them), with the selected effects and the track effects
    /// over those clips. False when no clip is selected.
    pub fn copy_selected(&mut self) -> bool {
        let ids = self.selected_with_linked();
        if ids.is_empty() {
            return false;
        }
        // an effect on a copied clip goes with the clip already
        let mut effects = self.selected_effects_in_timeline_time(&ids);
        let picked: HashSet<String> = self.selection.selected_effects().into_iter().map(|(_, fx)| fx).collect();
        for id in &ids {
            let (Some(c), Some(track)) = (self.find_clip(id), self.find_clip_track(id)) else { continue };
            for fx in effects_within(track, c.start_sec, c.end_sec()) {
                if !picked.contains(&fx.id) && !effects.iter().any(|p| p.effect.id == fx.id) {
                    effects.push(CopiedEffect { track_id: track.id.clone(), kind: track.kind, effect: fx });
                }
            }
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
        // effects keep their place against the clips: paste puts them back at the same distance from the first clip
        for p in &mut effects {
            p.effect = shifted_effect(&p.effect, -origin);
        }
        self.clipboard.effects = effects;
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
        let effects = self.clipboard.effects.clone();
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
            // where each copied clip's track went, for the effects that came with them
            let lanes: HashMap<String, String> =
                entries.iter().zip(&placed).map(|(en, p)| (en.track_id.clone(), p.1.clone())).collect();
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
            for copied in &effects {
                let track = lanes
                    .get(&copied.track_id)
                    .cloned()
                    .or_else(|| e.find_track(&copied.track_id).map(|t| t.id.clone()))
                    .or_else(|| e.doc().tracks.iter().find(|t| t.kind == copied.kind).map(|t| t.id.clone()));
                if let Some(track) = track {
                    e.add_effect(&track, shifted_effect(&copied.effect, at));
                }
            }
            ids
        });
        self.selection.select_clips(&pasted);
        pasted
    }

    /// Puts a copy of the selection straight after it: its clips, what's linked to them, and the track effects that
    /// sit over them. A copy taking in the magnetic main track is inserted there, and everything later on every track
    /// moves along to make room, so duplicating again and again keeps the rest in step. Anywhere else a copy covers
    /// nothing: it goes after the original where there's room, else on a free track of its kind (made if needed). One
    /// undo step; leaves the clipboard alone; the copies become the selection.
    pub fn duplicate_selected(&mut self) -> Vec<String> {
        let ids = self.selected_with_linked();
        let doc = self.doc();
        let mut sources: Vec<(String, Clip)> = Vec::new();
        for id in &ids {
            if let Some(track) = doc.tracks.iter().find(|t| t.clips.iter().any(|c| &c.id == id)) {
                sources.push((track.id.clone(), track.clips.iter().find(|c| &c.id == id).unwrap().clone()));
            }
        }
        if sources.is_empty() {
            return Vec::new();
        }
        let start = sources.iter().map(|s| s.1.start_sec).fold(f64::INFINITY, f64::min);
        let end = sources.iter().map(|s| s.1.end_sec()).fold(f64::MIN, f64::max);
        let main = main_track(doc).filter(|_| doc.main_track_magnet).map(|i| doc.tracks[i].id.clone());

        // how far each copy lands from its original
        let mut delta: HashMap<String, f64> = HashMap::new();
        let mut gap = None;
        match main.as_ref().filter(|m| sources.iter().any(|s| &s.0 == *m)) {
            Some(main) => {
                // the main track's copies go in back to back after its last selected clip
                let mut on_main: Vec<&Clip> = sources.iter().filter(|s| &s.0 == main).map(|s| &s.1).collect();
                on_main.sort_by(|a, b| a.start_sec.total_cmp(&b.start_sec));
                let cut = on_main.iter().map(|c| c.end_sec()).fold(f64::MIN, f64::max);
                let mut at = cut;
                for c in &on_main {
                    delta.insert(c.id.clone(), at - c.start_sec);
                    at += c.dur_sec;
                }
                gap = Some((cut, at - cut));
                // linked clips follow their main clip; the rest keep their place beside the first copy
                let first = delta[&on_main[0].id];
                for (_, c) in sources.iter().filter(|s| &s.0 != main) {
                    let partner = c.link_group_id.as_ref().and_then(|g| {
                        on_main.iter().find(|m| m.link_group_id.as_ref() == Some(g)).map(|m| delta[&m.id])
                    });
                    delta.insert(c.id.clone(), partner.unwrap_or(first));
                }
            }
            None => {
                for (_, c) in &sources {
                    delta.insert(c.id.clone(), end - start);
                }
            }
        }

        // the copies, by the track they came from, with that track's effects over them
        let mut by_track: Vec<(String, Vec<Clip>, Vec<EffectInstance>)> = Vec::new();
        let mut copies: Vec<Clip> = sources
            .iter()
            .map(|(_, c)| {
                let mut copy = fresh_copy(c);
                copy.start_sec = time::snap_to_frame(c.start_sec + delta[&c.id], doc.rate);
                copy
            })
            .collect();
        relink(&mut copies);
        for ((track_id, _), copy) in sources.iter().zip(copies) {
            match by_track.iter_mut().find(|t| &t.0 == track_id) {
                Some(t) => t.1.push(copy),
                None => by_track.push((track_id.clone(), vec![copy], Vec::new())),
            }
        }
        for (track_id, _, effects) in &mut by_track {
            let originals: Vec<&Clip> = sources.iter().filter(|s| &s.0 == track_id).map(|s| &s.1).collect();
            let from = originals.iter().map(|c| c.start_sec).fold(f64::INFINITY, f64::min);
            let to = originals.iter().map(|c| c.end_sec()).fold(f64::MIN, f64::max);
            let track = doc.find_track(track_id).unwrap();
            for fx in effects_within(track, from, to) {
                // it moves with the clip it starts over
                let s = fx.start_sec.unwrap();
                let over =
                    originals.iter().find(|c| s >= c.start_sec - EPS && s < c.end_sec()).unwrap_or(&originals[0]);
                effects.push(shifted_effect(&fx, delta[&over.id]));
            }
        }

        let ids: Vec<String> = by_track.iter().flat_map(|t| t.1.iter().map(|c| c.id.clone())).collect();
        self.edit(|e| {
            let doc = e.doc_mut();
            if let Some((cut, len)) = gap {
                open_gap(doc, cut, len);
            }
            for (track_id, clips, effects) in by_track {
                let Some(source) = doc.tracks.iter().position(|t| t.id == track_id) else { continue };
                let fits = |t: &Track| clips.iter().all(|c| !overlaps(t, c.start_sec, c.dur_sec, None));
                let ti = if fits(&doc.tracks[source]) {
                    source
                } else {
                    let kind = doc.tracks[source].kind;
                    match doc.tracks.iter().position(|t| t.kind == kind && fits(t)) {
                        Some(i) => i,
                        None => {
                            // a new lane beside the original: above it for pictures, below it for sound
                            let at = if kind == TrackKind::Video { source } else { source + 1 };
                            doc.tracks.insert(at, Track::new(kind));
                            at
                        }
                    }
                };
                for c in clips {
                    insert_clip(&mut doc.tracks[ti], c);
                }
                doc.tracks[ti].effects.extend(effects);
            }
            e.doc_refresh_track_indices();
        });
        self.selection.select_clips(&ids);
        ids
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
        // new lanes, each with the track it goes beside (above a video track, below a sound one), if any
        let mut new_tracks: Vec<(Track, Option<String>)> = Vec::new();
        let mut destinations: HashMap<String, String> = HashMap::new();
        let mut placements: Vec<(&Clip, String, f64)> = Vec::new();
        let mut crowded: indexmap::IndexSet<String> = indexmap::IndexSet::new();
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
                                new_tracks.push((t, None));
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
                crowded.insert(destination.clone());
            }
            placements.push((source, destination, start));
        }
        // a copy dropped onto other clips covers nothing: what was headed for that lane gets a new one beside it
        for lane in crowded {
            let kind = doc.find_track(&lane).map_or(anchor_track.kind, |t| t.kind);
            let t = Track::new(kind);
            let id = t.id.clone();
            new_tracks.push((t, Some(lane.clone())));
            for p in placements.iter_mut().filter(|p| p.1 == lane) {
                p.1 = id.clone();
            }
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
        // the track effects over the copied clips go with them, each with the clip it starts over
        let mut effects: Vec<(EffectInstance, String)> = Vec::new();
        let mut origins: Vec<&str> = Vec::new();
        for p in &placements {
            let origin = self.find_clip_track(&p.0.id).unwrap();
            if origins.contains(&origin.id.as_str()) {
                continue;
            }
            origins.push(&origin.id);
            let here: Vec<&(&Clip, String, f64)> = placements
                .iter()
                .filter(|q| self.find_clip_track_id(&q.0.id).as_deref() == Some(origin.id.as_str()))
                .collect();
            let from = here.iter().map(|q| q.0.start_sec).fold(f64::INFINITY, f64::min);
            let to = here.iter().map(|q| q.0.end_sec()).fold(f64::MIN, f64::max);
            for fx in effects_within(origin, from, to) {
                let s = fx.start_sec.unwrap();
                let over = here.iter().find(|q| s >= q.0.start_sec - EPS && s < q.0.end_sec()).unwrap_or(&here[0]);
                effects.push((shifted_effect(&fx, over.2 - over.0.start_sec), over.1.clone()));
            }
        }
        let ids: Vec<String> = copies.iter().map(|c| c.0.id.clone()).collect();
        self.edit(|e| {
            for (track, beside) in new_tracks {
                let doc = e.doc_mut();
                match beside.and_then(|b| doc.tracks.iter().position(|t| t.id == b)) {
                    Some(i) => {
                        let at = if track.kind == TrackKind::Video { i } else { i + 1 };
                        doc.tracks.insert(at, track);
                    }
                    None => doc.tracks.push(track),
                }
            }
            e.doc_refresh_track_indices();
            for (fx, track) in effects {
                if let Some(t) = e.doc_mut().tracks.iter_mut().find(|t| t.id == track) {
                    t.effects.push(fx);
                }
            }
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

#[cfg(test)]
mod tests {
    use crate::TimelineEditor;
    use fig_timeline::{Clip, EffectInstance, FrameRate, Timeline, TrackKind, VideoBody};

    fn video(id: &str, start: f64, dur: f64) -> Clip {
        Clip::new(VideoBody { source_id: "a".into(), src_out_sec: dur, ..Default::default() })
            .with_id(id)
            .at(start, dur)
    }

    fn region(start: f64, dur: f64) -> EffectInstance {
        EffectInstance { type_id: "transform".into(), start_sec: Some(start), dur_sec: Some(dur), ..Default::default() }
    }

    /// An overlay track above a magnetic main track holding a, b, c (4 s each); an overlay over c.
    fn reel() -> (TimelineEditor, String, String) {
        let mut ed = TimelineEditor::new(Timeline { rate: FrameRate::common(30.0), ..Default::default() });
        let main = ed.add_track(TrackKind::Video);
        let over = ed.add_track(TrackKind::Video);
        for (i, id) in ["a", "b", "c"].into_iter().enumerate() {
            ed.add_clip(&main, video(id, i as f64 * 4.0, 4.0));
        }
        ed.add_clip(&over, video("title", 8.5, 2.0));
        (ed, main, over)
    }

    fn starts(ed: &TimelineEditor, track: &str) -> Vec<f64> {
        ed.find_track(track).unwrap().clips.iter().map(|c| c.start_sec).collect()
    }

    #[test]
    fn duplicating_on_the_main_track_moves_everything_after_it_and_takes_its_track_effects() {
        let (mut ed, main, over) = reel();
        ed.edit(|e| {
            let track = e.doc_mut().find_track_mut(&main).unwrap();
            track.effects.push(region(4.5, 2.0)); // over b
            track.effects.push(region(9.0, 1.0)); // over c
        });
        ed.selection.select_clips(["b"]);
        let copies = ed.duplicate_selected();
        assert_eq!(copies.len(), 1);
        assert_eq!(starts(&ed, &main), vec![0.0, 4.0, 8.0, 12.0]);
        assert_eq!(ed.find_clip(&copies[0]).unwrap().start_sec, 8.0);
        // the title over c moved with c, and so did c's effect; b's effect was copied onto the copy
        assert_eq!(starts(&ed, &over), vec![12.5]);
        let mut regions: Vec<f64> =
            ed.find_track(&main).unwrap().effects.iter().map(|fx| fx.start_sec.unwrap()).collect();
        regions.sort_by(f64::total_cmp);
        assert_eq!(regions, vec![4.5, 8.5, 13.0]);
        ed.undo();
        assert_eq!(starts(&ed, &main), vec![0.0, 4.0, 8.0]);
        assert_eq!(starts(&ed, &over), vec![8.5]);
        assert_eq!(ed.find_track(&main).unwrap().effects.len(), 2);
    }

    #[test]
    fn chained_duplicates_keep_later_clips_in_step() {
        let (mut ed, main, over) = reel();
        ed.selection.select_clips(["b"]);
        for _ in 0..5 {
            ed.duplicate_selected();
        }
        // b and five copies back to back, then c with its title still 0.5 s into it
        assert_eq!(starts(&ed, &main), vec![0.0, 4.0, 8.0, 12.0, 16.0, 20.0, 24.0, 28.0]);
        let c = ed.find_clip("c").unwrap().start_sec;
        assert_eq!((c, starts(&ed, &over)[0]), (28.0, 28.5));
    }

    #[test]
    fn an_overlay_copy_covers_nothing_and_takes_a_new_lane_when_there_is_no_room() {
        let (mut ed, _, over) = reel();
        ed.add_clip(&over, video("next", 10.5, 3.0));
        ed.selection.select_clips(["title"]);
        let copies = ed.duplicate_selected();
        // "next" sits where the copy would go, so the copy takes a lane above, at the same time
        assert_eq!(starts(&ed, &over), vec![8.5, 10.5]);
        let lane = ed.find_clip_track_id(&copies[0]).unwrap();
        assert_ne!(lane, over);
        let lanes: Vec<&str> = ed.doc().tracks.iter().map(|t| t.id.as_str()).collect();
        assert_eq!(lanes.iter().position(|t| *t == lane).unwrap() + 1, lanes.iter().position(|t| *t == over).unwrap());
        assert_eq!(ed.find_clip(&copies[0]).unwrap().start_sec, 10.5);
        // with room after it, a copy goes straight after the original on its own track
        ed.selection.select_clips(["next"]);
        let copies = ed.duplicate_selected();
        assert_eq!(ed.find_clip_track_id(&copies[0]).unwrap(), over);
        assert_eq!(starts(&ed, &over), vec![8.5, 10.5, 13.5]);
    }

    #[test]
    fn copying_clips_with_an_effect_selected_pastes_both() {
        let (mut ed, _, over) = reel();
        let fx = region(9.0, 1.0);
        let fx_id = fx.id.clone();
        ed.edit(|e| e.doc_mut().find_track_mut(&over).unwrap().effects.push(fx));
        ed.selection.select_clips(["title"]);
        ed.selection.select_effects(vec![(over.clone(), fx_id)], true);
        assert!(ed.copy_selected());
        let pasted = ed.paste(20.0, None);
        assert_eq!(pasted.len(), 1);
        assert_eq!(ed.find_clip(&pasted[0]).unwrap().start_sec, 20.0);
        // the effect keeps its place 0.5 s into the title, and was copied once
        let regions: Vec<f64> = ed.find_track(&over).unwrap().effects.iter().map(|fx| fx.start_sec.unwrap()).collect();
        assert_eq!(regions, vec![9.0, 20.5]);
        ed.undo();
        assert_eq!(ed.find_track(&over).unwrap().effects.len(), 1);
    }

    #[test]
    fn a_copy_dragged_onto_other_clips_takes_a_new_lane_with_its_effects() {
        let (mut ed, main, over) = reel();
        ed.edit(|e| e.doc_mut().find_track_mut(&over).unwrap().effects.push(region(9.0, 1.0)));
        ed.add_clip(&over, video("next", 1.0, 2.0));
        let copies = ed.duplicate_clips_at(&["title".into()], "title", 1.5, &over);
        assert_eq!(copies.len(), 1);
        let lane = ed.find_clip_track_id(&copies[0]).unwrap();
        assert!(lane != over && lane != main);
        assert_eq!(starts(&ed, &over), vec![1.0, 8.5]);
        let lane_fx: Vec<f64> = ed.find_track(&lane).unwrap().effects.iter().map(|fx| fx.start_sec.unwrap()).collect();
        assert_eq!(lane_fx, vec![2.0]);
        // above the lane it was dropped on
        let order: Vec<&str> = ed.doc().tracks.iter().map(|t| t.id.as_str()).collect();
        assert_eq!(order.iter().position(|t| *t == lane).unwrap() + 1, order.iter().position(|t| *t == over).unwrap());
    }
}
