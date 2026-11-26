//! Every keyframe edit, for every kind of keyframe: a clip's own (opacity, volume, crop, text size...), an effect's,
//! and a clip's speed curve. Each names its keys with a [`KeyframeHostRef`], so moving, copying, easing and deleting
//! work the same everywhere. Times are seconds from the host's start ([`KeyframeHost::origin_sec`]).

use indexmap::IndexMap;

use super::TimelineEditor;
use super::history::Coalesce;
use fig_fx::effects;
use fig_timeline::{
    Clip, KeyframeEasing, KeyframeMap, KeyframePoint, ParamValue, Timeline, automation, automation_keys, clip_time,
    properties::ClipProperty,
};

/// Names a set of keyframes, wherever they live: a clip's own keys, an effect's keys on its track or clip, or a
/// clip's speed curve (drawn as its own line).
#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub struct KeyframeHostRef {
    pub owner_id: String,
    pub effect_id: Option<String>,
    pub lane: Option<String>,
}

impl KeyframeHostRef {
    pub const SPEED_LANE: &str = "speed";

    pub fn of_clip(clip_id: &str) -> Self {
        KeyframeHostRef { owner_id: clip_id.into(), effect_id: None, lane: None }
    }

    pub fn of_effect(owner_id: &str, effect_id: &str) -> Self {
        KeyframeHostRef { owner_id: owner_id.into(), effect_id: Some(effect_id.into()), lane: None }
    }

    pub fn of_speed(clip_id: &str) -> Self {
        KeyframeHostRef { owner_id: clip_id.into(), effect_id: None, lane: Some(Self::SPEED_LANE.into()) }
    }

    /// Which of a clip's hosts a property belongs to: its speed curve, or its own keys.
    pub fn of_clip_property(clip_id: &str, key: &str) -> Self {
        if key == automation_keys::SPEED { Self::of_speed(clip_id) } else { Self::of_clip(clip_id) }
    }
}

/// One diamond: every key a host has at one time (seconds from the host's start).
#[derive(Clone, Debug, PartialEq)]
pub struct KeyframeRef {
    pub host: KeyframeHostRef,
    pub time_sec: f64,
}

#[derive(Clone, Debug, PartialEq)]
enum HostKind {
    Clip,
    Speed,
    Effect { type_id: String },
}

/// What is particular about where a set of keyframes lives: where its time 0 is on the timeline, how far its keys may
/// go, what a key's value may be, and how new keys ease.
#[derive(Clone, Debug, PartialEq)]
pub struct KeyframeHost {
    pub reference: KeyframeHostRef,
    kind: HostKind,
    /// Where this host's time 0 is on the timeline: its clip's start, or 0 for a track effect.
    pub origin_sec: f64,
    /// How far its keys may go: its clip's length, or no limit for a track effect.
    pub limit_sec: f64,
    /// How a key added with the current look eases into the next.
    pub new_key_easing: KeyframeEasing,
}

impl KeyframeHost {
    /// Finds the host a reference names, if it still exists.
    pub fn find(doc: &Timeline, r: &KeyframeHostRef) -> Option<KeyframeHost> {
        if let Some(effect_id) = &r.effect_id {
            let (origin, limit, fx) = if let Some(track) = doc.find_track(&r.owner_id) {
                (0.0, f64::INFINITY, track.effects.iter().find(|e| &e.id == effect_id)?)
            } else {
                let clip = doc.find_clip(&r.owner_id)?;
                (clip.start_sec, clip.dur_sec, clip.effects.iter().find(|e| &e.id == effect_id)?)
            };
            return Some(KeyframeHost {
                reference: r.clone(),
                kind: HostKind::Effect { type_id: fx.type_id.clone() },
                origin_sec: origin,
                limit_sec: limit,
                new_key_easing: KeyframeEasing::EaseInOut,
            });
        }
        let clip = doc.find_clip(&r.owner_id)?;
        let speed = r.lane.as_deref() == Some(KeyframeHostRef::SPEED_LANE);
        Some(KeyframeHost {
            reference: r.clone(),
            kind: if speed { HostKind::Speed } else { HostKind::Clip },
            origin_sec: clip.start_sec,
            limit_sec: clip.dur_sec,
            new_key_easing: KeyframeEasing::Linear,
        })
    }

    /// Whether a keyframe list belongs to this host (a clip's store holds both its own keys and its speed curve).
    pub fn includes(&self, key: &str) -> bool {
        match self.kind {
            HostKind::Clip => key != automation_keys::SPEED,
            HostKind::Speed => key == automation_keys::SPEED,
            HostKind::Effect { .. } => true,
        }
    }

    /// A key's value as stored: a speed held to 0.1–8×, an integer parameter rounded.
    pub fn to_value(&self, key: &str, value: f64) -> ParamValue {
        match &self.kind {
            HostKind::Speed => ParamValue::Double(value.clamp(clip_time::MIN_SPEED, clip_time::MAX_SPEED)),
            HostKind::Effect { type_id } => effects::to_value(type_id, key, value),
            HostKind::Clip => ParamValue::Double(value),
        }
    }

    /// The keyframe lists this host owns.
    pub fn store<'a>(&self, doc: &'a Timeline) -> Option<&'a KeyframeMap> {
        store(doc, &self.reference)
    }

    /// Its keyframe lists, by name.
    pub fn tracks<'a>(&'a self, doc: &'a Timeline) -> impl Iterator<Item = (&'a String, &'a Vec<KeyframePoint>)> {
        self.store(doc).into_iter().flat_map(|s| s.iter()).filter(|(k, _)| self.includes(k))
    }

    /// Every time with a key, in order: one per diamond.
    pub fn times(&self, doc: &Timeline) -> Vec<f64> {
        let mut times: Vec<f64> =
            self.tracks(doc).flat_map(|(_, keys)| keys.iter().map(|k| (k.time_sec * 1e6).round() / 1e6)).collect();
        times.sort_by(f64::total_cmp);
        times.dedup();
        times
    }

    pub fn has_key_at(&self, doc: &Timeline, time_sec: f64) -> bool {
        self.tracks(doc).any(|(_, keys)| keys.iter().any(|k| (k.time_sec - time_sec).abs() < 1e-3))
    }

    /// The easing of the keys at a time (the first one there).
    pub fn easing_at(&self, doc: &Timeline, time_sec: f64) -> Option<KeyframeEasing> {
        self.tracks(doc)
            .flat_map(|(_, keys)| keys.iter())
            .find(|k| (k.time_sec - time_sec).abs() < 1e-3)
            .map(|k| k.easing)
    }

    /// What every keyable property of the host is at a moment: the values a new "current look" key holds.
    pub fn current_values(&self, doc: &Timeline, time_sec: f64) -> IndexMap<String, f64> {
        match &self.kind {
            HostKind::Effect { .. } => {
                let fx = effect(doc, &self.reference);
                fx.map(|fx| effects::current_numeric_values(fx, time_sec)).unwrap_or_default()
            }
            HostKind::Speed => doc
                .find_clip(&self.reference.owner_id)
                .map(|c| IndexMap::from([(automation_keys::SPEED.to_string(), clip_time::speed_at(c, time_sec))]))
                .unwrap_or_default(),
            HostKind::Clip => self
                .tracks(doc)
                .filter(|(_, keys)| !keys.is_empty())
                .map(|(k, keys)| (k.clone(), automation::evaluate(keys, time_sec)))
                .collect(),
        }
    }
}

fn effect<'a>(doc: &'a Timeline, r: &KeyframeHostRef) -> Option<&'a fig_timeline::EffectInstance> {
    let id = r.effect_id.as_ref()?;
    super::effects::owner_effects(doc, &r.owner_id)?.iter().find(|e| &e.id == id)
}

fn store<'a>(doc: &'a Timeline, r: &KeyframeHostRef) -> Option<&'a KeyframeMap> {
    match &r.effect_id {
        Some(_) => effect(doc, r).map(|e| &e.keyframes),
        None => doc.find_clip(&r.owner_id).map(|c| &c.keyframes),
    }
}

fn store_mut<'a>(doc: &'a mut Timeline, r: &KeyframeHostRef) -> Option<&'a mut KeyframeMap> {
    match &r.effect_id {
        Some(id) => super::effects::owner_effects_mut(doc, &r.owner_id)?
            .iter_mut()
            .find(|e| &e.id == id)
            .map(|e| &mut e.keyframes),
        None => doc.find_clip_mut(&r.owner_id).map(|c| &mut c.keyframes),
    }
}

/// The key nearest a time in a list (within a millisecond).
fn key_at(track: &[KeyframePoint], time_sec: f64) -> Option<usize> {
    let mut best = None;
    let mut best_distance = 1e-3;
    for (i, k) in track.iter().enumerate() {
        let d = (k.time_sec - time_sec).abs();
        if d <= best_distance {
            best = Some(i);
            best_distance = d;
        }
    }
    best
}

/// Writes keys at a time: a key already there takes the new value and keeps its easing (or takes `easing`), a new
/// one eases `easing` or `new_key_easing`.
fn upsert(
    store: &mut KeyframeMap,
    host: &KeyframeHost,
    time_sec: f64,
    values: impl IntoIterator<Item = (String, ParamValue)>,
    easing: Option<KeyframeEasing>,
    new_key_easing: KeyframeEasing,
) {
    for (key, value) in values {
        if !host.includes(&key) {
            continue;
        }
        let track = store.entry(key).or_default();
        match track.iter().position(|p| (p.time_sec - time_sec).abs() < 1e-6) {
            Some(i) => track[i] = KeyframePoint::eased(time_sec, value, easing.unwrap_or(track[i].easing)),
            None => track.push(KeyframePoint::eased(time_sec, value, easing.unwrap_or(new_key_easing))),
        }
    }
}

/// A speed curve changed: the clip's out point is wherever the curve (or its one speed) now leaves it.
fn sync_speed_range(clip: &mut Clip) {
    if !clip.has_source_range() {
        return;
    }
    if clip_time::has_curve(clip) {
        clip_time::sync_source_out(clip);
    } else {
        let speed = if clip.speed <= 0.0 { 1.0 } else { clip.speed };
        clip.set_source_range(clip.source_in(), clip.source_in() + clip.dur_sec * speed);
    }
}

impl TimelineEditor {
    pub fn find_keyframe_host(&self, r: &KeyframeHostRef) -> Option<KeyframeHost> {
        KeyframeHost::find(self.doc(), r)
    }

    /// Runs one keyframe edit (joining the last one when `coalesce` matches). Afterwards the host's lists are in time
    /// order, empty ones are gone (so "has keyframes" holds only while there are some), and a speed curve re-syncs
    /// its clip's out point.
    fn edit_keyframes(
        &mut self,
        r: &KeyframeHostRef,
        coalesce: Option<String>,
        mutate: impl FnOnce(&mut KeyframeMap, &KeyframeHost),
    ) {
        let Some(host) = self.find_keyframe_host(r) else { return };
        let run = |e: &mut Self| {
            let doc = e.doc_mut();
            let Some(store) = store_mut(doc, r) else { return };
            mutate(store, &host);
            for (key, track) in store.iter_mut() {
                if host.includes(key) {
                    track.sort_by(|a, b| a.time_sec.total_cmp(&b.time_sec));
                }
            }
            store.retain(|key, track| !(host.includes(key) && track.is_empty()));
            if host.kind == HostKind::Speed
                && let Some(clip) = doc.find_clip_mut(&r.owner_id)
            {
                sync_speed_range(clip);
            }
        };
        match coalesce {
            Some(key) => self.edit_coalescing(format!("kf:{key}:{r:?}"), Coalesce::Continue, run),
            None => self.edit(run),
        }
    }

    /// Moves a diamond (every key at its time) to `to_sec`. Steps of one drag share a `gesture`.
    pub fn move_keyframes(&mut self, keyframe: &KeyframeRef, to_sec: f64, gesture: &str) {
        let Some(host) = self.find_keyframe_host(&keyframe.host) else { return };
        let to = to_sec.clamp(0.0, host.limit_sec);
        let from = keyframe.time_sec;
        self.edit_keyframes(&keyframe.host, Some(format!("drag|{gesture}")), |store, host| {
            for (key, track) in store.iter_mut() {
                if !host.includes(key) {
                    continue;
                }
                let Some(i) = key_at(track, from) else { continue };
                let point = track.remove(i);
                track.retain(|p| (p.time_sec - to).abs() >= 1e-6);
                track.push(KeyframePoint { time_sec: to, ..point });
            }
        });
    }

    /// Copies a diamond (same values and easing) to another time.
    pub fn duplicate_keyframes(&mut self, keyframe: &KeyframeRef, to_sec: f64) {
        let Some(host) = self.find_keyframe_host(&keyframe.host) else { return };
        let to = to_sec.clamp(0.0, host.limit_sec);
        let from = keyframe.time_sec;
        self.edit_keyframes(&keyframe.host, None, |store, host| {
            for (key, track) in store.iter_mut() {
                if !host.includes(key) {
                    continue;
                }
                let Some(i) = key_at(track, from) else { continue };
                let copy = KeyframePoint { time_sec: to, ..track[i] };
                track.retain(|p| (p.time_sec - to).abs() >= 1e-6);
                track.push(copy);
            }
        });
    }

    /// Deletes a diamond: every key at its time.
    pub fn remove_keyframes(&mut self, keyframe: &KeyframeRef) {
        let t = keyframe.time_sec;
        self.edit_keyframes(&keyframe.host, None, |store, host| {
            for (key, track) in store.iter_mut() {
                if host.includes(key)
                    && let Some(i) = key_at(track, t)
                {
                    track.remove(i);
                }
            }
        });
    }

    /// How the diamond's keys move on to the next ones.
    pub fn set_keyframe_easing(&mut self, keyframe: &KeyframeRef, easing: KeyframeEasing) {
        let t = keyframe.time_sec;
        self.edit_keyframes(&keyframe.host, None, |store, host| {
            for (key, track) in store.iter_mut() {
                if host.includes(key)
                    && let Some(i) = key_at(track, t)
                {
                    track[i].easing = easing;
                }
            }
        });
    }

    /// Adds a key at a time holding the host's current look there, so adding it changes nothing visible (keys hold
    /// their first and last values outside their range). Returns the time keyed; none when there is nothing to key.
    pub fn add_keyframe(&mut self, r: &KeyframeHostRef, time_sec: f64) -> Option<f64> {
        let host = self.find_keyframe_host(r)?;
        let time = time_sec.clamp(0.0, host.limit_sec);
        let values = host.current_values(self.doc(), time);
        if values.is_empty() {
            return None;
        }
        self.edit_keyframes(r, None, |store, host| {
            let typed: Vec<(String, ParamValue)> =
                values.iter().map(|(k, v)| (k.clone(), host.to_value(k, *v))).collect();
            upsert(store, host, time, typed, None, host.new_key_easing);
        });
        Some(time)
    }

    /// The stopwatch: keys the current look at a time, unless there already is a key there. Never removes any.
    pub fn record_keyframe(&mut self, r: &KeyframeHostRef, time_sec: f64) -> Option<f64> {
        let host = self.find_keyframe_host(r)?;
        let time = time_sec.clamp(0.0, host.limit_sec);
        if host.has_key_at(self.doc(), time) {
            return Some(time);
        }
        self.add_keyframe(r, time)
    }

    /// Sets one key's number at a time: updates the key there (keeping its easing), or adds one. Edits sharing a
    /// `gesture` (a drag of the value) are one undo step.
    pub fn set_keyframe_value(
        &mut self,
        r: &KeyframeHostRef,
        key: &str,
        time_sec: f64,
        value: f64,
        gesture: Option<&str>,
    ) {
        self.set_keyframe_with(r, key, time_sec, |host| host.to_value(key, value), gesture);
    }

    /// Sets one key to a typed value (a colour, a choice...) at a time.
    pub fn set_keyframe(
        &mut self,
        r: &KeyframeHostRef,
        key: &str,
        time_sec: f64,
        value: ParamValue,
        gesture: Option<&str>,
    ) {
        self.set_keyframe_with(r, key, time_sec, |_| value, gesture);
    }

    fn set_keyframe_with(
        &mut self,
        r: &KeyframeHostRef,
        key: &str,
        time_sec: f64,
        value: impl FnOnce(&KeyframeHost) -> ParamValue,
        gesture: Option<&str>,
    ) {
        let Some(host) = self.find_keyframe_host(r).filter(|h| h.includes(key)) else { return };
        let time = time_sec.clamp(0.0, host.limit_sec);
        self.edit_keyframes(r, gesture.map(|g| format!("drag|{g}")), |store, host| {
            let v = value(host);
            upsert(store, host, time, [(key.to_string(), v)], None, KeyframeEasing::Linear);
        });
    }

    /// Removes the key of one property at a time.
    pub fn remove_keyframe(&mut self, r: &KeyframeHostRef, key: &str, time_sec: f64) {
        self.edit_keyframes(r, None, |store, host| {
            if host.includes(key)
                && let Some(track) = store.get_mut(key)
                && let Some(i) = key_at(track, time_sec)
            {
                track.remove(i);
            }
        });
    }

    /// Removes every key of one property, or every key the host has when `key` is none.
    pub fn clear_keyframes(&mut self, r: &KeyframeHostRef, key: Option<&str>) {
        self.edit_keyframes(r, None, |store, host| {
            for (name, track) in store.iter_mut() {
                if host.includes(name) && key.is_none_or(|k| k == name) {
                    track.clear();
                }
            }
        });
    }

    /// Replaces all of a host's keys (none removes them), e.g. a whole speed curve. Edits sharing a non-empty
    /// `gesture` are one undo step.
    pub fn replace_keyframes(&mut self, r: &KeyframeHostRef, keys: Option<&KeyframeMap>, gesture: &str) {
        let coalesce = (!gesture.is_empty()).then(|| format!("replace|{gesture}"));
        self.edit_keyframes(r, coalesce, |store, host| {
            for (name, track) in store.iter_mut() {
                if host.includes(name) {
                    track.clear();
                }
            }
            for (name, track) in keys.into_iter().flatten() {
                if host.includes(name) && !track.is_empty() {
                    store.insert(name.clone(), track.clone());
                }
            }
        });
    }

    // ---- clip properties --------------------------------------------------------------------

    pub fn set_clip_keyframe(&mut self, clip_id: &str, key: &str, time_sec: f64, value: f64) {
        self.set_keyframe_value(&KeyframeHostRef::of_clip_property(clip_id, key), key, time_sec, value, None);
    }

    pub fn remove_clip_keyframe(&mut self, clip_id: &str, key: &str, time_sec: f64) {
        self.remove_keyframe(&KeyframeHostRef::of_clip_property(clip_id, key), key, time_sec);
    }

    pub fn clear_clip_keyframes(&mut self, clip_id: &str, key: Option<&str>) {
        match key {
            None => self.clear_keyframes(&KeyframeHostRef::of_clip(clip_id), None),
            Some(k) => self.clear_keyframes(&KeyframeHostRef::of_clip_property(clip_id, k), Some(k)),
        }
    }

    /// Sets a clip property on clips (one undo step; a slider drag joins into it). An animated property sets the key
    /// at the playhead instead (each clip's own moment), so dragging a keyed slider reshapes the animation there.
    pub fn set_clip_property(
        &mut self,
        clip_ids: &[String],
        property: &ClipProperty,
        value: ParamValue,
        playhead_sec: f64,
    ) {
        let clips: Vec<&Clip> =
            clip_ids.iter().filter_map(|id| self.find_clip(id)).filter(|c| property.has(c)).collect();
        if clips.is_empty() {
            return;
        }
        let animated: Vec<(String, f64)> = clips
            .iter()
            .filter(|c| property.is_animated(c))
            .map(|c| (c.id.clone(), (playhead_sec - c.start_sec).clamp(0.0, c.dur_sec)))
            .collect();
        let still: Vec<String> = clips.iter().filter(|c| !property.is_animated(c)).map(|c| c.id.clone()).collect();
        for (id, local) in &animated {
            let gesture = format!("{}@{}", property.key(), (local * 1e4).round() / 1e4);
            self.set_keyframe_value(
                &KeyframeHostRef::of_clip(id),
                property.key(),
                *local,
                value.as_number(),
                Some(&gesture),
            );
        }
        if !still.is_empty() {
            let set = property.set;
            let get = property.get;
            if still.iter().all(|id| self.find_clip(id).is_some_and(|c| get(c) == value)) {
                return;
            }
            self.set_clip_value(&still, property.key(), move |c| set(c, value));
        }
    }

    /// Sets one value on every listed clip, as one undo step (a slider drag over the same clips joins it). `property`
    /// names the edit for joining.
    pub fn set_clip_value(&mut self, clip_ids: &[String], property: &str, set: impl Fn(&mut Clip)) {
        let mut ids: Vec<String> = clip_ids.iter().filter(|id| self.find_clip(id).is_some()).cloned().collect();
        if ids.is_empty() {
            return;
        }
        ids.sort();
        ids.dedup();
        let key = format!("value:{property}:{}", ids.join(","));
        self.edit_coalescing(key, Coalesce::Restart, |e| {
            for id in &ids {
                if let Some(c) = e.doc_mut().find_clip_mut(id) {
                    set(c);
                }
            }
        });
    }
}
