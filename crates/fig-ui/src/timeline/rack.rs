//! The racks (`TimelineView.Racks.cs`): every effect on a track as a bar under its clips that keeps its timing, and a
//! clip's own sound level and fades as bars under it. Bars fold to thin strips or open to labelled rows with a
//! stopwatch, power and remove buttons and a keyframe lane; a bar's body moves it and its ends resize it.
//!
//! Folding is per track. Once lanes are shrunk past [`FOLD_SCALE`] every rack folds unless its track is opened by hand.

use std::cell::RefCell;
use std::collections::{HashMap, HashSet};
use std::hash::{Hash, Hasher};
use std::rc::Rc;

use fig_timeline::{Clip, ClipBody, EffectInstance, Facet, Track, automation_keys};

pub const PAD: f32 = 4.0;
pub const BAR_COMPACT: f32 = 6.0;
pub const BAR_FULL: f32 = 32.0;
/// The label row of an open bar; under it is the keyframe lane.
pub const LABEL_ROW: f32 = 20.0;
const GAP_COMPACT: f32 = 2.0;
const GAP_FULL: f32 = 3.0;
pub const MIN_BAR_W: f32 = 4.0;
/// Bars this wide or wider show their stopwatch, power and remove buttons.
pub const BUTTONS_W: f32 = 110.0;
pub const FOLD_SCALE: f32 = 0.75;

/// Which tracks' racks are folded.
#[derive(Default)]
pub struct Folds {
    /// Bumped on every change (lane layouts are cached by it).
    pub generation: u64,
    /// Folded by hand while racks are open by default.
    collapsed: HashSet<String>,
    /// Opened by hand while short lanes fold every rack.
    opened: HashSet<String>,
}

impl Folds {
    pub fn is_open(&self, track_id: &str, scale: f32) -> bool {
        if scale < FOLD_SCALE { self.opened.contains(track_id) } else { !self.collapsed.contains(track_id) }
    }

    pub fn set_open(&mut self, track_id: &str, scale: f32, open: bool) {
        self.generation += 1;
        let id = track_id.to_string();
        if scale < FOLD_SCALE {
            if open { self.opened.insert(id) } else { self.opened.remove(track_id) };
        } else if open {
            self.collapsed.remove(track_id);
        } else {
            self.collapsed.insert(id);
        }
    }
}

pub fn bar_h(open: bool) -> f32 {
    if open { BAR_FULL } else { BAR_COMPACT }
}

pub fn row_h(open: bool) -> f32 {
    bar_h(open) + if open { GAP_FULL } else { GAP_COMPACT }
}

/// One bar.
#[derive(Clone, Debug)]
pub struct Item {
    pub label: String,
    pub fill: &'static str,
    pub edge: &'static str,
    /// The effect it stands for; none for a clip's level and fades.
    pub effect: Option<String>,
    /// From the owner's origin (a clip's start; a track's is time zero).
    pub start: f64,
    pub dur: f64,
    pub enabled: bool,
    /// Limited to part of its owner (else it covers the whole and only its ends drag).
    pub ranged: bool,
    /// The inspector tab a click on a level or fade bar opens.
    pub opens: Facet,
    pub row: usize,
    /// Its keyframe times, from the owner's origin.
    pub keys: Vec<f64>,
}

/// Something that carries effects: a track (whose time starts at zero) or, in old projects, a clip.
#[derive(Clone, Copy)]
pub enum Owner<'a> {
    Clip(&'a Clip),
    Track(&'a Track),
}

impl<'a> Owner<'a> {
    pub fn id(&self) -> &'a str {
        match self {
            Owner::Clip(c) => &c.id,
            Owner::Track(t) => &t.id,
        }
    }

    /// Where its bars' times count from.
    pub fn origin(&self) -> f64 {
        match self {
            Owner::Clip(c) => c.start_sec,
            Owner::Track(_) => 0.0,
        }
    }

    /// How long a bar covering all of it is.
    pub fn span(&self) -> f64 {
        match self {
            Owner::Clip(c) => c.dur_sec,
            Owner::Track(t) => t.clips.iter().map(Clip::end_sec).fold(0.0, f64::max),
        }
    }

    fn effects(&self) -> &'a [EffectInstance] {
        match self {
            Owner::Clip(c) => &c.effects,
            Owner::Track(t) => &t.effects,
        }
    }
}

fn keyframe_times(fx: &EffectInstance) -> Vec<f64> {
    let mut times: Vec<f64> = fx.keyframes.values().flat_map(|keys| keys.iter().map(|k| k.time_sec)).collect();
    times.sort_by(f64::total_cmp);
    times.dedup_by(|a, b| (*a - *b).abs() < 1e-6);
    times
}

/// A clip's sound level is worth a bar when it isn't 1 or is animated.
fn level_changed(clip: &Clip) -> bool {
    (clip.volume - 1.0).abs() > 0.01 || clip.keyframes.contains_key(automation_keys::VOLUME)
}

/// A built rack and the fingerprint it was built from.
type Built = (u64, Rc<Vec<Item>>);

thread_local! {
    /// Racks built so far, by owner, with the fingerprint of what they were built from.
    static RACKS: RefCell<HashMap<String, Built>> = RefCell::new(HashMap::new());
}

/// Everything [`build`] reads, folded into one number: cheap to compute, so a rack is only rebuilt when it changes.
fn fingerprint(owner: Owner) -> u64 {
    let mut h = std::collections::hash_map::DefaultHasher::new();
    owner.span().to_bits().hash(&mut h);
    for fx in owner.effects() {
        fx.id.hash(&mut h);
        fx.type_id.hash(&mut h);
        (fx.order, fx.enabled).hash(&mut h);
        fx.start_sec.map(f64::to_bits).hash(&mut h);
        fx.dur_sec.map(f64::to_bits).hash(&mut h);
        for (k, keys) in &fx.keyframes {
            k.hash(&mut h);
            for key in keys {
                key.time_sec.to_bits().hash(&mut h);
            }
        }
    }
    if let Owner::Clip(c) = owner {
        (c.volume.to_bits(), c.fade_in_sec.to_bits(), c.fade_out_sec.to_bits(), c.keyframes.contains_key(automation_keys::VOLUME)).hash(&mut h);
        c.as_video().map(|v| v.plays_audio).hash(&mut h);
    }
    h.finish()
}

/// The bars under an owner, each on its row (kept until what they're built from changes).
pub fn items(owner: Owner) -> Rc<Vec<Item>> {
    // a clip with nothing to show needs no lookup
    if let Owner::Clip(c) = owner
        && c.effects.is_empty()
        && !matches!(c.body, ClipBody::Audio(_) | ClipBody::Video(_))
    {
        return Rc::new(Vec::new());
    }
    let print = fingerprint(owner);
    if let Some(items) = RACKS.with_borrow(|m| m.get(owner.id()).filter(|(p, _)| *p == print).map(|(_, i)| i.clone())) {
        return items;
    }
    let items = Rc::new(build(owner));
    RACKS.with_borrow_mut(|m| m.insert(owner.id().to_string(), (print, items.clone())));
    items
}

fn build(owner: Owner) -> Vec<Item> {
    let mut items = Vec::new();
    let mut effects: Vec<&EffectInstance> = owner.effects().iter().collect();
    effects.sort_by_key(|fx| fx.order);
    for fx in effects {
        let motion = fx.type_id == fig_fx::effects::ids::TRANSFORM;
        let ranged = fx.start_sec.is_some() && fx.dur_sec.is_some();
        items.push(Item {
            label: fig_fx::effects::find(&fx.type_id).map_or_else(|| fx.type_id.clone(), |e| e.display_name.clone()),
            fill: if motion { "#1f3a66" } else { "#6f5526" },
            edge: if motion { "#5f9cf8" } else { "#c79a3d" },
            effect: Some(fx.id.clone()),
            start: if ranged { fx.start_sec.unwrap_or(0.0) } else { 0.0 },
            dur: if ranged { fx.dur_sec.unwrap_or(0.0) } else { owner.span() },
            enabled: fx.enabled,
            ranged,
            opens: Facet::EFFECTS,
            row: 0,
            keys: keyframe_times(fx),
        });
    }
    if let Owner::Clip(clip) = owner {
        let level = |dur| Item {
            label: "Level".into(),
            fill: "#2d674a",
            edge: "#589b74",
            effect: None,
            start: 0.0,
            dur,
            enabled: true,
            ranged: false,
            opens: Facet::SOUND,
            row: 0,
            keys: Vec::new(),
        };
        match &clip.body {
            ClipBody::Audio(_) => {
                if level_changed(clip) {
                    items.push(level(clip.dur_sec));
                }
                let fade = |label: &str, start, dur| Item { label: label.into(), fill: "#1f3a66", edge: "#5f9cf8", opens: Facet::FADES, start, ..level(dur) };
                if clip.fade_in_sec > 1e-3 {
                    items.push(fade("Fade in", 0.0, clip.fade_in_sec));
                }
                if clip.fade_out_sec > 1e-3 {
                    items.push(fade("Fade out", (clip.dur_sec - clip.fade_out_sec).max(0.0), clip.fade_out_sec));
                }
            }
            // a video's own sound: its level (its fades already show on the clip)
            ClipBody::Video(v) if v.plays_audio && level_changed(clip) => items.push(level(clip.dur_sec)),
            _ => {}
        }
    }
    assign_rows(&mut items, matches!(owner, Owner::Track(_)));
    items
}

/// A clip stacks its bars one a row. A track's effects are regions in timeline time, so bars that don't overlap share
/// a row and the lane only grows where regions overlap.
fn assign_rows(items: &mut [Item], pack: bool) {
    if !pack {
        for (i, item) in items.iter_mut().enumerate() {
            item.row = i;
        }
        return;
    }
    let mut order: Vec<usize> = (0..items.len()).collect();
    order.sort_by(|&a, &b| items[a].start.total_cmp(&items[b].start).then(a.cmp(&b)));
    let mut ends: Vec<f64> = Vec::new();
    for i in order {
        let row = ends.iter().position(|end| *end <= items[i].start + 1e-6).unwrap_or_else(|| {
            ends.push(0.0);
            ends.len() - 1
        });
        ends[row] = items[i].start + items[i].dur;
        items[i].row = row;
    }
}

fn rows(items: &[Item]) -> usize {
    items.iter().map(|i| i.row + 1).max().unwrap_or(0)
}

/// The height an owner's rack adds under the clips (0 when it has no bars).
pub fn height(owner: Owner, open: bool) -> f32 {
    match rows(&items(owner)) {
        0 => 0.0,
        n => PAD + n as f32 * row_h(open),
    }
}

/// The tallest rack among a track's clips; the track's own bars go under it.
pub fn clip_racks_height(track: &Track, open: bool) -> f32 {
    track.clips.iter().map(|c| height(Owner::Clip(c), open)).fold(0.0, f32::max)
}

/// True when the track has any bars (so its fold toggle is worth showing).
pub fn has_rack(track: &Track) -> bool {
    !track.effects.is_empty() || track.clips.iter().any(|c| !items(Owner::Clip(c)).is_empty())
}

/// Where a bar is drawn: `x` is the owner's origin on screen, `top` the top of its first row.
pub fn bar_rect(item: &Item, x: f32, top: f32, pps: f32, open: bool) -> [f32; 4] {
    [x + item.start as f32 * pps, top + item.row as f32 * row_h(open), (item.dur as f32 * pps).max(MIN_BAR_W), bar_h(open)]
}

/// The stopwatch, power and remove buttons of an open bar wide enough for them.
pub fn buttons(bar: [f32; 4]) -> [[f32; 4]; 3] {
    let right = bar[0] + bar[2];
    [[right - 60.0, bar[1] + 3.0, 16.0, 16.0], [right - 40.0, bar[1] + 3.0, 16.0, 16.0], [right - 22.0, bar[1] + 3.0, 16.0, 16.0]]
}

pub fn contains(r: [f32; 4], x: f32, y: f32) -> bool {
    x >= r[0] && x <= r[0] + r[2] && y >= r[1] && y <= r[1] + r[3]
}

#[cfg(test)]
mod tests {
    use super::*;

    fn bar(start: f64, dur: f64) -> Item {
        Item {
            label: String::new(),
            fill: "",
            edge: "",
            effect: None,
            start,
            dur,
            enabled: true,
            ranged: true,
            opens: Facet::EFFECTS,
            row: 0,
            keys: Vec::new(),
        }
    }

    #[test]
    fn track_bars_share_rows_unless_they_overlap() {
        let mut items = vec![bar(0.0, 2.0), bar(2.0, 1.0), bar(1.0, 3.0)];
        assign_rows(&mut items, true);
        assert_eq!(items.iter().map(|i| i.row).collect::<Vec<_>>(), [0, 0, 1]);
        assign_rows(&mut items, false);
        assert_eq!(items.iter().map(|i| i.row).collect::<Vec<_>>(), [0, 1, 2]);
    }
}
