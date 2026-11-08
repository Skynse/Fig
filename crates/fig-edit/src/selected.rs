//! What is selected, whatever it is: resolving it, describing it, deleting it, selecting everything.

use super::TimelineEditor;
use super::markers::find_marker;
use super::transitions::find_cut;
use fig_timeline::{Facet, Selectable, SelectionGroup, SelectionKey};

/// What is selected, by kind.
#[derive(Clone, Debug, PartialEq)]
pub enum Selected {
    Clip(fig_timeline::Clip),
    Track(fig_timeline::Track),
    Marker(fig_timeline::Marker),
    Transition(super::CutTransition),
    /// An effect bar, with the id of the track or clip it is on.
    Effect {
        owner_id: String,
        effect: fig_timeline::EffectInstance,
    },
    Media(String),
}

impl Selected {
    pub fn group(&self) -> SelectionGroup {
        match self {
            Selected::Clip(_) => SelectionGroup::Clip,
            Selected::Track(_) => SelectionGroup::Track,
            Selected::Marker(_) => SelectionGroup::Marker,
            Selected::Transition(_) => SelectionGroup::Transition,
            Selected::Effect { .. } => SelectionGroup::Effect,
            Selected::Media(_) => SelectionGroup::Media,
        }
    }

    pub fn facets(&self) -> Facet {
        match self {
            Selected::Clip(c) => c.facets(),
            Selected::Track(t) => t.facets(),
            Selected::Marker(m) => m.facets(),
            Selected::Transition(t) => t.facets(),
            Selected::Effect { .. } => Facet::EFFECTS,
            Selected::Media(_) => Facet::SOURCE,
        }
    }

    pub fn display_name(&self) -> String {
        match self {
            Selected::Clip(c) => c.display_name().into(),
            Selected::Track(t) => t.display_name().into(),
            Selected::Marker(m) => m.display_name().into(),
            Selected::Transition(t) => t.display_name().into(),
            Selected::Effect { effect, .. } => {
                fig_fx::effects::find(&effect.type_id).map_or("Effect".into(), |e| e.display_name.clone())
            }
            Selected::Media(_) => "Media".into(),
        }
    }
}

fn noun(group: SelectionGroup) -> &'static str {
    match group {
        SelectionGroup::Clip => "clip",
        SelectionGroup::Track => "track",
        SelectionGroup::Marker => "marker",
        SelectionGroup::Transition => "transition",
        SelectionGroup::Effect => "effect",
        SelectionGroup::Media => "media file",
    }
}

fn counted(n: usize, noun: &str) -> String {
    if n == 1 { format!("1 {noun} selected") } else { format!("{n} {noun}s selected") }
}

/// Things on clips and tracks go before the clips and tracks that hold them.
const DELETE_ORDER: [SelectionGroup; 5] = [
    SelectionGroup::Effect,
    SelectionGroup::Marker,
    SelectionGroup::Transition,
    SelectionGroup::Clip,
    SelectionGroup::Track,
];

impl TimelineEditor {
    /// The thing behind a selection key, or none when it no longer exists.
    pub fn resolve(&self, key: &SelectionKey) -> Option<Selected> {
        let doc = self.doc();
        Some(match key.group {
            SelectionGroup::Clip => Selected::Clip(doc.find_clip(&key.id)?.clone()),
            SelectionGroup::Track => Selected::Track(doc.find_track(&key.id)?.clone()),
            SelectionGroup::Marker => Selected::Marker(find_marker(doc, &key.id)?.marker),
            SelectionGroup::Transition => Selected::Transition(find_cut(doc, &key.id)?),
            SelectionGroup::Effect => {
                let owner = key.owner_id.clone()?;
                let effect = self.find_effect(&owner, &key.id)?.clone();
                Selected::Effect { owner_id: owner, effect }
            }
            SelectionGroup::Media => return None,
        })
    }

    /// Everything selected that still exists, in the order it was picked.
    pub fn selected(&self) -> Vec<Selected> {
        self.selection.items().iter().filter_map(|k| self.resolve(k)).collect()
    }

    /// What every selected item can be edited as: the facets they all have. None selected has none.
    pub fn shared_facets(&self) -> Facet {
        self.selected().iter().map(Selected::facets).reduce(|a, b| a & b).unwrap_or(Facet::empty())
    }

    /// Selects everything on the timeline: every effect bar, every marker and every clip (clips last, so the item a
    /// single-item panel shows is a clip).
    pub fn select_all(&mut self) {
        let doc = self.doc();
        let mut keys: Vec<SelectionKey> = Vec::new();
        for track in &doc.tracks {
            keys.extend(track.effects.iter().map(|fx| SelectionKey::owned(SelectionGroup::Effect, &fx.id, &track.id)));
            for clip in &track.clips {
                keys.extend(
                    clip.effects.iter().map(|fx| SelectionKey::owned(SelectionGroup::Effect, &fx.id, &clip.id)),
                );
            }
        }
        keys.extend(self.all_markers().into_iter().map(|m| SelectionKey::new(SelectionGroup::Marker, m.marker.id)));
        keys.extend(doc.clips().map(|c| c.key()));
        self.selection.replace_all(keys);
    }

    /// Deletes what is selected, whatever it is, as one undo step (a picked keyframe diamond goes, not what it sits
    /// on). The main track closes up. Returns how many were removed and the noun for the message.
    pub fn delete_selection(&mut self) -> Option<(usize, &'static str)> {
        self.keep_main_track_closed(|e| e.delete_selection_leaving_gaps())
    }

    fn delete_selection_leaving_gaps(&mut self) -> Option<(usize, &'static str)> {
        if let Some(k) = self.selection.keyframe().cloned() {
            self.remove_keyframes(&k);
            self.selection.clear_keyframe();
            return Some((1, "keyframe"));
        }
        let keys = self.selection.items().to_vec();
        if keys.is_empty() {
            return None;
        }
        let mut total = 0;
        let mut nouns: Vec<&'static str> = Vec::new();
        for group in DELETE_ORDER {
            let of: Vec<SelectionKey> = keys.iter().filter(|k| k.group == group).cloned().collect();
            if of.is_empty() {
                continue;
            }
            // each group's delete sees just its own items selected (some read the selection)
            self.selection.replace_all(of.clone());
            let removed = match group {
                SelectionGroup::Clip => {
                    self.lift_selected();
                    of.len()
                }
                SelectionGroup::Track => of.iter().filter(|k| self.remove_track(&k.id)).count(),
                SelectionGroup::Marker => self.delete_markers(&of.iter().map(|k| k.id.clone()).collect::<Vec<_>>()),
                SelectionGroup::Transition => {
                    let before = self.selection.selected_transition_key().map(str::to_string);
                    self.remove_selected_transition();
                    usize::from(before.is_some_and(|b| self.get_transition(&b).is_none()))
                }
                SelectionGroup::Effect => {
                    let items: Vec<(String, String)> =
                        of.iter().map(|k| (k.owner_id.clone().unwrap_or_default(), k.id.clone())).collect();
                    self.remove_effects(&items)
                }
                SelectionGroup::Media => 0,
            };
            total += removed;
            if removed > 0 {
                nouns.push(noun(group));
            }
        }
        self.selection.clear();
        (total > 0).then(|| (total, if nouns.len() == 1 { nouns[0] } else { "item" }))
    }

    /// What the status bar says about the selection, and a hint for what to do next.
    pub fn describe_selection(&self) -> (String, String) {
        let items = self.selected();
        if items.is_empty() {
            return (
                "Nothing selected".into(),
                "Click a clip to select it · drag empty space to box-select · Space plays · M adds a marker".into(),
            );
        }
        let mut groups: Vec<(SelectionGroup, Vec<&Selected>)> = Vec::new();
        for item in &items {
            match groups.iter_mut().find(|g| g.0 == item.group()) {
                Some(g) => g.1.push(item),
                None => groups.push((item.group(), vec![item])),
            }
        }
        let summary = |group: SelectionGroup, items: &[&Selected]| -> String {
            match group {
                SelectionGroup::Clip => {
                    let linked: Vec<Option<&String>> = items
                        .iter()
                        .map(|s| if let Selected::Clip(c) = s { c.link_group_id.as_ref() } else { None })
                        .collect();
                    if items.len() > 1 && linked[0].is_some() && linked.iter().all(|g| *g == linked[0]) {
                        format!("{} linked clips selected", items.len())
                    } else {
                        counted(items.len(), "clip")
                    }
                }
                SelectionGroup::Track => "Track selected".into(),
                SelectionGroup::Marker => counted(items.len(), "marker"),
                SelectionGroup::Transition => "Transition selected".into(),
                SelectionGroup::Effect => {
                    if items.len() == 1 {
                        "Effect selected".into()
                    } else {
                        format!("{} effects selected", items.len())
                    }
                }
                SelectionGroup::Media => "Media selected".into(),
            }
        };
        if groups.len() > 1 {
            let text = groups.iter().map(|(g, i)| summary(*g, i)).collect::<Vec<_>>().join(" · ");
            return (text, "Delete removes everything selected · Ctrl-click adds or removes · Esc clears".into());
        }
        let (group, list) = &groups[0];
        let hint = match group {
            SelectionGroup::Clip => {
                "Drag an edge to trim · Ctrl-click adds or removes · drag empty space (or Shift-drag anywhere) to select a region"
            }
            SelectionGroup::Track => "Drop clips on a track · Delete removes the track",
            SelectionGroup::Marker => {
                "Ctrl-click toggles markers · Shift-click selects a range · Delete removes selected markers"
            }
            SelectionGroup::Transition => "Drag the transition handle to change its length · Delete removes it",
            SelectionGroup::Effect if list.len() == 1 => {
                "Shift-click adds more · Ctrl-drag duplicates · Delete removes the effect"
            }
            SelectionGroup::Effect => "Drag to move them together · Shift-click adds or removes · Delete removes them",
            SelectionGroup::Media => "Drag it onto the timeline, or right-click it for more",
        };
        (summary(*group, list), hint.into())
    }
}
