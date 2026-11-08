//! What is selected, as one ordered list of keys. Clips, tracks, markers, transitions and effect bars are all the
//! same kind of entry, and one selection can hold any mix of them. A plain pick replaces everything; Ctrl/Shift
//! picks add to it. The clip/marker/effect members are views over that list for the callers that only care about
//! one group.

use fig_timeline::{SelectionGroup, SelectionKey};

use super::KeyframeRef;

#[derive(Clone, Debug, Default)]
pub struct SelectionState {
    items: Vec<SelectionKey>,
    marker_anchor_id: Option<String>,
    keyframe: Option<KeyframeRef>,
    /// Bumped on every change, so a view can tell when to redraw.
    revision: u64,
}

impl SelectionState {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn revision(&self) -> u64 {
        self.revision
    }

    fn changed(&mut self) {
        self.revision += 1;
    }

    /// The keyframe diamond picked on the timeline, of any kind; its owner (clip or effect bar) is the selection.
    /// Any other change to the selection drops it.
    pub fn keyframe(&self) -> Option<&KeyframeRef> {
        self.keyframe.as_ref()
    }

    /// Selects a keyframe diamond, and its owner with it.
    pub fn select_keyframe(&mut self, owner: SelectionKey, keyframe: KeyframeRef) {
        self.items.clear();
        self.items.push(owner);
        self.keyframe = Some(keyframe);
        self.changed();
    }

    /// The selected diamond moved (dragged): keep it selected at its new time.
    pub fn move_keyframe(&mut self, time_sec: f64) {
        if let Some(k) = &mut self.keyframe {
            k.time_sec = time_sec;
        }
    }

    /// Drops the selected diamond (it was deleted), keeping its owner selected.
    pub fn clear_keyframe(&mut self) {
        if self.keyframe.take().is_some() {
            self.changed();
        }
    }

    // ---- the uniform API ------------------------------------------------------------------

    /// Everything selected, in the order it was picked.
    pub fn items(&self) -> &[SelectionKey] {
        &self.items
    }

    /// The last item picked: what a single-item panel shows.
    pub fn primary(&self) -> Option<&SelectionKey> {
        self.items.last()
    }

    pub fn is_empty(&self) -> bool {
        self.items.is_empty()
    }

    pub fn contains(&self, key: &SelectionKey) -> bool {
        self.items.contains(key)
    }

    /// Selects exactly this item.
    pub fn replace(&mut self, key: SelectionKey) {
        self.replace_all([key]);
    }

    /// Selects exactly these items (of any groups).
    pub fn replace_all(&mut self, keys: impl IntoIterator<Item = SelectionKey>) {
        let mut next: Vec<SelectionKey> = Vec::new();
        for key in keys {
            if !next.contains(&key) {
                next.push(key);
            }
        }
        if self.items == next {
            return;
        }
        self.items = next;
        self.keyframe = None;
        self.changed();
    }

    /// Adds an item to the selection, whatever else is selected.
    pub fn add(&mut self, key: SelectionKey) {
        if self.items.contains(&key) {
            return;
        }
        self.items.push(key);
        self.keyframe = None;
        self.changed();
    }

    /// Takes an item out of the selection.
    pub fn remove(&mut self, key: &SelectionKey) {
        if let Some(i) = self.items.iter().position(|k| k == key) {
            self.items.remove(i);
            self.keyframe = None;
            self.changed();
        }
    }

    /// Adds the item, or takes it out when it is already selected.
    pub fn toggle(&mut self, key: SelectionKey) {
        if self.items.contains(&key) { self.remove(&key) } else { self.add(key) }
    }

    /// Makes `keys` the selected items of `group`, leaving the other groups selected as they are (Ctrl-clicking a
    /// clip while effect bars are selected keeps the bars).
    pub fn set_group(&mut self, group: SelectionGroup, keys: impl IntoIterator<Item = SelectionKey>) {
        let others: Vec<SelectionKey> = self.items.iter().filter(|k| k.group != group).cloned().collect();
        self.replace_all(others.into_iter().chain(keys.into_iter().filter(|k| k.group == group)));
    }

    pub fn clear(&mut self) {
        if self.items.is_empty() && self.keyframe.is_none() {
            return;
        }
        self.items.clear();
        self.marker_anchor_id = None;
        self.keyframe = None;
        self.changed();
    }

    fn of(&self, group: SelectionGroup) -> impl Iterator<Item = &SelectionKey> {
        self.items.iter().filter(move |k| k.group == group)
    }

    // ---- clips ----------------------------------------------------------------------------

    pub fn selected_clip_ids(&self) -> Vec<String> {
        self.of(SelectionGroup::Clip).map(|k| k.id.clone()).collect()
    }

    pub fn is_selected(&self, clip_id: &str) -> bool {
        self.of(SelectionGroup::Clip).any(|k| k.id == clip_id)
    }

    /// How many clips are selected.
    pub fn clip_count(&self) -> usize {
        self.of(SelectionGroup::Clip).count()
    }

    pub fn select(&mut self, clip_id: &str) {
        self.add(SelectionKey::new(SelectionGroup::Clip, clip_id));
    }

    pub fn deselect(&mut self, clip_id: &str) {
        self.remove(&SelectionKey::new(SelectionGroup::Clip, clip_id));
    }

    pub fn select_only(&mut self, clip_id: &str) {
        self.replace(SelectionKey::new(SelectionGroup::Clip, clip_id));
    }

    /// Replaces the selection with these clips in one change.
    pub fn select_clips<S: AsRef<str>>(&mut self, clip_ids: impl IntoIterator<Item = S>) {
        let keys = clip_keys(clip_ids);
        self.replace_all(keys);
    }

    /// Makes these the selected clips, keeping whatever else is selected (a Ctrl/Shift pick).
    pub fn set_clips<S: AsRef<str>>(&mut self, clip_ids: impl IntoIterator<Item = S>) {
        let keys = clip_keys(clip_ids);
        self.set_group(SelectionGroup::Clip, keys);
    }

    // ---- tracks ---------------------------------------------------------------------------

    /// The selected track, if a track (rather than something on it) is what is selected.
    pub fn active_track_id(&self) -> Option<&str> {
        self.of(SelectionGroup::Track).map(|k| k.id.as_str()).next()
    }

    pub fn set_active_track_id(&mut self, id: Option<&str>) {
        match id {
            Some(id) => self.replace(SelectionKey::new(SelectionGroup::Track, id)),
            None => {
                if let Some(current) = self.active_track_id().map(str::to_string) {
                    self.remove(&SelectionKey::new(SelectionGroup::Track, current));
                }
            }
        }
    }

    // ---- transitions ----------------------------------------------------------------------

    /// The selected cut transition ("{leftClipId}|{rightClipId}").
    pub fn selected_transition_key(&self) -> Option<&str> {
        self.of(SelectionGroup::Transition).map(|k| k.id.as_str()).next()
    }

    pub fn set_selected_transition_key(&mut self, key: Option<&str>) {
        match key {
            Some(key) => self.select_transition(key),
            None => {
                if let Some(current) = self.selected_transition_key().map(str::to_string) {
                    self.remove(&SelectionKey::new(SelectionGroup::Transition, current));
                }
            }
        }
    }

    pub fn select_transition(&mut self, key: &str) {
        self.replace(SelectionKey::new(SelectionGroup::Transition, key));
    }

    // ---- markers --------------------------------------------------------------------------

    pub fn selected_marker_ids(&self) -> Vec<String> {
        self.of(SelectionGroup::Marker).map(|k| k.id.clone()).collect()
    }

    pub fn is_marker_selected(&self, id: &str) -> bool {
        self.of(SelectionGroup::Marker).any(|k| k.id == id)
    }

    /// The primary marker for the inspector; `selected_marker_ids` holds the full selection.
    pub fn selected_marker_id(&self) -> Option<&str> {
        self.of(SelectionGroup::Marker).map(|k| k.id.as_str()).last()
    }

    pub fn set_selected_marker_id(&mut self, id: Option<&str>) {
        match id {
            Some(id) => self.select_marker(id),
            None => {
                self.marker_anchor_id = None;
                self.set_group(SelectionGroup::Marker, []);
            }
        }
    }

    pub fn select_marker(&mut self, marker_id: &str) {
        self.select_markers([marker_id], Some(marker_id));
    }

    pub fn select_markers<S: AsRef<str>>(&mut self, marker_ids: impl IntoIterator<Item = S>, anchor_id: Option<&str>) {
        let mut ids: Vec<String> = Vec::new();
        for id in marker_ids {
            let id = id.as_ref();
            if !id.is_empty() && !ids.iter().any(|i| i == id) {
                ids.push(id.into());
            }
        }
        self.marker_anchor_id = match anchor_id {
            Some(a) => Some(a.into()),
            None => match &self.marker_anchor_id {
                Some(a) if ids.contains(a) => Some(a.clone()),
                _ => ids.first().cloned(),
            },
        };
        self.replace_all(ids.into_iter().map(|id| SelectionKey::new(SelectionGroup::Marker, id)));
    }

    /// Adds the marker to the selection, or takes it out (anything else selected stays).
    pub fn toggle_marker(&mut self, marker_id: &str) {
        self.toggle(SelectionKey::new(SelectionGroup::Marker, marker_id));
        // Shift-click ranges keep measuring from the anchor while it is still selected
        let selected = self.selected_marker_ids();
        if !self.marker_anchor_id.as_ref().is_some_and(|a| selected.contains(a)) {
            self.marker_anchor_id = selected.first().cloned();
        }
    }

    /// Selects the markers from the anchor to `marker_id`, in timeline order (`ordered_ids`).
    pub fn select_marker_range(&mut self, marker_id: &str, ordered_ids: &[String], additive: bool) {
        let end = ordered_ids.iter().position(|i| i == marker_id);
        let start = self.marker_anchor_id.as_ref().and_then(|a| ordered_ids.iter().position(|i| i == a));
        let (Some(start), Some(end)) = (start, end) else {
            self.select_marker(marker_id);
            return;
        };
        let range = &ordered_ids[start.min(end)..=start.max(end)];
        if !additive {
            let anchor = self.marker_anchor_id.clone();
            self.select_markers(range, anchor.as_deref());
            return;
        }
        let mut chosen = self.selected_marker_ids();
        for id in range {
            if !chosen.contains(id) {
                chosen.push(id.clone());
            }
        }
        chosen.sort_by_key(|id| ordered_ids.iter().position(|i| i == id).unwrap_or(usize::MAX));
        self.set_group(
            SelectionGroup::Marker,
            chosen.into_iter().map(|id| SelectionKey::new(SelectionGroup::Marker, id)),
        );
    }

    // ---- effect bars ----------------------------------------------------------------------

    /// The effect bars that are selected (owner id, effect id), in the order they were picked.
    pub fn selected_effects(&self) -> Vec<(String, String)> {
        self.of(SelectionGroup::Effect).map(|k| (k.owner_id.clone().unwrap_or_default(), k.id.clone())).collect()
    }

    /// The primary effect (the last one picked): what the inspector shows.
    pub fn selected_effect(&self) -> Option<(String, String)> {
        self.of(SelectionGroup::Effect).last().map(|k| (k.owner_id.clone().unwrap_or_default(), k.id.clone()))
    }

    pub fn is_effect_selected(&self, effect_id: &str) -> bool {
        self.of(SelectionGroup::Effect).any(|k| k.id == effect_id)
    }

    pub fn select_effect(&mut self, owner_id: &str, effect_id: &str) {
        self.select_effects([(owner_id.to_string(), effect_id.to_string())], false);
    }

    /// Selects exactly these effect bars, or adds them to whatever is selected.
    pub fn select_effects(&mut self, effects: impl IntoIterator<Item = (String, String)>, additive: bool) {
        let keys = effects.into_iter().map(|(owner, id)| SelectionKey::owned(SelectionGroup::Effect, id, owner));
        if additive {
            let current = self.items.clone();
            self.replace_all(current.into_iter().chain(keys));
        } else {
            self.replace_all(keys);
        }
    }

    pub fn toggle_effect(&mut self, owner_id: &str, effect_id: &str) {
        self.toggle(SelectionKey::owned(SelectionGroup::Effect, effect_id, owner_id));
    }

    /// Drops keys whose things no longer exist (after an undo took them away).
    pub fn retain(&mut self, mut exists: impl FnMut(&SelectionKey) -> bool) {
        let before = self.items.len();
        self.items.retain(|k| exists(k));
        if self.items.len() != before {
            self.keyframe = None;
            self.changed();
        }
    }
}

fn clip_keys<S: AsRef<str>>(ids: impl IntoIterator<Item = S>) -> Vec<SelectionKey> {
    ids.into_iter()
        .filter(|id| !id.as_ref().is_empty())
        .map(|id| SelectionKey::new(SelectionGroup::Clip, id.as_ref()))
        .collect()
}
