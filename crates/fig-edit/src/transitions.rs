//! Transitions across cuts: applying, resizing, editing and removing them.

use super::TimelineEditor;
use super::history::Coalesce;
use fig_timeline::{
    Clip, ClipBody, Facet, ParamValue, Selectable, SelectionGroup, SelectionKey, Timeline, TransitionRef,
};

/// A transition across the cut between two abutting clips. Its selection key is "{left id}|{right id}".
#[derive(Clone, Debug, PartialEq)]
pub struct CutTransition {
    pub left_clip_id: String,
    pub right_clip_id: String,
    pub type_id: String,
    pub duration_sec: f64,
    pub cut_sec: f64,
}

impl CutTransition {
    pub fn key(&self) -> String {
        format!("{}|{}", self.left_clip_id, self.right_clip_id)
    }
}

impl Selectable for CutTransition {
    fn key(&self) -> SelectionKey {
        SelectionKey::new(SelectionGroup::Transition, CutTransition::key(self))
    }

    fn display_name(&self) -> &'static str {
        "Transition"
    }

    fn facets(&self) -> Facet {
        Facet::TRANSITION
    }
}

/// True for clips a transition can join: video, still images and titles, in any mix. A drawn layer holds its
/// picture for as long as the transition needs, so only the clips' lengths limit it.
pub fn can_transition(clip: &Clip) -> bool {
    !matches!(clip.body, ClipBody::Audio(_))
}

fn cut_of(left: &Clip, right: &Clip) -> Option<CutTransition> {
    let type_id = left.transition_out.as_ref().or(right.transition_in.as_ref())?.type_id.clone();
    let dur = left
        .transition_out
        .as_ref()
        .map_or(0.0, |t| t.duration_sec)
        .max(right.transition_in.as_ref().map_or(0.0, |t| t.duration_sec));
    Some(CutTransition {
        left_clip_id: left.id.clone(),
        right_clip_id: right.id.clone(),
        type_id,
        duration_sec: dur,
        cut_sec: left.end_sec(),
    })
}

/// The transition a key ("{left}|{right}") names, if both clips are there and it exists.
pub fn find_cut(doc: &Timeline, key: &str) -> Option<CutTransition> {
    let (l, r) = key.split_once('|')?;
    cut_of(doc.find_clip(l)?, doc.find_clip(r)?)
}

/// The transitions across abutting cuts on a track, in time order.
pub fn transitions_on(doc: &Timeline, track_id: &str) -> Vec<CutTransition> {
    let Some(track) = doc.find_track(track_id) else { return Vec::new() };
    let mut sorted: Vec<&Clip> = track.clips.iter().collect();
    sorted.sort_by(|a, b| a.start_sec.total_cmp(&b.start_sec));
    sorted
        .windows(2)
        .filter(|p| (p[1].start_sec - p[0].end_sec()).abs() <= 1e-3)
        .filter_map(|p| cut_of(p[0], p[1]))
        .collect()
}

/// The longest a transition across these clips may be: twice the shorter clip. Rendering samples real source
/// handles when there are some and holds the nearest frame when there aren't.
fn clamp_duration(left: &Clip, right: &Clip, requested: f64) -> f64 {
    requested.clamp(0.0, 2.0 * left.dur_sec.min(right.dur_sec))
}

impl TimelineEditor {
    pub fn get_transition(&self, key: &str) -> Option<CutTransition> {
        find_cut(self.doc(), key)
    }

    pub fn transitions_on(&self, track_id: &str) -> Vec<CutTransition> {
        transitions_on(self.doc(), track_id)
    }

    /// Writes both edges of a cut.
    fn edit_cut(&mut self, left: &str, right: &str, key: Option<String>, change: impl FnOnce(&mut Clip, &mut Clip)) {
        let run = |e: &mut Self| {
            let doc = e.doc_mut();
            let (Some((lt, lc)), Some((rt, rc))) = (doc.locate_clip(left), doc.locate_clip(right)) else { return };
            let mut l = doc.tracks[lt].clips[lc].clone();
            let mut r = doc.tracks[rt].clips[rc].clone();
            change(&mut l, &mut r);
            doc.tracks[lt].clips[lc] = l;
            doc.tracks[rt].clips[rc] = r;
        };
        match key {
            Some(key) => self.edit_coalescing(key, Coalesce::Restart, run),
            None => self.edit(run),
        }
    }

    /// Applies a catalog transition to the cut between two abutting clips (video, image and title clips only). A
    /// transition longer than the clips allow is shortened; the message says so.
    pub fn apply_transition_at_cut(&mut self, outgoing: &str, incoming: &str, transition: &TransitionRef) -> bool {
        let (Some(left), Some(right)) = (self.find_clip(outgoing), self.find_clip(incoming)) else { return false };
        if !can_transition(left) || !can_transition(right) {
            return false;
        }
        let requested = transition.duration_sec;
        let duration = clamp_duration(left, right, requested);
        if duration < 2.0 / self.doc().rate.fps().max(1.0) {
            self.transition_validation_message = Some("The neighboring clips are too short for a transition.".into());
            return false;
        }
        let tx = TransitionRef { duration_sec: duration, ..transition.clone() };
        self.edit_cut(outgoing, incoming, None, |l, r| {
            l.transition_out = Some(tx.clone());
            r.transition_in = Some(tx);
        });
        self.transition_validation_message =
            (duration + 1e-6 < requested).then(|| "Transition duration was limited by neighboring clip length.".into());
        true
    }

    /// Puts a transition on the cut after a clip, when the next clip on its track abuts it.
    pub fn apply_transition_after(&mut self, clip_id: &str, transition: &TransitionRef) -> bool {
        let (Some(clip), Some(track)) = (self.find_clip(clip_id), self.find_clip_track(clip_id)) else { return false };
        if !can_transition(clip) {
            return false;
        }
        let cut = clip.end_sec();
        let next = track.clips.iter().find(|o| o.id != clip.id && (o.start_sec - cut).abs() < 1e-3);
        match next {
            Some(next) if can_transition(next) => {
                let next = next.id.clone();
                self.apply_transition_at_cut(clip_id, &next, transition)
            }
            Some(_) => false,
            None => {
                self.transition_validation_message =
                    Some("Transitions need a video, image or title clip right after this one.".into());
                false
            }
        }
    }

    /// Sets a parameter of the transition across a cut (both edges). Slider drags join into one step.
    pub fn set_transition_param(&mut self, left: &str, right: &str, key: &str, value: ParamValue) {
        let (Some(l), Some(r)) = (self.find_clip(left), self.find_clip(right)) else { return };
        let Some(any) = l.transition_out.as_ref().or(r.transition_in.as_ref()) else { return };
        if any.params.get(key).copied().unwrap_or_default() == value {
            return;
        }
        let k = key.to_string();
        self.edit_cut(left, right, Some(format!("txparam:{left}|{right}|{key}")), move |l, r| {
            for edge in [l.transition_out.as_mut(), r.transition_in.as_mut()].into_iter().flatten() {
                edge.params.insert(k.clone(), value);
            }
        });
    }

    /// Resizes a cut transition (drags join into one step).
    pub fn set_transition_duration(&mut self, left: &str, right: &str, duration_sec: f64) {
        let (Some(l), Some(r)) = (self.find_clip(left), self.find_clip(right)) else { return };
        if l.transition_out.is_none() && r.transition_in.is_none() {
            return;
        }
        let duration =
            if can_transition(l) && can_transition(r) { clamp_duration(l, r, duration_sec) } else { duration_sec };
        if duration <= 1e-6 {
            return;
        }
        self.edit_cut(left, right, Some(format!("txdur:{left}|{right}")), |l, r| {
            for edge in [l.transition_out.as_mut(), r.transition_in.as_mut()].into_iter().flatten() {
                edge.duration_sec = duration;
            }
        });
    }

    /// The longest the transition across two clips may be.
    pub fn max_transition_duration(&self, left: &str, right: &str) -> f64 {
        match (self.find_clip(left), self.find_clip(right)) {
            (Some(l), Some(r)) if can_transition(l) && can_transition(r) => clamp_duration(l, r, f64::MAX),
            _ => 0.0,
        }
    }

    /// Removes the transition at a cut, and its selection when it was selected.
    pub fn remove_transition(&mut self, left: &str, right: &str) {
        let (Some(l), Some(r)) = (self.find_clip(left), self.find_clip(right)) else { return };
        if l.transition_out.is_none() && r.transition_in.is_none() {
            return;
        }
        self.edit_cut(left, right, None, |l, r| {
            l.transition_out = None;
            r.transition_in = None;
        });
        if self.selection.selected_transition_key() == Some(format!("{left}|{right}").as_str()) {
            self.selection.set_selected_transition_key(None);
        }
    }

    pub fn remove_selected_transition(&mut self) {
        let Some(key) = self.selection.selected_transition_key().map(str::to_string) else { return };
        match self.get_transition(&key) {
            Some(t) => self.remove_transition(&t.left_clip_id, &t.right_clip_id),
            None => self.selection.set_selected_transition_key(None),
        }
    }
}
