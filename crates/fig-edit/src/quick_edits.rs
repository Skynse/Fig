//! The fast edits: trimming to the playhead (Q / W), splitting every track, closing a gap, jumping between cuts,
//! putting media where it was dropped, moving clips that have no room to a track that has, freeze frames, speed
//! curves and tempo matching, and detaching a video's sound.

use std::collections::HashSet;

use fig_timeline::serde_util::new_id_n;
use indexmap::IndexMap;

use super::magnet::{in_time_order, insert_index, pack_track};
use super::{KeyframeHostRef, MAX_SPEED, MIN_SPEED, TimelineEditor, insert_clip, overlaps};
use fig_timeline::{
    AudioBody, Clip, ClipBody, Facet, ImageBody, KeyframeMap, KeyframePoint, MediaAsset, MediaKind, ParamValue,
    Selectable, Timeline, TrackKind, automation_keys, clip_time,
};

fn spans(clip: Option<&Clip>, at: f64) -> bool {
    clip.is_some_and(|c| c.start_sec < at - 1e-6 && at < c.end_sec() - 1e-6)
}

/// The speed that puts a clip's own beat on the song's: a dancer stepping at 100 BPM, cut to a 128 BPM song, plays at
/// 1.28×. A clip can also land on every other beat (half time) or twice a beat (double time), whichever needs a speed
/// nearer 1×.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct TempoMatch {
    pub speed: f64,
    /// 0 on the beat; -1 half time (every other beat), +1 double time, and so on.
    pub octave: i32,
}

impl TempoMatch {
    pub const MIN_BPM: f64 = 10.0;
    pub const MAX_BPM: f64 = 400.0;

    /// The speed for a clip at `clip_bpm` to play in time with `song_bpm`. With `half_or_double` it takes whichever of
    /// half, normal or double time (and so on) is nearest 1×. A speed past what a clip can play at is halved or
    /// doubled until it fits. None for a tempo out of range.
    pub fn new(song_bpm: f64, clip_bpm: f64, half_or_double: bool) -> Option<TempoMatch> {
        let range = Self::MIN_BPM..=Self::MAX_BPM;
        if !range.contains(&song_bpm) || !range.contains(&clip_bpm) {
            return None;
        }
        let exact = song_bpm / clip_bpm;
        let mut octave = if half_or_double { (-exact.log2()).round_ties_even() as i32 } else { 0 };
        while exact * 2f64.powi(octave) > MAX_SPEED {
            octave -= 1;
        }
        while exact * 2f64.powi(octave) < MIN_SPEED {
            octave += 1;
        }
        Some(TempoMatch { speed: exact * 2f64.powi(octave), octave })
    }

    /// "half time", "double time", "1/4 time"…, or "" on the beat.
    pub fn describe(&self) -> String {
        match self.octave {
            0 => String::new(),
            -1 => "half time".into(),
            1 => "double time".into(),
            o if o < 0 => format!("1/{} time", 1 << -o),
            o => format!("{}× time", 1 << o),
        }
    }
}

/// Moves clips (all of the target's kind from one track) onto the target track, every clip to its start in `starts`.
/// Clips of the other kind (a linked partner) stay on their tracks and only move in time. Nothing changes, and false
/// comes back, when anything would land on another clip.
pub(crate) fn move_clips_to_track(
    doc: &mut Timeline,
    clip_ids: &[String],
    target: &str,
    starts: &IndexMap<String, f64>,
) -> bool {
    let Some(ti) = doc.tracks.iter().position(|t| t.id == target) else { return false };
    let target_kind = doc.tracks[ti].kind;
    let mut ids: Vec<&String> = Vec::new();
    for id in clip_ids {
        if doc.find_clip(id).is_some() && !ids.contains(&id) {
            ids.push(id);
        }
    }
    let kind_of = |c: &Clip| if matches!(c.body, ClipBody::Audio(_)) { TrackKind::Audio } else { TrackKind::Video };
    let movers: Vec<&String> =
        ids.iter().copied().filter(|id| kind_of(doc.find_clip(id).unwrap()) == target_kind).collect();
    let from_tracks: HashSet<usize> = movers.iter().filter_map(|id| doc.locate_clip(id).map(|l| l.0)).collect();
    if movers.is_empty() || from_tracks.len() != 1 {
        return false; // only clips from one track move across together
    }
    let moving: HashSet<&String> = ids.iter().copied().collect();
    let start_of = |c: &Clip| starts.get(&c.id).map_or(c.start_sec, |s| s.max(0.0));
    let free = |track: usize, c: &Clip| {
        let s = start_of(c);
        !doc.tracks[track]
            .clips
            .iter()
            .any(|o| !moving.contains(&o.id) && o.start_sec < s + c.dur_sec - 1e-9 && s < o.end_sec() - 1e-9)
    };
    for id in &ids {
        let c = doc.find_clip(id).unwrap();
        let track = if movers.contains(id) { ti } else { doc.locate_clip(id).unwrap().0 };
        if !free(track, c) {
            return false;
        }
    }
    let new_starts: Vec<(String, f64)> =
        ids.iter().map(|id| ((*id).clone(), start_of(doc.find_clip(id).unwrap()))).collect();
    for (id, s) in new_starts {
        doc.find_clip_mut(&id).unwrap().start_sec = s;
    }
    for id in movers {
        let (from, c) = doc.locate_clip(id).unwrap();
        if from != ti {
            let clip = doc.tracks[from].clips.remove(c);
            insert_clip(&mut doc.tracks[ti], clip);
        }
    }
    true
}

impl TimelineEditor {
    /// Q (`keep_right`) or W: deletes the part of clips before or after the playhead. The clips are the selected ones
    /// under the playhead, else the one under it on `track_id`, else the one on the main track; their linked partners
    /// go with them. On the magnetic main track the gap closes. Returns where the playhead should go (Q on the main
    /// track pulls the rest of the clip back to it), or none when nothing was under it.
    pub fn trim_to_playhead(&mut self, pos_sec: f64, keep_right: bool, track_id: Option<&str>) -> Option<f64> {
        let at = self.snap_time(pos_sec);
        let mut seeds: Vec<String> =
            self.selected_group_seeds().into_iter().filter(|id| spans(self.find_clip(id), at)).collect();
        if seeds.is_empty() {
            let hit = track_id
                .and_then(|t| self.find_clip_at(t, at))
                .or_else(|| self.main_track_id().and_then(|m| self.find_clip_at(m, at)));
            if !spans(hit, at) {
                return None;
            }
            seeds.push(hit.unwrap().id.clone());
        }
        let kept: Vec<String> = self.edit(|e| {
            let produced = e.split_groups_quietly(&seeds, at);
            let mut kept = Vec::new();
            for pair in produced.chunks(2) {
                let [left, right] = pair else { continue };
                let (gone, keep) = if keep_right { (left, right) } else { (right, left) };
                kept.push(keep.clone());
                for t in &mut e.doc_mut().tracks {
                    t.clips.retain(|c| &c.id != gone);
                }
            }
            if let Some(main) = e.main_track_id().map(str::to_string)
                && e.is_magnetic(&main)
            {
                let mi = e.doc().tracks.iter().position(|t| t.id == main).unwrap();
                let order = in_time_order(&e.doc().tracks[mi]);
                pack_track(e.doc_mut(), mi, &order, None, 0.0);
            }
            kept
        });
        if kept.is_empty() {
            return None;
        }
        self.selection.select_clips(&kept);
        let earliest =
            kept.iter().filter_map(|id| self.find_clip(id)).map(|c| c.start_sec).fold(f64::INFINITY, f64::min);
        Some(if keep_right { earliest } else { at })
    }

    /// Cuts the groups of `seeds` that span `at` (one id pair per cut), without touching the selection.
    pub(crate) fn split_groups_quietly(&mut self, seeds: &[String], at: f64) -> Vec<String> {
        let saved = self.selection.clone();
        let produced = self.split_groups(seeds, at);
        self.selection = saved;
        produced
    }

    /// Splits every clip under the playhead, on every track.
    pub fn split_all_at_playhead(&mut self, pos_sec: f64) -> Vec<String> {
        let at = self.snap_time(pos_sec);
        let seeds: Vec<String> = self.doc().clips().filter(|c| spans(Some(c), at)).map(|c| c.id.clone()).collect();
        if seeds.is_empty() { Vec::new() } else { self.split_groups(&seeds, at) }
    }

    // ---- gaps -------------------------------------------------------------------------------

    /// The empty stretch between clips on a track at `at_sec` (before the first clip counts; after the last doesn't).
    pub fn gap_at(&self, track_id: &str, at_sec: f64) -> Option<(f64, f64)> {
        let track = self.find_track(track_id)?;
        let mut end = 0.0f64;
        for id in in_time_order(track) {
            let c = self.find_clip(&id).unwrap();
            if at_sec < c.start_sec {
                return (at_sec >= end - 1e-9 && c.start_sec - end > 1e-6).then_some((end, c.start_sec));
            }
            end = end.max(c.end_sec());
        }
        None
    }

    /// Closes the gap at `at_sec` on a track: the clips after it move back by its length, with the clips linked to
    /// them. False when there is no gap there, or a linked clip would land on another clip.
    pub fn close_gap(&mut self, track_id: &str, at_sec: f64) -> bool {
        let Some((gs, ge)) = self.gap_at(track_id, at_sec) else { return false };
        let shift = ge - gs;
        let mut starts: IndexMap<String, f64> = IndexMap::new();
        for c in self.find_track(track_id).unwrap().clips.iter().filter(|c| c.start_sec >= ge - 1e-9) {
            for m in self.link_group(&c.id) {
                let s = self.find_clip(&m).unwrap().start_sec;
                starts.insert(m, (s - shift).max(0.0));
            }
        }
        if starts.is_empty() || self.would_overlap_move(&starts) {
            return false;
        }
        self.edit(|e| {
            for (id, s) in &starts {
                e.doc_mut().find_clip_mut(id).unwrap().start_sec = *s;
            }
        });
        true
    }

    // ---- jumping between cuts -----------------------------------------------------------------

    /// Every place a clip starts or ends, on every track, earliest first (0 included).
    pub fn edit_points(&self) -> Vec<f64> {
        let mut points: Vec<f64> = std::iter::once(0.0)
            .chain(self.doc().clips().flat_map(|c| [c.start_sec, c.end_sec()]))
            .map(|t| (t * 1e6).round() / 1e6)
            .collect();
        points.sort_by(f64::total_cmp);
        points.dedup();
        points
    }

    /// The next cut after `at_sec` (more than half a frame on).
    pub fn next_edit_point(&self, at_sec: f64) -> Option<f64> {
        let tolerance = 0.5 / self.doc().rate.fps().max(1.0);
        self.edit_points().into_iter().find(|p| *p > at_sec + tolerance)
    }

    /// The cut before `at_sec` (more than half a frame back).
    pub fn previous_edit_point(&self, at_sec: f64) -> Option<f64> {
        let tolerance = 0.5 / self.doc().rate.fps().max(1.0);
        self.edit_points().into_iter().take_while(|p| *p < at_sec - tolerance).last()
    }

    // ---- putting media and clips where they fit -----------------------------------------------

    /// Puts media on the timeline where it was dropped. On the magnetic main track it goes in at the nearest cut and
    /// the later clips move along. On another track with no room there, it goes on the nearest track of its kind that
    /// has room, or a new one beside it. With no track (dropped below them all) it gets a new track. Returns the clip's
    /// id.
    pub fn place_media(&mut self, asset: &MediaAsset, track_id: Option<&str>, at_sec: f64) -> String {
        let kind = if asset.kind == MediaKind::Audio { TrackKind::Audio } else { TrackKind::Video };
        self.edit(|e| {
            if !e.doc().rate_chosen
                && e.doc().clips().next().is_none()
                && let Some(rate) = fig_timeline::FrameRate::plausible(asset.source_rate)
            {
                e.doc_mut().rate = rate;
            }
            let mut clip = Self::clip_from_asset(asset);
            clip.start_sec = e.snap_time(at_sec.max(0.0));
            let mut target = track_id.and_then(|id| e.find_track(id)).map(|t| (t.id.clone(), t.kind));
            if let Some((_, k)) = &target
                && *k != kind
            {
                target = if kind == TrackKind::Video {
                    e.main_track_id().map(|m| (m.to_string(), kind))
                } else {
                    e.doc().tracks.iter().find(|t| t.kind == kind).map(|t| (t.id.clone(), kind))
                };
            }
            let target = match target {
                Some((id, _)) => id,
                None => e.add_track(kind),
            };
            let id = clip.id.clone();
            e.put_clip(clip, &target);
            id
        })
    }

    /// Puts a clip on a track: into the main track's order, or where it is if there's room, else on a track that has
    /// room.
    fn put_clip(&mut self, clip: Clip, target: &str) {
        let doc = self.doc_mut();
        let ti = doc.tracks.iter().position(|t| t.id == target).unwrap();
        if self.is_magnetic(target) {
            let doc = self.doc_mut();
            let mut order = in_time_order(&doc.tracks[ti]);
            let durs: Vec<f64> = order.iter().map(|id| doc.find_clip(id).unwrap().dur_sec).collect();
            order.insert(insert_index(durs, clip.start_sec), clip.id.clone());
            insert_clip(&mut doc.tracks[ti], clip);
            pack_track(doc, ti, &order, None, 0.0);
            return;
        }
        let mut track = target.to_string();
        if overlaps(&self.doc().tracks[ti], clip.start_sec, clip.dur_sec, None) {
            let near = self
                .tracks_near(target)
                .into_iter()
                .find(|t| !self.is_magnetic(t) && !self.would_overlap(t, clip.start_sec, clip.dur_sec, None));
            let kind = self.doc().tracks[ti].kind;
            track = near.unwrap_or_else(|| self.add_track_beside(kind, Some(target)));
        }
        let doc = self.doc_mut();
        let ti = doc.tracks.iter().position(|t| t.id == track).unwrap();
        insert_clip(&mut doc.tracks[ti], clip);
    }

    /// The other tracks of the track's kind, nearest first; on a tie, the side a new one would go on first.
    fn tracks_near(&self, track_id: &str) -> Vec<String> {
        let tracks = &self.doc().tracks;
        let Some(index) = tracks.iter().position(|t| t.id == track_id) else { return Vec::new() };
        let kind = tracks[index].kind;
        let step: i64 = if kind == TrackKind::Video { -1 } else { 1 };
        let mut candidates: Vec<(String, i64)> = tracks
            .iter()
            .enumerate()
            .filter(|(i, t)| *i != index && t.kind == kind)
            .map(|(i, t)| {
                let d = i as i64 - index as i64;
                (t.id.clone(), d.abs() * 2 + if d.signum() == step { 0 } else { 1 })
            })
            .collect();
        candidates.sort_by_key(|c| c.1);
        candidates.into_iter().map(|c| c.0).collect()
    }

    /// Moves dragged clips live onto a track (see [`move_clips_to_track`]). For a live edit.
    pub fn move_clips_to_track_live(
        &mut self,
        clip_ids: &[String],
        target: &str,
        starts: &IndexMap<String, f64>,
    ) -> bool {
        move_clips_to_track(self.live_doc(), clip_ids, target, starts)
    }

    /// Clips dragged onto a track where they have no room go on the nearest track of that kind that has room at
    /// `starts`, or on a new one beside it (above for video, below for audio).
    pub fn move_clips_to_free_track(
        &mut self,
        clip_ids: &[String],
        near_track_id: &str,
        starts: &IndexMap<String, f64>,
    ) -> bool {
        let Some(kind) = self.find_track(near_track_id).map(|t| t.kind) else { return false };
        self.edit(|e| {
            for t in e.tracks_near(near_track_id) {
                if !e.is_magnetic(&t) && move_clips_to_track(e.doc_mut(), clip_ids, &t, starts) {
                    return true;
                }
            }
            let fresh = e.add_track_beside(kind, Some(near_track_id));
            move_clips_to_track(e.doc_mut(), clip_ids, &fresh, starts)
        })
    }

    // ---- freeze frame -------------------------------------------------------------------------

    /// How long an inserted freeze frame holds when it is made.
    pub const FREEZE_FRAME_SEC: f64 = 3.0;

    /// Holds the picture of a video clip at `at_sec` for the rest of the clip: the clip splits there and its second
    /// half becomes `still` (that frame, saved as an image). Nothing moves and the clip keeps its length, so a cut to
    /// music stays on the beat. The still keeps the half's effects, keyframes, opacity, crop, fades and transition out;
    /// the clip's own sound stops there (sound on a linked track plays on). None when the playhead isn't on the clip.
    /// Returns the still's clip id.
    pub fn freeze_frame(&mut self, clip_id: &str, at_sec: f64, still: &MediaAsset) -> Option<String> {
        let video = self.find_clip(clip_id).filter(|c| c.as_video().is_some())?.clone();
        let at = self.snap_time(at_sec);
        if at < video.start_sec - 1e-6 || at >= video.end_sec() - 1e-6 {
            return None;
        }
        let whole = at <= video.start_sec + 1e-6;
        let hold_id = self.edit(|e| {
            let target = if whole {
                video.id.clone()
            } else {
                // the held half stays linked with the footage's sound, so they still move together
                e.cut_one_into(&video.id, at, video.link_group_id.clone()).get(1)?.clone()
            };
            let c = e.doc_mut().find_clip_mut(&target)?;
            let ClipBody::Video(v) = &c.body else { return None };
            c.body = ClipBody::Image(ImageBody {
                source_id: still.id.clone(),
                crop_l: v.crop_l,
                crop_t: v.crop_t,
                crop_r: v.crop_r,
                crop_b: v.crop_b,
                ..Default::default()
            });
            c.speed = 1.0;
            if whole {
                c.id = new_id_n();
            }
            Some(c.id.clone())
        })?;
        self.selection.select_only(&hold_id);
        Some(hold_id)
    }

    /// The clips on a track from `at` on (and `also`), with the clips linked to them, once each.
    fn followers(&self, track_id: &str, at: f64, also: Option<&str>) -> Vec<String> {
        let mut seen = HashSet::new();
        let mut out = Vec::new();
        for c in &self.find_track(track_id).unwrap().clips {
            if c.start_sec >= at - 1e-6 || Some(c.id.as_str()) == also {
                for m in self.link_group(&c.id) {
                    if seen.insert(m.clone()) {
                        out.push(m);
                    }
                }
            }
        }
        out
    }

    /// Inserts a held picture of a video clip at `at_sec`: the clip splits there and `still` (that frame, saved as an
    /// image) goes in between for three seconds, pushing the rest of the track along. The still keeps the clip's
    /// effects, opacity and crop. None when the playhead isn't on the clip, or the rest can't move without landing on
    /// a linked clip's neighbour. Returns the still's clip id.
    pub fn insert_freeze_frame(&mut self, clip_id: &str, at_sec: f64, still: &MediaAsset) -> Option<String> {
        let video = self.find_clip(clip_id).filter(|c| c.as_video().is_some())?.clone();
        let track = self.find_clip_track_id(clip_id)?;
        let at = self.snap_time(at_sec);
        if at < video.start_sec - 1e-6 || at >= video.end_sec() - 1e-6 {
            return None;
        }
        let mut hold = Self::clip_from_asset(still);
        hold.start_sec = at;
        hold.dur_sec = Self::FREEZE_FRAME_SEC;
        hold.effects = video.effects.iter().map(|e| e.duplicate()).collect();
        hold.opacity = video.opacity;
        if let (ClipBody::Image(i), ClipBody::Video(v)) = (&mut hold.body, &video.body) {
            (i.crop_l, i.crop_t, i.crop_r, i.crop_b) = (v.crop_l, v.crop_t, v.crop_r, v.crop_b);
        }
        let magnetic = self.is_magnetic(&track);
        if !magnetic {
            let moves: IndexMap<String, f64> = self
                .followers(&track, at, Some(clip_id))
                .into_iter()
                .map(|id| {
                    let s = self.find_clip(&id).unwrap().start_sec + Self::FREEZE_FRAME_SEC;
                    (id, s)
                })
                .collect();
            if self.would_overlap_move(&moves) {
                return None;
            }
        }
        let hold_id = hold.id.clone();
        self.edit(|e| {
            if spans(Some(&video), at) {
                e.split_groups_quietly(std::slice::from_ref(&video.id), at);
            }
            let ti = e.doc().tracks.iter().position(|t| t.id == track).unwrap();
            if magnetic {
                let mut order = in_time_order(&e.doc().tracks[ti]);
                let index =
                    order.iter().position(|id| e.find_clip(id).unwrap().start_sec >= at - 1e-6).unwrap_or(order.len());
                order.insert(index, hold.id.clone());
                let doc = e.doc_mut();
                insert_clip(&mut doc.tracks[ti], hold);
                pack_track(doc, ti, &order, None, 0.0);
                return;
            }
            for id in e.followers(&track, at, None) {
                e.doc_mut().find_clip_mut(&id).unwrap().start_sec += Self::FREEZE_FRAME_SEC;
            }
            insert_clip(&mut e.doc_mut().tracks[ti], hold);
        });
        self.selection.select_only(&hold_id);
        Some(hold_id)
    }

    // ---- speed ------------------------------------------------------------------------------

    /// A clip that can take a speed curve: a video clip (its own sound follows the curve).
    pub fn can_have_speed_curve(&self, clip_id: &str) -> bool {
        self.find_clip(clip_id).is_some_and(|c| c.as_video().is_some())
    }

    /// Gives the clip a speed curve: a flat line at its speed, a point at each end, so nothing changes until a point
    /// moves.
    pub fn add_speed_curve(&mut self, clip_id: &str) {
        let Some(clip) = self.find_clip(clip_id) else { return };
        if !self.can_have_speed_curve(clip_id) || clip_time::has_curve(clip) {
            return;
        }
        let speed = ParamValue::Double(clip.speed.clamp(MIN_SPEED, MAX_SPEED));
        let dur = clip.dur_sec;
        self.set_speed_curve(clip_id, Some(&[KeyframePoint::new(0.0, speed), KeyframePoint::new(dur, speed)]), "");
    }

    /// Takes the curve off: the clip plays at one speed again, keeping its length.
    pub fn remove_speed_curve(&mut self, clip_id: &str) {
        if self.find_clip(clip_id).is_some_and(clip_time::has_curve) {
            self.set_speed_curve(clip_id, None, "");
        }
    }

    /// Replaces the clip's speed curve; speeds are held to 0.1×–8× and times to 0 or later (points past the end stay:
    /// trimming the clip longer brings them back). Edits sharing a `gesture` (one drag) are one undo step.
    pub fn set_speed_curve(&mut self, clip_id: &str, keys: Option<&[KeyframePoint]>, gesture: &str) {
        if self.find_clip(clip_id).is_none() {
            return;
        }
        let cleaned: Option<Vec<KeyframePoint>> = keys.map(|keys| {
            let mut by_time: Vec<KeyframePoint> = Vec::new();
            for k in keys {
                let k = KeyframePoint {
                    time_sec: k.time_sec.max(0.0),
                    value: ParamValue::Double(k.value.as_number().clamp(MIN_SPEED, MAX_SPEED)),
                    ..*k
                };
                let t = (k.time_sec * 1e6).round();
                by_time.retain(|p| (p.time_sec * 1e6).round() != t);
                by_time.push(k);
            }
            by_time.sort_by(|a, b| a.time_sec.total_cmp(&b.time_sec));
            by_time
        });
        let map =
            cleaned.filter(|c| !c.is_empty()).map(|c| KeyframeMap::from([(automation_keys::SPEED.to_string(), c)]));
        self.replace_keyframes(&KeyframeHostRef::of_speed(clip_id), map.as_ref(), gesture);
    }

    /// Plays clips at `speed` (a tempo match) as one undo step; clips with a speed curve, and those that can't be
    /// retimed, are left alone. On the magnetic main track the clips after them close up or make room. Returns how
    /// many clips changed.
    pub fn set_speeds(&mut self, clip_ids: &[String], speed: f64) -> usize {
        let speed = speed.clamp(MIN_SPEED, MAX_SPEED);
        let mut seen = HashSet::new();
        let targets: Vec<String> = clip_ids
            .iter()
            .filter_map(|id| self.find_clip(id))
            .filter(|c| {
                c.facets().contains(Facet::RETIME) && !clip_time::has_curve(c) && (c.speed - speed).abs() > 1e-6
            })
            .filter(|c| seen.insert(c.link_group_id.clone().filter(|g| !g.is_empty()).unwrap_or_else(|| c.id.clone())))
            .map(|c| c.id.clone())
            .collect();
        if targets.is_empty() {
            return 0;
        }
        self.keep_main_track_closed(|e| {
            for id in &targets {
                e.set_speed(id, speed);
            }
        });
        // a speed drag after this is a new step, not part of the match
        self.break_coalescing();
        targets.len()
    }

    // ---- a video's own sound -------------------------------------------------------------------

    /// True when any of the clips is a video clip still playing its own sound.
    pub fn can_detach_audio(&self, clip_ids: &[String]) -> bool {
        clip_ids.iter().any(|id| self.find_clip(id).and_then(Clip::as_video).is_some_and(|v| v.plays_audio))
    }

    /// Moves each video clip's sound to an audio clip of its own, on the first audio track with room (or a new one):
    /// the same part of the file, place, speed (and speed curve), level, sound fades and volume keyframes, so it sounds
    /// the same. The new clips are not linked. One undo step; returns the new clips' ids.
    pub fn detach_audio(&mut self, clip_ids: &[String]) -> Vec<String> {
        let mut seen = HashSet::new();
        let videos: Vec<Clip> = clip_ids
            .iter()
            .filter(|id| seen.insert((*id).clone()))
            .filter_map(|id| self.find_clip(id))
            .filter(|c| c.as_video().is_some_and(|v| v.plays_audio))
            .cloned()
            .collect();
        if videos.is_empty() {
            return Vec::new();
        }
        self.edit(|e| {
            let mut created = Vec::new();
            for video in videos {
                let v = video.as_video().unwrap();
                let mut audio = Clip::new(AudioBody {
                    source_id: v.source_id.clone(),
                    src_in_sec: v.src_in_sec,
                    src_out_sec: v.src_out_sec,
                })
                .at(video.start_sec, video.dur_sec);
                audio.source_rate = video.source_rate;
                audio.speed = video.speed;
                audio.volume = video.volume;
                audio.fade_in_sec = v.audio_fade_in_sec;
                audio.fade_out_sec = v.audio_fade_out_sec;
                audio.enabled = video.enabled;
                if let Some(volume) = video.keyframes.get(automation_keys::VOLUME) {
                    audio.keyframes.insert(automation_keys::VOLUME.into(), volume.clone());
                }
                // a velocity edit: the sound keeps following the picture's speed curve
                if let Some(curve) = clip_time::curve(&video) {
                    audio.keyframes.insert(automation_keys::SPEED.into(), curve.to_vec());
                }
                if let Some(vb) = e.doc_mut().find_clip_mut(&video.id).and_then(Clip::as_video_mut) {
                    vb.plays_audio = false;
                }
                let track = e
                    .doc()
                    .tracks
                    .iter()
                    .find(|t| t.kind == TrackKind::Audio && !overlaps(t, audio.start_sec, audio.dur_sec, None))
                    .map(|t| t.id.clone());
                let track = track.unwrap_or_else(|| e.add_track(TrackKind::Audio));
                created.push(audio.id.clone());
                insert_clip(e.doc_mut().find_track_mut(&track).unwrap(), audio);
            }
            created
        })
    }
}
