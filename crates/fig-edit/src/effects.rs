//! Effects on tracks (and, in older projects, on clips): adding, removing, their bars, parameters, keyframes, and
//! copying them.

use indexmap::IndexMap;

use super::history::Coalesce;
use super::{KeyframeHostRef, KeyframeRef, TimelineEditor};
use fig_fx::effects as catalog;
use fig_timeline::{EffectInstance, KeyframeEasing, ParamKind, ParamValue, Timeline, TrackKind};

/// The effect stack of a track or a clip, by its id.
pub fn owner_effects<'a>(doc: &'a Timeline, owner_id: &str) -> Option<&'a Vec<EffectInstance>> {
    match doc.find_track(owner_id) {
        Some(t) => Some(&t.effects),
        None => doc.find_clip(owner_id).map(|c| &c.effects),
    }
}

pub fn owner_effects_mut<'a>(doc: &'a mut Timeline, owner_id: &str) -> Option<&'a mut Vec<EffectInstance>> {
    if doc.find_track(owner_id).is_some() {
        return doc.find_track_mut(owner_id).map(|t| &mut t.effects);
    }
    doc.find_clip_mut(owner_id).map(|c| &mut c.effects)
}

/// Where an owner's effect time 0 is and how far its effects may go: a track's from the timeline's start without
/// limit, a clip's from its start for its length.
fn owner_span(doc: &Timeline, owner_id: &str) -> Option<(f64, f64)> {
    if doc.find_track(owner_id).is_some() {
        return Some((0.0, f64::INFINITY));
    }
    doc.find_clip(owner_id).map(|c| (c.start_sec, c.dur_sec))
}

/// An effect on the clipboard; its range and keyframes are relative to the earliest copied one.
#[derive(Clone, Debug)]
pub(crate) struct CopiedEffect {
    pub(crate) track_id: String,
    pub(crate) kind: TrackKind,
    pub(crate) effect: EffectInstance,
}

pub(crate) fn shift_keys(fx: &mut EffectInstance, by: f64) {
    for keys in fx.keyframes.values_mut() {
        for k in keys {
            k.time_sec += by;
        }
    }
}

impl TimelineEditor {
    pub fn find_effect(&self, owner_id: &str, effect_id: &str) -> Option<&EffectInstance> {
        owner_effects(self.doc(), owner_id)?.iter().find(|e| e.id == effect_id)
    }

    /// Runs an edit on one effect (joining the last one when `coalesce` matches).
    fn edit_effect(
        &mut self,
        owner_id: &str,
        effect_id: &str,
        coalesce: Option<(String, Coalesce)>,
        mutate: impl FnOnce(&mut EffectInstance),
    ) {
        if self.find_effect(owner_id, effect_id).is_none() {
            return;
        }
        let run = |e: &mut Self| {
            if let Some(fx) =
                owner_effects_mut(e.doc_mut(), owner_id).and_then(|l| l.iter_mut().find(|x| x.id == effect_id))
            {
                mutate(fx);
            }
        };
        match coalesce {
            Some((key, mode)) => self.edit_coalescing(format!("{key}|{owner_id}|{effect_id}"), mode, run),
            None => self.edit(run),
        }
    }

    /// Adds an effect at the end of a track's (or clip's) stack.
    pub fn add_effect(&mut self, owner_id: &str, mut effect: EffectInstance) -> bool {
        let Some(list) = owner_effects(self.doc(), owner_id) else { return false };
        if list.iter().any(|e| e.id == effect.id) {
            return false;
        }
        effect.order = list.iter().map(|e| e.order).max().map_or(0, |m| m + 1);
        self.edit(|e| owner_effects_mut(e.doc_mut(), owner_id).unwrap().push(effect));
        true
    }

    pub fn remove_effect(&mut self, owner_id: &str, effect_id: &str) {
        self.remove_effects(&[(owner_id.to_string(), effect_id.to_string())]);
    }

    /// Removes several effect bars as one undo step. Returns how many were removed.
    pub fn remove_effects(&mut self, items: &[(String, String)]) -> usize {
        let present: Vec<&(String, String)> = items.iter().filter(|(o, e)| self.find_effect(o, e).is_some()).collect();
        if present.is_empty() {
            return 0;
        }
        self.edit(|e| {
            for (owner, id) in &present {
                if let Some(list) = owner_effects_mut(e.doc_mut(), owner) {
                    list.retain(|x| &x.id != id);
                }
            }
        });
        present.len()
    }

    /// Limits an effect to part of its owner (or, with no start and length, puts it back on the whole). Moving a bar
    /// can carry its keyframes; resizing its edges leaves keyframe times unchanged.
    pub fn set_effect_range(
        &mut self,
        owner_id: &str,
        effect_id: &str,
        range: Option<(f64, f64)>,
        move_keyframes: bool,
    ) {
        let Some((_, limit)) = owner_span(self.doc(), owner_id) else { return };
        let range = range.map(|(s, d)| {
            const MIN_LENGTH: f64 = 0.1;
            let d = d.clamp(MIN_LENGTH.min(limit), limit);
            (s.clamp(0.0, (limit - d).max(0.0)), d)
        });
        self.edit_effect(owner_id, effect_id, Some(("range".into(), Coalesce::Continue)), |fx| {
            if move_keyframes && let Some((start, _)) = range {
                let delta = start - fx.start_sec.unwrap_or(0.0);
                for keys in fx.keyframes.values_mut() {
                    for k in keys {
                        k.time_sec = (k.time_sec + delta).clamp(0.0, limit);
                    }
                }
            }
            fx.start_sec = range.map(|r| r.0);
            fx.dur_sec = range.map(|r| r.1);
        });
    }

    /// Sets a typed parameter on an effect (slider drags join into one undo step).
    pub fn set_effect_param(&mut self, owner_id: &str, effect_id: &str, key: &str, value: ParamValue) {
        let Some(fx) = self.find_effect(owner_id, effect_id) else { return };
        if fx.params.get(key).is_none_or(|old| *old == value) {
            return;
        }
        let k = key.to_string();
        self.edit_effect(owner_id, effect_id, Some((format!("param|{key}"), Coalesce::Restart)), move |fx| {
            fx.params.insert(k, value);
        });
    }

    pub fn toggle_effect(&mut self, owner_id: &str, effect_id: &str) {
        self.edit_effect(owner_id, effect_id, None, |fx| fx.enabled = !fx.enabled);
    }

    /// Moves several effect bars together by `delta_sec` (the total from where the drag began), no bar leaving the
    /// start of time or the end of its clip. Steps of one drag share a `gesture`.
    pub fn move_effects(&mut self, items: &[(String, String)], delta_sec: f64, gesture: &str) {
        if items.is_empty() {
            return;
        }
        let ids: Vec<&str> = items.iter().map(|(_, e)| e.as_str()).collect();
        let key = format!("move-effects|{gesture}|{}", ids.join(","));
        self.edit_coalescing(key, Coalesce::Restart, |e| {
            // one shared distance, limited by every bar
            let mut delta = delta_sec;
            for (owner, id) in items {
                let (Some(fx), Some((_, limit))) = (e.find_effect(owner, id), owner_span(e.doc(), owner)) else {
                    continue;
                };
                if !fx.has_range() {
                    continue;
                }
                let (start, dur) = (fx.start_sec.unwrap_or(0.0), fx.dur_sec.unwrap_or(0.0));
                delta = delta.max(-start);
                if limit.is_finite() {
                    delta = delta.min((limit - (start + dur)).max(0.0));
                }
            }
            for (owner, id) in items {
                let Some(fx) = owner_effects_mut(e.doc_mut(), owner).and_then(|l| l.iter_mut().find(|x| &x.id == id))
                else {
                    continue;
                };
                if !fx.has_range() {
                    continue;
                }
                fx.start_sec = Some(fx.start_sec.unwrap_or(0.0) + delta);
                for keys in fx.keyframes.values_mut() {
                    for k in keys {
                        k.time_sec = (k.time_sec + delta).max(0.0);
                    }
                }
            }
        });
    }

    // ---- effect keyframes -------------------------------------------------------------------

    /// Sets (or adds) the keyframe of an effect at a time for every given parameter at once. Dragging a value joins
    /// into one undo step.
    pub fn set_effect_keyframe(
        &mut self,
        owner_id: &str,
        effect_id: &str,
        time_sec: f64,
        values: &IndexMap<String, f64>,
        easing: Option<KeyframeEasing>,
    ) {
        let Some(type_id) = self.find_effect(owner_id, effect_id).map(|f| f.type_id.clone()) else { return };
        let gesture = format!("kf|{owner_id}|{effect_id}|{}", (time_sec * 1e4).round() / 1e4);
        self.edit_effect(owner_id, effect_id, Some((gesture, Coalesce::Continue)), |fx| {
            for (key, &number) in values {
                let value = catalog::to_value(&type_id, key, number);
                let track = fx.keyframes.entry(key.clone()).or_default();
                match track.iter().position(|p| (p.time_sec - time_sec).abs() < 1e-6) {
                    Some(i) => {
                        track[i].value = value;
                        if let Some(e) = easing {
                            track[i].easing = e;
                        }
                    }
                    None => {
                        let e = easing.unwrap_or(KeyframeEasing::EaseInOut);
                        track.push(fig_timeline::KeyframePoint::eased(time_sec, value, e));
                        track.sort_by(|a, b| a.time_sec.total_cmp(&b.time_sec));
                    }
                }
            }
        });
    }

    /// Adds a keyframe at a time holding the effect's current look there. Returns the time keyed.
    pub fn add_effect_keyframe(&mut self, owner_id: &str, effect_id: &str, local_sec: f64) -> Option<f64> {
        self.add_keyframe(&KeyframeHostRef::of_effect(owner_id, effect_id), local_sec)
    }

    /// Records a keyframe holding the effect's current look (the stopwatch). Only ever adds.
    pub fn record_effect_keyframe(&mut self, owner_id: &str, effect_id: &str, local_sec: f64) -> Option<f64> {
        self.record_keyframe(&KeyframeHostRef::of_effect(owner_id, effect_id), local_sec)
    }

    /// Turns animation on or off. On: a keyframe at the time holds the current look. Off: every keyframe is removed
    /// and the look at that time becomes the constant value.
    pub fn set_effect_animated(&mut self, owner_id: &str, effect_id: &str, animated: bool, local_sec: f64) {
        let Some((_, limit)) = owner_span(self.doc(), owner_id) else { return };
        let time = local_sec.clamp(0.0, limit);
        self.edit_effect(owner_id, effect_id, None, |fx| {
            let current = catalog::current_numeric_values(fx, time);
            let entry = catalog::find(&fx.type_id);
            let kind =
                |key: &str| entry.as_ref().and_then(|e| e.param(key).map(|p| p.kind)).unwrap_or(ParamKind::Double);
            let typed = |key: &str, n: f64| {
                if kind(key) == ParamKind::Int {
                    ParamValue::Int(n.round_ties_even() as i32)
                } else {
                    ParamValue::Double(n)
                }
            };
            if animated {
                if fx.keyframes.is_empty() {
                    for (key, n) in current {
                        let v = typed(&key, n);
                        fx.keyframes
                            .insert(key, vec![fig_timeline::KeyframePoint::eased(time, v, KeyframeEasing::EaseInOut)]);
                    }
                }
                return;
            }
            fx.keyframes.clear();
            for (key, n) in current {
                let v = typed(&key, n);
                fx.params.insert(key, v);
            }
        });
    }

    /// Copies the keyframe at one time to another (same values and easing).
    pub fn duplicate_effect_keyframe(&mut self, owner_id: &str, effect_id: &str, from_sec: f64, to_sec: f64) {
        let host = KeyframeHostRef::of_effect(owner_id, effect_id);
        self.duplicate_keyframes(&KeyframeRef { host, time_sec: from_sec }, to_sec);
    }

    /// Removes the keyframe at a time from every parameter of an effect.
    pub fn remove_effect_keyframe(&mut self, owner_id: &str, effect_id: &str, time_sec: f64) {
        let host = KeyframeHostRef::of_effect(owner_id, effect_id);
        self.remove_keyframes(&KeyframeRef { host, time_sec });
    }

    /// Moves a keyframe on every parameter of an effect. Steps of one drag share a `gesture`.
    pub fn move_effect_keyframe(&mut self, owner_id: &str, effect_id: &str, from_sec: f64, to_sec: f64, gesture: &str) {
        let host = KeyframeHostRef::of_effect(owner_id, effect_id);
        self.move_keyframes(&KeyframeRef { host, time_sec: from_sec }, to_sec, gesture);
    }

    pub fn set_effect_keyframe_easing(
        &mut self,
        owner_id: &str,
        effect_id: &str,
        time_sec: f64,
        easing: KeyframeEasing,
    ) {
        let host = KeyframeHostRef::of_effect(owner_id, effect_id);
        self.set_keyframe_easing(&KeyframeRef { host, time_sec }, easing);
    }

    // ---- copy and paste ---------------------------------------------------------------------

    /// True when the last copy was of effects, not clips: pasting puts them on tracks at the playhead.
    pub fn clipboard_holds_effects(&self) -> bool {
        self.clipboard.holds_effects
    }

    pub fn has_effect_clipboard(&self) -> bool {
        !self.clipboard.effects.is_empty()
    }

    /// Copies the selected effect bars, keeping their spacing (relative to the earliest). Returns how many.
    pub fn copy_selected_effects(&mut self) -> usize {
        let mut picked = self.selected_effects_in_timeline_time(&[]);
        if picked.is_empty() {
            return 0;
        }
        let anchor = picked
            .iter()
            .filter(|p| p.effect.has_range())
            .filter_map(|p| p.effect.start_sec)
            .fold(f64::INFINITY, f64::min);
        let anchor = if anchor.is_finite() { anchor } else { 0.0 };
        for p in &mut picked {
            p.effect.start_sec = p.effect.start_sec.map(|s| s - anchor);
            shift_keys(&mut p.effect, -anchor);
        }
        let count = picked.len();
        self.clipboard.clips.clear();
        self.clipboard.effects = picked;
        self.clipboard.holds_effects = true;
        count
    }

    /// The selected effect bars as copies placed on their tracks, in timeline seconds (a whole-clip effect becomes a
    /// region over its clip).
    pub(crate) fn selected_effects_in_timeline_time(&self, except_owners: &[String]) -> Vec<CopiedEffect> {
        let mut picked: Vec<CopiedEffect> = Vec::new();
        for (owner_id, effect_id) in self.selection.selected_effects() {
            if except_owners.contains(&owner_id) {
                continue;
            }
            let Some(fx) = self.find_effect(&owner_id, &effect_id) else { continue };
            let on_track = self.find_track(&owner_id).is_some();
            // an older project's effect on a clip: its track, in timeline seconds
            let Some(track) = (if on_track { self.find_track(&owner_id) } else { self.find_clip_track(&owner_id) })
            else {
                continue;
            };
            let (shift, owner_dur) = if on_track {
                (0.0, 0.0)
            } else {
                let c = self.find_clip(&owner_id).unwrap();
                (c.start_sec, c.dur_sec)
            };
            let mut copy = fx.duplicate();
            if fx.has_range() {
                copy.start_sec = Some(fx.start_sec.unwrap() + shift);
            } else if !on_track {
                // a whole-clip effect becomes a region over that clip
                copy.start_sec = Some(shift);
                copy.dur_sec = Some(owner_dur);
            }
            shift_keys(&mut copy, shift);
            picked.push(CopiedEffect { track_id: track.id.clone(), kind: track.kind, effect: copy });
        }
        picked
    }

    /// Pastes the copied effects so the earliest starts at `at_sec`, keeping their spacing. Each goes back on the
    /// track it came from (or `target_track_id`, when they all came from one track and it is the same kind). One undo
    /// step. The pasted effects become the selection; returns their (track id, effect id).
    pub fn paste_effects(&mut self, at_sec: f64, target_track_id: Option<&str>) -> Vec<(String, String)> {
        if self.clipboard.effects.is_empty() {
            return Vec::new();
        }
        let at = at_sec.max(0.0);
        let one_track = self.clipboard.effects.iter().all(|c| c.track_id == self.clipboard.effects[0].track_id);
        let target = target_track_id.and_then(|id| self.find_track(id)).map(|t| (t.id.clone(), t.kind));
        let copies = self.clipboard.effects.clone();
        let added: Vec<(String, String)> = self.edit(|e| {
            let mut added = Vec::new();
            for copied in copies {
                let track = match &target {
                    Some((id, kind)) if one_track && *kind == copied.kind => Some(id.clone()),
                    _ => e
                        .find_track(&copied.track_id)
                        .or_else(|| e.doc().tracks.iter().find(|t| t.kind == copied.kind))
                        .map(|t| t.id.clone()),
                };
                let Some(track) = track else { continue };
                let mut fx = copied.effect.duplicate();
                if fx.has_range() {
                    fx.start_sec = fx.start_sec.map(|s| s + at);
                }
                shift_keys(&mut fx, at);
                let id = fx.id.clone();
                if e.add_effect(&track, fx) {
                    added.push((track, id));
                }
            }
            added
        });
        if !added.is_empty() {
            self.selection.select_effects(added.clone(), false);
        }
        added
    }
}
