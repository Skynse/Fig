//! Markers on clips, tracks and the timeline: adding, picking, editing, moving, ranges, beat markers and cutting at
//! markers.

use std::collections::HashSet;

use super::TimelineEditor;
use super::history::Coalesce;
use fig_timeline::serde_util::new_id_n;
use fig_timeline::{ClipBody, Marker, Timeline};

/// What a marker is pinned to.
#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub enum MarkerOwner {
    /// The timeline: the marker's time is absolute.
    Timeline,
    /// A track: absolute.
    Track(String),
    /// A clip: seconds from the clip's start.
    Clip(String),
}

/// Where a marker lives, with a copy of it.
#[derive(Clone, Debug, PartialEq)]
pub struct MarkerLocation {
    pub marker: Marker,
    pub owner: MarkerOwner,
    /// Where its time 0 is on the timeline: its clip's start, or 0.
    pub origin_sec: f64,
    /// For a clip marker: how long the clip is.
    pub clip_dur_sec: Option<f64>,
}

impl MarkerLocation {
    /// The marker's time on the timeline.
    pub fn timeline_sec(&self) -> f64 {
        self.origin_sec + self.marker.start_sec
    }
}

/// Every marker in the timeline, with where it lives.
pub fn all_markers(doc: &Timeline) -> Vec<MarkerLocation> {
    let mut out: Vec<MarkerLocation> = doc
        .markers
        .iter()
        .map(|m| MarkerLocation {
            marker: m.clone(),
            owner: MarkerOwner::Timeline,
            origin_sec: 0.0,
            clip_dur_sec: None,
        })
        .collect();
    for track in &doc.tracks {
        out.extend(track.markers.iter().map(|m| MarkerLocation {
            marker: m.clone(),
            owner: MarkerOwner::Track(track.id.clone()),
            origin_sec: 0.0,
            clip_dur_sec: None,
        }));
        for clip in &track.clips {
            out.extend(clip.markers.iter().map(|m| MarkerLocation {
                marker: m.clone(),
                owner: MarkerOwner::Clip(clip.id.clone()),
                origin_sec: clip.start_sec,
                clip_dur_sec: Some(clip.dur_sec),
            }));
        }
    }
    out
}

/// Where a marker lives, if it still exists.
pub fn find_marker(doc: &Timeline, marker_id: &str) -> Option<MarkerLocation> {
    all_markers(doc).into_iter().find(|m| m.marker.id == marker_id)
}

fn owner_markers_mut<'a>(doc: &'a mut Timeline, owner: &MarkerOwner) -> Option<&'a mut Vec<Marker>> {
    match owner {
        MarkerOwner::Timeline => Some(&mut doc.markers),
        MarkerOwner::Track(id) => doc.find_track_mut(id).map(|t| &mut t.markers),
        MarkerOwner::Clip(id) => doc.find_clip_mut(id).map(|c| &mut c.markers),
    }
}

fn marker_mut<'a>(doc: &'a mut Timeline, marker_id: &str) -> Option<&'a mut Marker> {
    let owner = find_marker(doc, marker_id)?.owner;
    owner_markers_mut(doc, &owner)?.iter_mut().find(|m| m.id == marker_id)
}

/// Several marker fields to change at once; none leaves a field as it is.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct MarkerEdit {
    pub name: Option<String>,
    pub color: Option<String>,
    pub start_sec: Option<f64>,
    pub dur_sec: Option<f64>,
    pub include_in_export: Option<bool>,
    /// An empty name goes back to one derived from the marker's name.
    pub output_name: Option<String>,
}

impl MarkerEdit {
    /// Only times change (a drag), so steps of it can join.
    fn is_timing_only(&self) -> bool {
        self.name.is_none() && self.color.is_none() && self.include_in_export.is_none() && self.output_name.is_none()
    }
}

/// A range between bookmarks, planned before it is made.
#[derive(Clone, Debug, PartialEq)]
pub struct PlannedRange {
    pub name: String,
    pub start_sec: f64,
    pub end_sec: f64,
    pub color: String,
}

/// Where the last clip ends.
pub fn sequence_end_sec(doc: &Timeline) -> f64 {
    doc.duration_sec()
}

/// One range per section between the timeline's bookmarks (and, with `include_sequence_bounds`, from the start and
/// to the end).
pub fn plan_bookmark_ranges(doc: &Timeline, include_sequence_bounds: bool) -> Vec<PlannedRange> {
    let end = sequence_end_sec(doc);
    let mut points: Vec<&Marker> = doc.markers.iter().filter(|m| !m.is_range() && m.start_sec <= end + 1e-6).collect();
    points.sort_by(|a, b| a.start_sec.total_cmp(&b.start_sec));
    let mut bounds: Vec<(String, f64, String)> = Vec::new();
    if include_sequence_bounds {
        bounds.push(("Sequence start".into(), 0.0, "#ffd60a".into()));
    }
    for m in points {
        let name = if m.name.trim().is_empty() { "Bookmark".to_string() } else { m.name.trim().to_string() };
        bounds.push((name, m.start_sec, m.color.clone()));
    }
    if include_sequence_bounds {
        bounds.push(("Sequence end".into(), end, "#ffd60a".into()));
    }
    let mut ranges = Vec::new();
    for (i, pair) in bounds.windows(2).enumerate() {
        let (from, to) = (&pair[0], &pair[1]);
        if to.1 - from.1 < 1e-3 {
            continue;
        }
        let color = if i == 0 && !include_sequence_bounds { to.2.clone() } else { from.2.clone() };
        ranges.push(PlannedRange {
            name: format!("{} \u{2192} {}", from.0, to.0),
            start_sec: from.1,
            end_sec: to.1,
            color,
        });
    }
    ranges
}

/// Beat times cleaned up for a clip: finite, inside it, each once, in order.
pub fn clean_beat_times(times: &[f64], clip_dur_sec: f64) -> Vec<f64> {
    let mut t: Vec<f64> = times.iter().copied().filter(|t| t.is_finite() && *t >= 0.0 && *t < clip_dur_sec).collect();
    t.sort_by(f64::total_cmp);
    t.dedup();
    t
}

/// Beat markers shown on a clip before they are committed. Not part of the timeline.
#[derive(Clone, Debug, PartialEq)]
pub struct BeatPreview {
    pub clip_id: String,
    pub local_times: Vec<f64>,
}

impl TimelineEditor {
    pub fn all_markers(&self) -> Vec<MarkerLocation> {
        all_markers(self.doc())
    }

    pub fn find_marker(&self, marker_id: &str) -> Option<MarkerLocation> {
        find_marker(self.doc(), marker_id)
    }

    /// Picks a marker: alone, toggled into the selection, or (`extend`) the run from the anchor to it in time order,
    /// counting only `visible_ids` when given.
    pub fn select_marker(&mut self, id: &str, toggle: bool, extend: bool, visible_ids: Option<&HashSet<String>>) {
        if self.find_marker(id).is_none() {
            return;
        }
        if extend {
            let mut ordered: Vec<MarkerLocation> = self
                .all_markers()
                .into_iter()
                .filter(|m| visible_ids.is_none_or(|v| v.contains(&m.marker.id)))
                .collect();
            ordered.sort_by(|a, b| a.timeline_sec().total_cmp(&b.timeline_sec()).then(a.marker.id.cmp(&b.marker.id)));
            let ids: Vec<String> = ordered.into_iter().map(|m| m.marker.id).collect();
            self.selection.select_marker_range(id, &ids, toggle);
        } else if toggle {
            self.selection.toggle_marker(id);
        } else {
            self.selection.select_marker(id);
        }
    }

    fn add_marker_to(&mut self, owner: MarkerOwner, marker: Marker) -> Option<Marker> {
        let added = marker.clone();
        let ok = self.edit(|e| match owner_markers_mut(e.doc_mut(), &owner) {
            Some(list) => {
                list.push(marker);
                true
            }
            None => false,
        });
        ok.then_some(added)
    }

    /// Adds a marker on a clip at a time from its start (held inside the clip).
    pub fn add_clip_marker(&mut self, clip_id: &str, local_sec: f64, name: &str, color: &str) -> Option<Marker> {
        let dur = self.find_clip(clip_id)?.dur_sec;
        let m = Marker {
            name: name.into(),
            start_sec: local_sec.clamp(0.0, dur),
            color: color.into(),
            ..Default::default()
        };
        self.add_marker_to(MarkerOwner::Clip(clip_id.into()), m)
    }

    /// Adds a marker on a track at a timeline time.
    pub fn add_track_marker(&mut self, track_id: &str, sec: f64, name: &str, color: &str) -> Option<Marker> {
        let m = Marker { name: name.into(), start_sec: sec.max(0.0), color: color.into(), ..Default::default() };
        self.add_marker_to(MarkerOwner::Track(track_id.into()), m)
    }

    /// Adds a marker on the timeline.
    pub fn add_timeline_marker(&mut self, sec: f64, name: &str, color: &str) -> Marker {
        let m = Marker { name: name.into(), start_sec: sec.max(0.0), color: color.into(), ..Default::default() };
        self.add_marker_to(MarkerOwner::Timeline, m).unwrap()
    }

    /// Adds a range marker to the timeline.
    pub fn add_range_marker(&mut self, start_sec: f64, dur_sec: f64, name: &str, color: &str) -> Marker {
        let m = Marker {
            name: name.into(),
            start_sec: start_sec.max(0.0),
            dur_sec: dur_sec.max(0.0),
            color: color.into(),
            ..Default::default()
        };
        self.add_marker_to(MarkerOwner::Timeline, m).unwrap()
    }

    /// Replaces a clip's beat markers with markers at these clip-local times, as one edit.
    pub fn add_beat_markers(&mut self, clip_id: &str, times: &[f64]) -> Vec<Marker> {
        let Some(clip) = self.find_clip(clip_id) else { return Vec::new() };
        let had_beats = clip.markers.iter().any(Marker::is_generated_beat);
        let markers: Vec<Marker> = clean_beat_times(times, clip.dur_sec)
            .into_iter()
            .enumerate()
            .map(|(i, t)| Marker {
                start_sec: t,
                name: format!("Beat {}", i + 1),
                color: "#ffd60a".into(),
                metadata: Some(fig_timeline::Metadata::from_iter([(
                    Marker::BEAT_ANALYSIS_KEY.to_string(),
                    true.into(),
                )])),
                ..Default::default()
            })
            .collect();
        if markers.is_empty() && !had_beats {
            return Vec::new();
        }
        let added = markers.clone();
        self.edit(|e| {
            if let Some(c) = e.doc_mut().find_clip_mut(clip_id) {
                c.markers.retain(|m| !m.is_generated_beat());
                c.markers.extend(markers);
            }
        });
        added
    }

    pub fn delete_marker(&mut self, marker_id: &str) {
        self.delete_markers(&[marker_id.to_string()]);
    }

    /// Deletes markers (one undo step), and takes them out of the selection. Returns how many.
    pub fn delete_markers(&mut self, marker_ids: &[String]) -> usize {
        let ids: HashSet<&String> = marker_ids.iter().filter(|id| self.find_marker(id).is_some()).collect();
        if ids.is_empty() {
            return 0;
        }
        self.edit(|e| {
            let doc = e.doc_mut();
            doc.markers.retain(|m| !ids.contains(&m.id));
            for t in &mut doc.tracks {
                t.markers.retain(|m| !ids.contains(&m.id));
                for c in &mut t.clips {
                    c.markers.retain(|m| !ids.contains(&m.id));
                }
            }
        });
        let selected = self.selection.selected_marker_ids();
        if selected.iter().any(|s| ids.contains(s)) {
            let left: Vec<String> = selected.into_iter().filter(|s| !ids.contains(s)).collect();
            self.selection.select_markers(&left, None);
        }
        ids.len()
    }

    /// Deletes a clip's markers (or just its beat markers).
    pub fn clear_clip_markers(&mut self, clip_id: &str, generated_only: bool) -> usize {
        let Some(clip) = self.find_clip(clip_id) else { return 0 };
        let ids: Vec<String> =
            clip.markers.iter().filter(|m| !generated_only || m.is_generated_beat()).map(|m| m.id.clone()).collect();
        self.delete_markers(&ids)
    }

    /// A range from a timeline bookmark: with `as_start` it runs from the bookmark to the next one (or the end of the
    /// sequence); otherwise from the previous one (or the start) to this one. None when the bookmark is not a
    /// timeline point marker or there is no room.
    pub fn add_range_from_bookmark(&mut self, marker_id: &str, as_start: bool) -> Option<Marker> {
        let doc = self.doc();
        let bookmark = doc.markers.iter().find(|m| m.id == marker_id && !m.is_range())?.clone();
        let others: Vec<&Marker> = doc.markers.iter().filter(|m| !m.is_range() && m.id != bookmark.id).collect();
        let (from, to, from_name, to_name) = if as_start {
            let next = others
                .iter()
                .filter(|m| m.start_sec > bookmark.start_sec + 1e-3)
                .min_by(|a, b| a.start_sec.total_cmp(&b.start_sec));
            let to = next.map_or(sequence_end_sec(doc), |m| m.start_sec);
            (bookmark.start_sec, to, bookmark.name.clone(), next.map_or("Sequence end".into(), |m| m.name.clone()))
        } else {
            let prev = others
                .iter()
                .filter(|m| m.start_sec < bookmark.start_sec - 1e-3)
                .max_by(|a, b| a.start_sec.total_cmp(&b.start_sec));
            (
                prev.map_or(0.0, |m| m.start_sec),
                bookmark.start_sec,
                prev.map_or("Sequence start".into(), |m| m.name.clone()),
                bookmark.name.clone(),
            )
        };
        if to - from < 1e-3 {
            return None;
        }
        let label = |n: &str| if n.trim().is_empty() { "Bookmark".to_string() } else { n.to_string() };
        let name = format!("{} \u{2192} {}", label(&from_name), label(&to_name));
        Some(self.add_range_marker(from, to - from, &name, &bookmark.color))
    }

    /// One range marker per section between bookmarks; existing markers are kept. All undo together.
    pub fn create_ranges_from_bookmarks(&mut self, include_sequence_bounds: bool) -> Vec<Marker> {
        let plan = plan_bookmark_ranges(self.doc(), include_sequence_bounds);
        self.edit(|e| {
            plan.iter().map(|r| e.add_range_marker(r.start_sec, r.end_sec - r.start_sec, &r.name, &r.color)).collect()
        })
    }

    /// Changes several marker fields at once (drags of its times join into one undo step).
    pub fn edit_marker(&mut self, marker_id: &str, edit: MarkerEdit) {
        if self.find_marker(marker_id).is_none() {
            return;
        }
        let apply = |e: &mut Self| {
            let Some(m) = marker_mut(e.doc_mut(), marker_id) else { return };
            if let Some(n) = &edit.name {
                m.name = n.clone();
            }
            if let Some(c) = &edit.color {
                m.color = c.clone();
            }
            if let Some(s) = edit.start_sec {
                m.start_sec = s.max(0.0);
            }
            if let Some(d) = edit.dur_sec {
                m.dur_sec = d.max(0.0);
            }
            if let Some(i) = edit.include_in_export {
                m.include_in_export = i;
            }
            if let Some(o) = &edit.output_name {
                m.output_name = (!o.is_empty()).then(|| o.clone());
            }
        };
        if edit.is_timing_only() {
            self.edit_coalescing(format!("marker-edit:{marker_id}"), Coalesce::Continue, apply);
        } else {
            self.edit(apply);
        }
    }

    /// Moves a marker (a clip marker stays inside its clip). Drags join into one undo step.
    pub fn move_marker(&mut self, marker_id: &str, new_sec: f64) {
        let Some(loc) = self.find_marker(marker_id) else { return };
        let t = match loc.clip_dur_sec {
            Some(d) => new_sec.clamp(0.0, d),
            None => new_sec.max(0.0),
        };
        self.edit_coalescing(format!("marker-move:{marker_id}"), Coalesce::Continue, |e| {
            if let Some(m) = marker_mut(e.doc_mut(), marker_id) {
                m.start_sec = t;
            }
        });
    }

    /// Renames or recolours a marker.
    pub fn update_marker(&mut self, marker_id: &str, name: Option<&str>, color: Option<&str>) {
        if name.is_none() && color.is_none() {
            return;
        }
        self.edit_marker(
            marker_id,
            MarkerEdit { name: name.map(Into::into), color: color.map(Into::into), ..Default::default() },
        );
    }

    /// Splits every video spanning a selected marker's (frame-aligned) timeline position; linked video and audio
    /// follow, unrelated audio and text are left alone. A range marker gives its start. One undo step. Returns how
    /// many video cuts were made.
    pub fn split_video_at_selected_markers(&mut self) -> usize {
        let mut times: Vec<f64> = self
            .selection
            .selected_marker_ids()
            .iter()
            .filter_map(|id| self.find_marker(id))
            .map(|l| l.timeline_sec())
            .filter(|t| t.is_finite())
            .map(|t| self.snap_time(t))
            .collect();
        times.sort_by(|a, b| b.total_cmp(a));
        times.dedup();
        let inside = |c: &fig_timeline::Clip, t: f64| t > c.start_sec + 1e-8 && t < c.end_sec() - 1e-8;
        // descending, so every earlier point stays in the original left half and its id holds
        let mut plan: Vec<(String, f64, Option<String>, bool)> = Vec::new();
        for &t in &times {
            let mut seen = HashSet::new();
            let videos: Vec<String> =
                self.doc().clips().filter(|c| c.as_video().is_some() && inside(c, t)).map(|c| c.id.clone()).collect();
            for vid in videos {
                if seen.contains(&vid) {
                    continue;
                }
                let video = self.find_clip(&vid).unwrap();
                let right = video.link_group_id.as_ref().filter(|g| !g.is_empty()).map(|_| new_id_n());
                for id in self.link_group(&vid) {
                    let c = self.find_clip(&id).unwrap();
                    if !matches!(c.body, ClipBody::Video(_) | ClipBody::Audio(_))
                        || !inside(c, t)
                        || !seen.insert(id.clone())
                    {
                        continue;
                    }
                    plan.push((id, t, right.clone(), matches!(c.body, ClipBody::Video(_))));
                }
            }
        }
        if plan.is_empty() {
            return 0;
        }
        let cuts = plan.iter().filter(|p| p.3).count();
        self.edit(|e| {
            for (id, t, group, _) in plan {
                e.cut_one_into(&id, t, group);
            }
        });
        cuts
    }

    // ---- beat preview -----------------------------------------------------------------------

    /// Candidate beat markers being tuned (with a sensitivity slider, say). They live outside the timeline: no undo
    /// step, no save.
    pub fn beat_preview(&self) -> Option<&BeatPreview> {
        self.beat_preview.as_ref()
    }

    /// Shows clip-local beat times on the clip, replacing any earlier preview.
    pub fn set_beat_preview(&mut self, clip_id: &str, local_times: &[f64]) {
        let Some(clip) = self.find_clip(clip_id) else { return };
        self.beat_preview =
            Some(BeatPreview { clip_id: clip_id.into(), local_times: clean_beat_times(local_times, clip.dur_sec) });
    }

    pub fn clear_beat_preview(&mut self) {
        self.beat_preview = None;
    }

    /// True while the clip's beat markers are about to be replaced by the preview, so views hide them.
    pub fn is_superseded_by_preview(&self, clip_id: &str, marker: &Marker) -> bool {
        self.beat_preview.as_ref().is_some_and(|p| p.clip_id == clip_id) && marker.is_generated_beat()
    }

    /// Turns the preview into real markers as one undoable edit, and clears it.
    pub fn commit_beat_preview(&mut self) -> Vec<Marker> {
        let Some(preview) = self.beat_preview.take() else { return Vec::new() };
        self.add_beat_markers(&preview.clip_id, &preview.local_times)
    }
}
