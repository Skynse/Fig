//! The timeline editor: owns the timeline, its undo history and the selection, and makes every edit.
//!
//! Every edit runs through [`TimelineEditor::edit`], which keeps the timeline as it was before and records it as one
//! undo step if anything changed. Edits nest: an edit made inside another is part of the outer one's step. A drag
//! that changes clips as the pointer moves runs between [`TimelineEditor::begin_live`] and
//! [`TimelineEditor::commit_live`] and becomes one step when it ends.

use std::collections::HashSet;

use super::history::{Coalesce, History};
use super::{SelectionState, SnapSettings};
use fig_timeline::serde_util::new_id_n;
use fig_timeline::{
    AudioBody, Clip, ClipKind, FrameRate, FrameShape, ImageBody, SelectionGroup, Timeline, Track, TrackKind, VideoBody,
    clip_time, fade, fade::FadeOf, split, time,
};
use fig_timeline::{MediaAsset, MediaKind};

pub const MIN_SPEED: f64 = clip_time::MIN_SPEED;
pub const MAX_SPEED: f64 = clip_time::MAX_SPEED;

pub struct TimelineEditor {
    doc: Timeline,
    history: History,
    pub selection: SelectionState,
    pub snap: SnapSettings,
    /// Why the last transition could not be put where it was asked for, for the status bar.
    pub transition_validation_message: Option<String>,
    pub(crate) beat_preview: Option<super::markers::BeatPreview>,
    pub(crate) clipboard: super::clipboard::Clipboard,
    revision: u64,
    edit_depth: usize,
    live: Option<Timeline>,
}

impl TimelineEditor {
    /// An editor for `doc`; `undo_steps` edits can be undone before the oldest are forgotten.
    pub fn new(doc: Timeline) -> TimelineEditor {
        Self::with_undo_steps(doc, 100)
    }

    pub fn with_undo_steps(doc: Timeline, undo_steps: usize) -> TimelineEditor {
        let mut editor = TimelineEditor {
            doc,
            history: History::new(undo_steps),
            selection: SelectionState::new(),
            snap: SnapSettings::default(),
            transition_validation_message: None,
            beat_preview: None,
            clipboard: Default::default(),
            revision: 0,
            edit_depth: 0,
            live: None,
        };
        // the index is saved and can be stale; the C# build saved "V1"/"A1" labels as names, which are worked out
        for track in &mut editor.doc.tracks {
            if track.name.as_deref().is_some_and(is_auto_label) {
                track.name = None;
            }
        }
        editor.doc_refresh_track_indices();
        editor
    }

    /// The timeline being edited.
    pub fn doc(&self) -> &Timeline {
        &self.doc
    }

    /// Gives the timeline back (when the project saves or closes).
    pub fn into_doc(self) -> Timeline {
        self.doc
    }

    /// Bumped after every change to the timeline, so a view knows when to redraw and playback when to re-read.
    pub fn revision(&self) -> u64 {
        self.revision
    }

    pub fn can_undo(&self) -> bool {
        self.history.can_undo()
    }

    pub fn can_redo(&self) -> bool {
        self.history.can_redo()
    }

    /// Ends the current coalescing run, so the next slider or gizmo edit is its own undo step.
    pub fn break_coalescing(&mut self) {
        self.history.break_coalescing();
    }

    // ---- recording edits --------------------------------------------------------------------

    /// Runs an edit as one undo step (or as part of the step of an edit it runs inside). Nothing is recorded when
    /// it changes nothing.
    pub fn edit<R>(&mut self, f: impl FnOnce(&mut Self) -> R) -> R {
        if self.edit_depth > 0 || self.live.is_some() {
            self.edit_depth += 1;
            let r = f(self);
            self.edit_depth -= 1;
            return r;
        }
        let before = self.doc.clone();
        self.edit_depth = 1;
        let r = f(self);
        self.edit_depth = 0;
        if self.history.record(before, &self.doc, None) {
            self.changed();
        }
        r
    }

    /// An edit that joins the step before it when that step had the same `key` (a slider being dragged), else
    /// starts a step of its own with that key.
    pub fn edit_coalescing<R>(&mut self, key: String, mode: Coalesce, f: impl FnOnce(&mut Self) -> R) -> R {
        if self.edit_depth > 0 || self.live.is_some() {
            return self.edit(f);
        }
        if let Some(base) = self.history.coalescing_base(&key) {
            if mode == Coalesce::Restart {
                self.doc = base.clone();
            }
            self.edit_depth = 1;
            let r = f(self);
            self.edit_depth = 0;
            self.history.joined();
            self.changed();
            return r;
        }
        let before = self.doc.clone();
        self.edit_depth = 1;
        let r = f(self);
        self.edit_depth = 0;
        if self.history.record(before, &self.doc, Some(key)) {
            self.changed();
        }
        r
    }

    /// Starts a drag that changes the timeline as the pointer moves ([`live_doc`](Self::live_doc)). Nothing is
    /// recorded until [`commit_live`](Self::commit_live).
    pub fn begin_live(&mut self) {
        if self.live.is_none() {
            self.live = Some(self.doc.clone());
        }
    }

    /// The timeline, for a drag in progress to change directly.
    pub fn live_doc(&mut self) -> &mut Timeline {
        debug_assert!(self.live.is_some(), "live_doc outside a live edit");
        self.revision += 1;
        &mut self.doc
    }

    pub fn is_live(&self) -> bool {
        self.live.is_some()
    }

    /// Ends a drag: what it changed becomes one undo step. Returns false when nothing changed.
    pub fn commit_live(&mut self) -> bool {
        let Some(before) = self.live.take() else {
            return false;
        };
        self.doc_refresh_track_indices();
        let recorded = self.history.record(before, &self.doc, None);
        self.changed();
        recorded
    }

    /// Ends a drag by putting everything back as it was.
    pub fn cancel_live(&mut self) {
        if let Some(before) = self.live.take() {
            self.doc = before;
            self.changed();
        }
    }

    pub fn undo(&mut self) -> bool {
        self.live = None;
        let current = std::mem::take(&mut self.doc);
        match self.history.undo(current) {
            Ok(previous) => {
                self.doc = previous;
                self.after_history();
                true
            }
            Err(current) => {
                self.doc = current;
                false
            }
        }
    }

    pub fn redo(&mut self) -> bool {
        self.live = None;
        let current = std::mem::take(&mut self.doc);
        match self.history.redo(current) {
            Ok(next) => {
                self.doc = next;
                self.after_history();
                true
            }
            Err(current) => {
                self.doc = current;
                false
            }
        }
    }

    /// After undo or redo: the selection keeps only what still exists.
    fn after_history(&mut self) {
        let doc = &self.doc;
        self.selection.retain(|key| match key.group {
            SelectionGroup::Clip => doc.find_clip(&key.id).is_some(),
            SelectionGroup::Track => doc.find_track(&key.id).is_some(),
            SelectionGroup::Marker => super::markers::find_marker(doc, &key.id).is_some(),
            SelectionGroup::Effect => key.owner_id.as_deref().is_some_and(|o| {
                super::effects::owner_effects(doc, o).is_some_and(|fx| fx.iter().any(|e| e.id == key.id))
            }),
            SelectionGroup::Transition => super::transitions::find_cut(doc, &key.id).is_some(),
            SelectionGroup::Media => true,
        });
        self.changed();
    }

    /// Something the timeline shows changed outside an edit (a filmstrip finished, media was relinked).
    pub fn notify_media_changed(&mut self) {
        self.changed();
    }

    fn changed(&mut self) {
        self.revision += 1;
        // a preview for a clip that was deleted (or undone away) has nothing to attach to
        if let Some(preview) = &self.beat_preview
            && self.doc.find_clip(&preview.clip_id).is_none()
        {
            self.beat_preview = None;
        }
    }

    /// The timeline, for an edit in progress (inside [`edit`](Self::edit)) to change.
    pub(crate) fn doc_mut(&mut self) -> &mut Timeline {
        debug_assert!(self.edit_depth > 0 || self.live.is_some(), "doc_mut outside an edit");
        &mut self.doc
    }

    // ---- finding things ---------------------------------------------------------------------

    pub fn find_clip(&self, id: &str) -> Option<&Clip> {
        self.doc.find_clip(id)
    }

    pub fn find_track(&self, id: &str) -> Option<&Track> {
        self.doc.find_track(id)
    }

    /// The track holding the clip.
    pub fn find_clip_track(&self, clip_id: &str) -> Option<&Track> {
        self.doc.locate_clip(clip_id).map(|(t, _)| &self.doc.tracks[t])
    }

    pub fn find_clip_track_id(&self, clip_id: &str) -> Option<String> {
        self.find_clip_track(clip_id).map(|t| t.id.clone())
    }

    /// The clip on the track under `pos_sec`.
    pub fn find_clip_at(&self, track_id: &str, pos_sec: f64) -> Option<&Clip> {
        self.find_track(track_id)?.clips.iter().find(|c| pos_sec >= c.start_sec && pos_sec < c.end_sec())
    }

    /// The ids of every clip sharing the given clip's link group (itself included).
    pub fn link_group(&self, clip_id: &str) -> Vec<String> {
        link_group(&self.doc, clip_id)
    }

    /// One clip id per selected link group (so a selected video+audio pair is acted on once, not twice).
    pub fn selected_group_seeds(&self) -> Vec<String> {
        let mut seen = HashSet::new();
        let mut seeds = Vec::new();
        for id in self.selection.selected_clip_ids() {
            let Some(clip) = self.find_clip(&id) else { continue };
            let key = clip.link_group_id.clone().filter(|g| !g.is_empty()).unwrap_or_else(|| clip.id.clone());
            if seen.insert(key) {
                seeds.push(id);
            }
        }
        seeds
    }

    pub fn snap_time(&self, sec: f64) -> f64 {
        time::snap_to_frame(sec, self.doc.rate)
    }

    // ---- clips --------------------------------------------------------------------------------

    /// Puts a clip on a track (kept in time order).
    pub fn add_clip(&mut self, track_id: &str, clip: Clip) -> bool {
        self.edit(|e| match e.doc_mut().find_track_mut(track_id) {
            Some(track) => {
                insert_clip(track, clip);
                true
            }
            None => false,
        })
    }

    /// Puts a clip on the track it is on, or the first of its kind (made if there is none).
    pub fn add_clip_anywhere(&mut self, clip: Clip) {
        self.edit(|e| {
            let kind = if clip.kind() == ClipKind::Audio { TrackKind::Audio } else { TrackKind::Video };
            let track = e.ensure_track(kind);
            let track = e.doc_mut().find_track_mut(&track).unwrap();
            insert_clip(track, clip);
        });
    }

    /// A new clip of a library file: a video's sound is part of its clip until it is detached.
    pub fn clip_from_asset(asset: &MediaAsset) -> Clip {
        let mut clip = match asset.kind {
            MediaKind::Audio => Clip::new(AudioBody { source_id: asset.id.clone(), ..Default::default() }),
            // a still lasts the default length; an animated one (a GIF) at least one whole loop
            MediaKind::Image => Clip::new(ImageBody { source_id: asset.id.clone(), ..Default::default() }),
            MediaKind::Video => {
                Clip::new(VideoBody { source_id: asset.id.clone(), plays_audio: asset.has_audio, ..Default::default() })
            }
        };
        clip.dur_sec = match asset.kind {
            MediaKind::Image => ImageBody::DEFAULT_DURATION_SEC.max(asset.duration_sec),
            _ => asset.duration_sec,
        };
        clip.source_rate = asset.source_rate;
        clip.set_source_range(0.0, asset.duration_sec);
        clip
    }

    /// Readies a new clip of `asset` for the timeline. An empty timeline whose rate the user hasn't chosen takes the
    /// media's frame rate, if it reports a sensible one.
    fn prepare_placement(&mut self, asset: &MediaAsset) {
        if !self.doc.rate_chosen
            && self.doc.clips().next().is_none()
            && let Some(rate) = FrameRate::plausible(asset.source_rate)
        {
            self.doc_mut().rate = rate;
        }
    }

    /// Adds a library file to the timeline at `start_sec`, on the target track when it is of the file's kind (none
    /// when it would overlap a clip there), else on a free track of its kind. Returns the new clip's id.
    pub fn add_media(&mut self, asset: &MediaAsset, target_track_id: &str, start_sec: f64) -> Option<String> {
        let target_kind = self.find_track(target_track_id)?.kind;
        self.edit(|e| {
            e.prepare_placement(asset);
            let mut clip = Self::clip_from_asset(asset);
            clip.start_sec = e.snap_time(start_sec);
            let kind = if asset.kind == MediaKind::Audio { TrackKind::Audio } else { TrackKind::Video };
            let track = if target_kind == kind {
                if e.would_overlap(target_track_id, clip.start_sec, clip.dur_sec, None) {
                    return None;
                }
                target_track_id.to_string()
            } else {
                e.find_free_track(kind, &clip)
            };
            let id = clip.id.clone();
            insert_clip(e.doc_mut().find_track_mut(&track).unwrap(), clip);
            Some(id)
        })
    }

    /// Adds a library file on a brand-new track of its kind (a drop into empty space). Returns the new clip's id.
    pub fn add_media_new_track(&mut self, asset: &MediaAsset, start_sec: f64) -> String {
        self.edit(|e| {
            e.prepare_placement(asset);
            let mut clip = Self::clip_from_asset(asset);
            clip.start_sec = e.snap_time(start_sec);
            let kind = if asset.kind == MediaKind::Audio { TrackKind::Audio } else { TrackKind::Video };
            let track = e.add_track(kind);
            let id = clip.id.clone();
            insert_clip(e.doc_mut().find_track_mut(&track).unwrap(), clip);
            id
        })
    }

    /// A track of `kind` where `clip` fits without overlap (made if there is none).
    fn find_free_track(&mut self, kind: TrackKind, clip: &Clip) -> String {
        let free = self
            .doc
            .tracks
            .iter()
            .find(|t| t.kind == kind && !overlaps(t, clip.start_sec, clip.dur_sec, Some(&clip.id)))
            .map(|t| t.id.clone());
        free.unwrap_or_else(|| self.add_track(kind))
    }

    /// True when a clip of `dur_sec` at `start_sec` on the track would overlap another clip.
    pub fn would_overlap(&self, track_id: &str, start_sec: f64, dur_sec: f64, exclude_clip_id: Option<&str>) -> bool {
        self.find_track(track_id).is_some_and(|t| overlaps(t, start_sec, dur_sec, exclude_clip_id))
    }

    /// True when moving a link group to `start_sec` on the track would overlap a clip outside the group.
    pub fn would_overlap_group(
        &self,
        track_id: &str,
        group_ids: &HashSet<String>,
        start_sec: f64,
        dur_sec: f64,
    ) -> bool {
        let end = start_sec + dur_sec;
        self.find_track(track_id).is_some_and(|t| {
            t.clips.iter().any(|c| !group_ids.contains(&c.id) && c.start_sec < end && c.end_sec() > start_sec)
        })
    }

    /// True when moving the clips to the given starts would make any of them overlap a clip that is not moving with
    /// them, on the track each sits on.
    pub fn would_overlap_move(&self, new_starts: &indexmap::IndexMap<String, f64>) -> bool {
        new_starts.iter().any(|(id, &start)| {
            let (Some(clip), Some(track)) = (self.find_clip(id), self.find_clip_track(id)) else {
                return false;
            };
            let end = start + clip.dur_sec;
            track
                .clips
                .iter()
                .any(|o| !new_starts.contains_key(&o.id) && o.start_sec < end - 1e-9 && o.end_sec() > start + 1e-9)
        })
    }

    /// Cuts the clip, and the clips linked to it that span the time, at `at_sec`. The right halves of a linked
    /// group are linked to each other, not to the left halves. Returns the left and right halves' ids, in pairs.
    pub fn cut(&mut self, clip_id: &str, at_sec: f64) -> Vec<String> {
        let group: Vec<String> = self
            .link_group(clip_id)
            .into_iter()
            .filter(|id| self.find_clip(id).is_some_and(|c| c.start_sec < at_sec && at_sec < c.end_sec()))
            .collect();
        if group.is_empty() {
            return Vec::new();
        }
        let second_group = self.find_clip(&group[0]).and_then(|c| c.link_group_id.as_ref()).map(|_| new_id_n());
        self.edit(|e| group.iter().flat_map(|id| e.cut_one_into(id, at_sec, second_group.clone())).collect())
    }

    /// Cuts one clip in two at `at_sec`, the right half linked to `second_group`. Returns both ids (none when the
    /// time is not inside the clip).
    pub(crate) fn cut_one_into(&mut self, clip_id: &str, at_sec: f64, second_group: Option<String>) -> Vec<String> {
        let Some((t, c)) = self.doc.locate_clip(clip_id) else {
            return Vec::new();
        };
        let whole = self.doc.tracks[t].clips[c].clone();
        let offset = at_sec - whole.start_sec;
        if offset <= 0.0 || offset >= whole.dur_sec {
            return Vec::new();
        }
        let mut left = split::before(&whole, offset);
        left.id = whole.id.clone();
        left.transition_out = None;
        let mut right = split::after(&whole, offset);
        right.transition_in = None;
        right.link_group_id = second_group;
        // markers are shared out, not copied, and keep their ids
        left.markers = whole.markers.iter().filter(|m| m.start_sec < offset).cloned().collect();
        right.markers = whole
            .markers
            .iter()
            .filter(|m| m.start_sec >= offset)
            .map(|m| {
                let mut m = m.clone();
                m.start_sec -= offset;
                m
            })
            .collect();
        let ids = vec![left.id.clone(), right.id.clone()];
        let track = &mut self.doc_mut().tracks[t];
        track.clips[c] = left;
        insert_clip(track, right);
        ids
    }

    /// Splits the selected clips (and their link groups) at the playhead, then selects the right halves so
    /// successive beat splits need no re-clicking. Unselected clips that overlap the same time are left alone.
    pub fn split_at_playhead(&mut self, pos_sec: f64) -> Vec<String> {
        let snapped = self.snap_time(pos_sec);
        let seeds = self.selected_group_seeds();
        self.split_groups(&seeds, snapped)
    }

    /// Splits the clip under the playhead on `track_id` (and its link group) when nothing is selected; with a
    /// selection, the selection is split instead.
    pub fn split_at_playhead_on(&mut self, track_id: &str, pos_sec: f64) -> Vec<String> {
        let snapped = self.snap_time(pos_sec);
        if self.selection.clip_count() == 0 {
            let Some(hit) = self.find_clip_at(track_id, snapped).map(|c| c.id.clone()) else {
                return Vec::new();
            };
            let group = self.link_group(&hit);
            self.selection.select_clips(&group);
        }
        self.split_at_playhead(snapped)
    }

    pub(crate) fn split_groups(&mut self, seeds: &[String], at: f64) -> Vec<String> {
        let mut seen = HashSet::new();
        let mut targets: Vec<(String, Option<String>)> = Vec::new();
        for seed in seeds {
            let spanning: Vec<&Clip> = self
                .link_group(seed)
                .iter()
                .filter_map(|id| self.find_clip(id))
                .filter(|c| c.start_sec < at && at < c.end_sec())
                .collect();
            let Some(first) = spanning.first() else { continue };
            let linked = first.link_group_id.clone().filter(|g| !g.is_empty());
            if !seen.insert(linked.clone().unwrap_or_else(|| first.id.clone())) {
                continue;
            }
            let second = linked.map(|_| new_id_n());
            targets.extend(spanning.iter().map(|c| (c.id.clone(), second.clone())));
        }
        if targets.is_empty() {
            return Vec::new();
        }
        let produced: Vec<String> =
            self.edit(|e| targets.iter().flat_map(|(id, group)| e.cut_one_into(id, at, group.clone())).collect());
        let mut right = Vec::new();
        for pair in produced.chunks(2) {
            if let [_, r] = pair {
                right.extend(self.link_group(r));
            }
        }
        if !right.is_empty() {
            self.selection.select_clips(&right);
        }
        produced
    }

    /// Trims a clip to play `new_in`..`new_out` of its source (a clip without one: to that length).
    pub fn trim(&mut self, clip_id: &str, new_in: f64, new_out: f64) -> bool {
        if new_out <= new_in {
            return false;
        }
        self.edit(|e| {
            let Some(clip) = e.doc_mut().find_clip_mut(clip_id) else {
                return false;
            };
            if clip.has_source_range() {
                clip.set_source_range(new_in, new_out);
                clip.dur_sec = clip_time::duration_for(clip, new_out - new_in);
            } else {
                clip.dur_sec = new_out - new_in;
            }
            true
        })
    }

    /// Moves a clip, and the clips linked to it by the same amount, so it starts at `new_start_sec`.
    pub fn move_clip(&mut self, clip_id: &str, new_start_sec: f64) -> bool {
        let Some(old) = self.find_clip(clip_id).map(|c| c.start_sec) else {
            return false;
        };
        let delta = new_start_sec - old;
        let group = self.link_group(clip_id);
        self.edit(|e| {
            for id in &group {
                if let Some(c) = e.doc_mut().find_clip_mut(id) {
                    c.start_sec += delta;
                }
            }
        });
        true
    }

    /// Deletes a clip's link group and closes the gap on the tracks it was on (only those: clips on other tracks
    /// never move).
    pub fn ripple_delete(&mut self, clip_id: &str) -> bool {
        let group = self.link_group(clip_id);
        if group.is_empty() {
            return false;
        }
        self.edit(|e| e.ripple_delete_group(&group));
        true
    }

    fn ripple_delete_group(&mut self, group: &[String]) {
        let clips: Vec<&Clip> = group.iter().filter_map(|id| self.find_clip(id)).collect();
        let removed_dur = clips.iter().map(|c| c.dur_sec).fold(0.0, f64::max);
        let ripple_from = clips.iter().map(|c| c.start_sec).fold(f64::MAX, f64::min);
        let tracks: HashSet<String> = group.iter().filter_map(|id| self.find_clip_track_id(id)).collect();
        let doc = self.doc_mut();
        for track in doc.tracks.iter_mut().filter(|t| tracks.contains(&t.id)) {
            track.clips.retain(|c| !group.contains(&c.id));
            for c in track.clips.iter_mut().filter(|c| c.start_sec >= ripple_from) {
                c.start_sec = (c.start_sec - removed_dur).max(0.0);
            }
        }
    }

    /// Ripple-deletes every distinct selected link group. Unrelated clips that merely share a timeline position are
    /// not touched.
    pub fn ripple_delete_selected(&mut self) {
        let seeds = self.selected_group_seeds();
        if seeds.is_empty() {
            return;
        }
        self.edit(|e| {
            for seed in &seeds {
                let group = e.link_group(seed);
                e.ripple_delete_group(&group);
            }
        });
        self.selection.clear();
    }

    /// Takes a clip's link group off the timeline, leaving a gap (so a deleted video never leaves its sound behind).
    pub fn lift(&mut self, clip_id: &str) -> bool {
        let group = self.link_group(clip_id);
        if group.is_empty() {
            return false;
        }
        self.edit(|e| {
            for track in &mut e.doc_mut().tracks {
                track.clips.retain(|c| !group.contains(&c.id));
            }
        });
        true
    }

    /// Lifts every distinct selected link group, leaving gaps (except on the magnetic main track). Unselected clips
    /// are never touched.
    pub fn lift_selected(&mut self) {
        let seeds = self.selected_group_seeds();
        if seeds.is_empty() {
            return;
        }
        self.keep_main_track_closed(|e| {
            for seed in &seeds {
                e.lift(seed);
            }
        });
        self.selection.clear();
    }

    /// Unlinks the clips and everything linked to them (they then move and cut separately).
    pub fn unlink_clips<S: AsRef<str>>(&mut self, clip_ids: impl IntoIterator<Item = S>) {
        let members: Vec<String> = clip_ids.into_iter().flat_map(|id| self.link_group(id.as_ref())).collect();
        self.edit(|e| {
            for id in &members {
                if let Some(c) = e.doc_mut().find_clip_mut(id) {
                    c.link_group_id = None;
                }
            }
        });
    }

    /// Links two or more clips so they move, trim and cut together.
    pub fn link_clips<S: AsRef<str>>(&mut self, clip_ids: impl IntoIterator<Item = S>) {
        let mut members: Vec<String> = Vec::new();
        for id in clip_ids {
            for m in self.link_group(id.as_ref()) {
                if !members.contains(&m) {
                    members.push(m);
                }
            }
        }
        if members.len() < 2 {
            return;
        }
        let group = new_id_n();
        self.edit(|e| {
            for id in &members {
                if let Some(c) = e.doc_mut().find_clip_mut(id) {
                    c.link_group_id = Some(group.clone());
                }
            }
        });
    }

    /// Turns every distinct selected link group on or off.
    pub fn toggle_enabled_selected(&mut self) {
        let seeds = self.selected_group_seeds();
        let ids: HashSet<String> = seeds.iter().flat_map(|s| self.link_group(s)).collect();
        self.edit(|e| {
            for c in e.doc_mut().clips_mut().filter(|c| ids.contains(&c.id)) {
                c.enabled = !c.enabled;
            }
        });
    }

    /// Sets the fade-in of the clip's picture, or of a video clip's own sound (and its link group's). Slider drags
    /// join into one step.
    pub fn set_fade_in(&mut self, clip_id: &str, fade_in_sec: f64, of: FadeOf) {
        self.set_fade(clip_id, fade_in_sec, of, true);
    }

    pub fn set_fade_out(&mut self, clip_id: &str, fade_out_sec: f64, of: FadeOf) {
        self.set_fade(clip_id, fade_out_sec, of, false);
    }

    fn set_fade(&mut self, clip_id: &str, value: f64, of: FadeOf, fade_in: bool) {
        let value = value.max(0.0);
        let Some(clip) = self.find_clip(clip_id) else { return };
        let (cur_in, cur_out) = fade::get(clip, of);
        let (current, other) = if fade_in { (cur_in, cur_out) } else { (cur_out, cur_in) };
        let clamped = value.min((clip.dur_sec - other.max(0.0)).max(0.0));
        if (current - clamped).abs() < 1e-6 {
            return;
        }
        let group = self.link_group(clip_id);
        let key = format!("fade:{fade_in}:{of:?}:{clip_id}");
        self.edit_coalescing(key, Coalesce::Restart, |e| {
            for id in &group {
                if let Some(c) = e.doc_mut().find_clip_mut(id) {
                    if fade_in { fade::apply_fade_in(c, value, of) } else { fade::apply_fade_out(c, value, of) }
                }
            }
        });
    }

    /// Sets the speed of a clip and its link group; each keeps the part of its file it plays, so its length changes,
    /// and later clips on its track move out of the way. A clip with a speed curve takes its speed from the curve.
    pub fn set_speed(&mut self, clip_id: &str, speed: f64) {
        let speed = speed.clamp(MIN_SPEED, MAX_SPEED);
        let Some(clip) = self.find_clip(clip_id) else { return };
        if (clip.speed - speed).abs() < 1e-6 || clip_time::has_curve(clip) {
            return;
        }
        let group = self.link_group(clip_id);
        self.edit_coalescing(format!("speed:{clip_id}"), Coalesce::Restart, |e| {
            let doc = e.doc_mut();
            for track in &mut doc.tracks {
                let mut old_end = f64::MIN;
                let mut new_end = f64::MIN;
                let mut touched = false;
                for c in track.clips.iter_mut().filter(|c| group.contains(&c.id)) {
                    use fig_timeline::Selectable;
                    if !c.facets().contains(fig_timeline::Facet::RETIME) {
                        continue;
                    }
                    touched = true;
                    old_end = old_end.max(c.end_sec());
                    let old_speed = c.speed;
                    c.speed = speed;
                    if c.has_source_range() {
                        let out =
                            if c.source_out() > 0.0 { c.source_out() } else { c.source_in() + c.dur_sec * old_speed };
                        let span = out - c.source_in();
                        if span > 1e-9 {
                            c.dur_sec = span / speed;
                        }
                    }
                    new_end = new_end.max(c.end_sec());
                }
                // push later clips on the track out of the way so nothing overlaps
                let delta = new_end - old_end;
                if touched && delta.abs() >= 1e-9 {
                    for c in track.clips.iter_mut().filter(|c| !group.contains(&c.id) && c.start_sec >= old_end - 1e-9)
                    {
                        c.start_sec += delta;
                    }
                }
            }
        });
    }

    /// Sets the project frame rate. Clips keep their place and length.
    pub fn set_rate(&mut self, rate: FrameRate) {
        if rate == self.doc.rate && self.doc.rate_chosen {
            return;
        }
        self.edit(|e| {
            let doc = e.doc_mut();
            doc.rate = rate;
            doc.rate_chosen = true;
        });
    }

    pub fn set_frame_shape(&mut self, shape: FrameShape) {
        self.edit(|e| e.doc_mut().shape = shape);
    }

    // ---- tracks -------------------------------------------------------------------------------

    /// Adds an empty track: a video track on top of the picture, an audio track at the end. Returns its id.
    pub fn add_track(&mut self, kind: TrackKind) -> String {
        self.edit(|e| {
            let track = Track::new(kind);
            let id = track.id.clone();
            let doc = e.doc_mut();
            let top_video = doc.tracks.iter().position(|t| t.kind == TrackKind::Video);
            match top_video {
                Some(i) if kind == TrackKind::Video => doc.tracks.insert(i, track),
                _ => doc.tracks.push(track),
            }
            e.doc_refresh_track_indices();
            id
        })
    }

    /// A new track beside another: a video track above the one it was dropped on (it covers it), an audio track
    /// below. Without one, as [`add_track`](Self::add_track).
    pub(crate) fn add_track_beside(&mut self, kind: TrackKind, beside: Option<&str>) -> String {
        let Some(index) = beside.and_then(|b| self.doc.tracks.iter().position(|t| t.id == b)) else {
            return self.add_track(kind);
        };
        self.edit(|e| {
            let track = Track::new(kind);
            let id = track.id.clone();
            e.doc_mut().tracks.insert(if kind == TrackKind::Video { index } else { index + 1 }, track);
            e.doc_refresh_track_indices();
            id
        })
    }

    /// The first track of `kind`, made if there is none.
    pub fn ensure_track(&mut self, kind: TrackKind) -> String {
        match self.doc.tracks.iter().find(|t| t.kind == kind) {
            Some(t) => t.id.clone(),
            None => self.add_track(kind),
        }
    }

    /// Moves a track so it sits before the track now at `insert_index` (0 is the top; the track count is the end).
    /// Higher video tracks cover lower ones, so this changes the picture's stacking.
    pub fn move_track(&mut self, track_id: &str, insert_index: usize) -> bool {
        let Some(from) = self.doc.tracks.iter().position(|t| t.id == track_id) else {
            return false;
        };
        let insert_index = insert_index.min(self.doc.tracks.len());
        let to = if insert_index > from { insert_index - 1 } else { insert_index };
        if to == from {
            return false;
        }
        self.edit(|e| {
            let tracks = &mut e.doc_mut().tracks;
            let track = tracks.remove(from);
            tracks.insert(to, track);
            e.doc_refresh_track_indices();
        });
        true
    }

    pub fn remove_track(&mut self, track_id: &str) -> bool {
        if self.find_track(track_id).is_none() {
            return false;
        }
        self.edit(|e| {
            e.doc_mut().tracks.retain(|t| t.id != track_id);
            e.doc_refresh_track_indices();
        });
        self.selection.clear();
        true
    }

    /// Shows or hides a video track.
    pub fn set_track_visible(&mut self, track_id: &str, visible: bool) {
        self.edit(|e| {
            if let Some(t) = e.doc_mut().find_track_mut(track_id) {
                t.visible = visible;
            }
        });
    }

    /// Mutes or unmutes a track's sound.
    pub fn set_track_muted(&mut self, track_id: &str, muted: bool) {
        self.edit(|e| {
            if let Some(t) = e.doc_mut().find_track_mut(track_id) {
                t.muted = muted;
            }
        });
    }

    /// Renames a track (a blank name goes back to the automatic V1 / A1 label).
    pub fn rename_track(&mut self, track_id: &str, name: Option<&str>) {
        let name = name.map(str::trim).filter(|n| !n.is_empty()).map(str::to_string);
        self.edit(|e| {
            if let Some(t) = e.doc_mut().find_track_mut(track_id) {
                t.name = name;
            }
        });
    }

    /// Re-syncs each track's index with its place in the list (it is saved, and can be stale).
    fn doc_refresh_track_indices(&mut self) {
        for (i, track) in self.doc.tracks.iter_mut().enumerate() {
            track.index = i as i32;
        }
    }

    /// The label a track shows: its name, else V1, V2… or A1, A2… by its place among tracks of its kind.
    pub fn track_label(&self, track_id: &str) -> String {
        track_label(&self.doc, track_id)
    }
}

/// The label a track shows: its name, else V1, V2… or A1, A2… by its place among tracks of its kind.
pub fn track_label(doc: &Timeline, track_id: &str) -> String {
    let Some(track) = doc.find_track(track_id) else {
        return String::new();
    };
    if let Some(name) = &track.name {
        return name.clone();
    }
    let n = doc.tracks.iter().filter(|t| t.kind == track.kind).position(|t| t.id == track_id).unwrap_or(0) + 1;
    format!("{}{n}", if track.kind == TrackKind::Video { 'V' } else { 'A' })
}

/// The ids of every clip sharing the given clip's link group (itself included; empty when there is no such clip).
pub fn link_group(doc: &Timeline, clip_id: &str) -> Vec<String> {
    let Some(clip) = doc.find_clip(clip_id) else {
        return Vec::new();
    };
    // empty is the same as none, so unlinked clips never form a phantom group
    match clip.link_group_id.as_deref().filter(|g| !g.is_empty()) {
        None => vec![clip.id.clone()],
        Some(group) => {
            doc.clips().filter(|c| c.link_group_id.as_deref() == Some(group)).map(|c| c.id.clone()).collect()
        }
    }
}

/// Puts a clip on a track, keeping its clips in time order.
pub fn insert_clip(track: &mut Track, clip: Clip) {
    let index = track.clips.iter().position(|c| c.start_sec >= clip.start_sec).unwrap_or(track.clips.len());
    track.clips.insert(index, clip);
}

pub(crate) fn overlaps(track: &Track, start_sec: f64, dur_sec: f64, exclude: Option<&str>) -> bool {
    let end = start_sec + dur_sec;
    track.clips.iter().any(|c| Some(c.id.as_str()) != exclude && c.start_sec < end && c.end_sec() > start_sec)
}

/// "V1", "A12": a label the C# build saved as a track's name.
fn is_auto_label(name: &str) -> bool {
    let mut chars = name.chars();
    matches!(chars.next(), Some('V' | 'A')) && name.len() > 1 && chars.all(|c| c.is_ascii_digit())
}
