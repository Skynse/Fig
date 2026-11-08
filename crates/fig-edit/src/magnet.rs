//! The main track magnet. The main track is the lowest video track; while the magnet is on its clips sit end to end
//! from the start, so deleting, trimming or moving a clip there closes the gap, and a clip put there goes in at the
//! nearest cut and pushes the later ones along. Clips linked to a main-track clip (its detached sound) move with it.
//! Every other track keeps clips where they are put.

use std::collections::HashMap;

use super::{TimelineEditor, insert_clip};
use fig_timeline::{Timeline, Track, TrackKind};

impl TimelineEditor {
    /// Turns the magnet on or off. Turning it on closes the main track's gaps (one undo step with it).
    pub fn set_main_track_magnet(&mut self, on: bool) {
        if self.doc().main_track_magnet == on {
            return;
        }
        self.edit(|e| {
            e.doc_mut().main_track_magnet = on;
            if on {
                e.close_main_track_gaps();
            }
        });
    }

    /// The lowest video track.
    pub fn main_track_id(&self) -> Option<&str> {
        main_track(self.doc()).map(|i| self.doc().tracks[i].id.as_str())
    }

    /// True for the main track while the magnet is on.
    pub fn is_magnetic(&self, track_id: &str) -> bool {
        self.doc().main_track_magnet && self.main_track_id() == Some(track_id)
    }

    /// Closes the main track's gaps while the magnet is on. False when there were none.
    pub fn close_main_track_gaps(&mut self) -> bool {
        let Some(main) = main_track(self.doc()) else { return false };
        if !self.doc().main_track_magnet || !has_gaps(&self.doc().tracks[main]) {
            return false;
        }
        self.edit(|e| {
            let order = in_time_order(&e.doc().tracks[main]);
            pack_track(e.doc_mut(), main, &order, None, 0.0);
        });
        true
    }

    /// Runs an edit, then, if it changed the main track while the magnet is on, closes the gaps it left. The edit and
    /// the closing are one undo step.
    pub fn keep_main_track_closed<R>(&mut self, f: impl FnOnce(&mut Self) -> R) -> R {
        let main_id = self.main_track_id().map(str::to_string);
        let before = main_id.as_deref().and_then(|id| self.find_track(id)).map(footprint);
        self.edit(|e| {
            let r = f(e);
            if let Some(id) = &main_id
                && e.find_track(id).is_some_and(|t| Some(footprint(t)) != before)
            {
                e.close_main_track_gaps();
            }
            r
        })
    }

    /// Moves a clip onto another track as it is, overlaps and all: a drag in progress, which puts everything right
    /// before it ends. For a live edit.
    pub fn put_on_track_live(&mut self, clip_id: &str, target_track_id: &str) {
        let doc = self.live_doc();
        let Some((from, c)) = doc.locate_clip(clip_id) else { return };
        if doc.tracks[from].id == target_track_id {
            return;
        }
        let Some(to) = doc.tracks.iter().position(|t| t.id == target_track_id) else { return };
        let clip = doc.tracks[from].clips.remove(c);
        insert_clip(&mut doc.tracks[to], clip);
    }

    /// Lays the clips `order` (ids, on the track at `track`) end to end from its start, leaving `hole_sec` empty
    /// before the clip at `hole_at` (where a dragged clip will go). For a live edit.
    pub fn pack_track_live(&mut self, track: usize, order: &[String], hole_at: Option<usize>, hole_sec: f64) {
        let doc = self.live_doc();
        pack_track(doc, track, order, hole_at, hole_sec);
    }
}

/// The index of the lowest video track.
pub fn main_track(doc: &Timeline) -> Option<usize> {
    doc.tracks.iter().rposition(|t| t.kind == TrackKind::Video)
}

/// A track's clip ids, earliest first.
pub fn in_time_order(track: &Track) -> Vec<String> {
    let mut clips: Vec<_> = track.clips.iter().collect();
    clips.sort_by(|a, b| a.start_sec.total_cmp(&b.start_sec).then(a.dur_sec.total_cmp(&b.dur_sec)));
    clips.into_iter().map(|c| c.id.clone()).collect()
}

/// Where a clip put at `at_sec` goes among clips of these lengths laid end to end: before the first clip whose middle
/// is later, so it lands at whichever cut is nearest.
pub fn insert_index(durations: impl IntoIterator<Item = f64>, at_sec: f64) -> usize {
    let mut at = 0.0;
    let mut count = 0;
    for (i, dur) in durations.into_iter().enumerate() {
        if at_sec < at + dur / 2.0 {
            return i;
        }
        at += dur;
        count = i + 1;
    }
    count
}

/// True when a track's clips don't run end to end from the start.
pub fn has_gaps(track: &Track) -> bool {
    let mut at = 0.0;
    for id in in_time_order(track) {
        let clip = track.clips.iter().find(|c| c.id == id).unwrap();
        if (clip.start_sec - at).abs() > 1e-6 {
            return true;
        }
        at += clip.dur_sec;
    }
    false
}

/// Lays `order` end to end from the start of the track at `track`, leaving `hole_sec` empty before the clip at
/// `hole_at`. Clips on other tracks linked to them move by the same amount.
pub(crate) fn pack_track(doc: &mut Timeline, track: usize, order: &[String], hole_at: Option<usize>, hole_sec: f64) {
    // the moves first (clips on other tracks linked to them follow), then applied
    let mut at = 0.0;
    let mut moves: HashMap<String, f64> = HashMap::new();
    let mut group_moves: Vec<(String, f64)> = Vec::new();
    for (i, id) in order.iter().enumerate() {
        if Some(i) == hole_at {
            at += hole_sec;
        }
        let Some(clip) = doc.tracks[track].clips.iter().find(|c| &c.id == id) else { continue };
        let delta = at - clip.start_sec;
        if delta.abs() > 1e-9 {
            moves.insert(id.clone(), at);
            if let Some(group) = clip.link_group_id.clone().filter(|g| !g.is_empty()) {
                group_moves.push((group, delta));
            }
        }
        at += clip.dur_sec;
    }
    for c in &mut doc.tracks[track].clips {
        if let Some(&start) = moves.get(&c.id) {
            c.start_sec = start;
        }
    }
    for (t, other) in doc.tracks.iter_mut().enumerate() {
        if t == track {
            continue;
        }
        for c in &mut other.clips {
            for (group, delta) in &group_moves {
                if c.link_group_id.as_ref() == Some(group) {
                    c.start_sec = (c.start_sec + delta).max(0.0);
                }
            }
        }
    }
}

fn footprint(track: &Track) -> Vec<(String, f64, f64)> {
    track.clips.iter().map(|c| (c.id.clone(), c.start_sec, c.dur_sec)).collect()
}
