use bitflags::bitflags;

bitflags! {
    /// What a selected thing can be edited as. The inspector shows one tab per facet that every selected item has,
    /// so it never has to ask "is this a video clip or a marker?": each item says what it is made of.
    #[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Default)]
    pub struct Facet: u32 {
        /// Opacity.
        const PICTURE = 1 << 0;
        /// Volume.
        const SOUND = 1 << 1;
        /// Fade in and out.
        const FADES = 1 << 2;
        /// Speed.
        const RETIME = 1 << 3;
        /// An effect stack, and the parameters of its effects.
        const EFFECTS = 1 << 4;
        /// The media file behind it.
        const SOURCE = 1 << 5;
        /// Text and how it is set.
        const TEXT = 1 << 6;
        /// A track's own settings.
        const TRACK = 1 << 7;
        /// A marker's name, time and colour.
        const MARKER = 1 << 8;
        /// A transition across a cut.
        const TRANSITION = 1 << 9;
        /// Crop insets from each edge.
        const CROP = 1 << 10;
        /// Where a layer sits on the frame: position, size and rotation.
        const LAYOUT = 1 << 11;
    }
}

/// Items of different groups are never selected together: picking something from another group replaces the
/// selection. (A clip and a marker would have no tab in common to edit.)
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum SelectionGroup {
    Clip,
    Track,
    Marker,
    Transition,
    Effect,
    /// A file in the media library (not on the timeline).
    Media,
}

/// Identifies one selectable thing. `owner_id` is set for things that live on another (an effect on its clip or
/// track).
#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub struct SelectionKey {
    pub group: SelectionGroup,
    pub id: String,
    pub owner_id: Option<String>,
}

impl SelectionKey {
    pub fn new(group: SelectionGroup, id: impl Into<String>) -> Self {
        SelectionKey { group, id: id.into(), owner_id: None }
    }

    pub fn owned(group: SelectionGroup, id: impl Into<String>, owner_id: impl Into<String>) -> Self {
        SelectionKey { group, id: id.into(), owner_id: Some(owner_id.into()) }
    }
}

/// Anything the user can select on the timeline.
pub trait Selectable {
    fn key(&self) -> SelectionKey;

    /// What it is called in the inspector's title ("Video clip", "Point marker").
    fn display_name(&self) -> &'static str;

    /// What can be edited about it.
    fn facets(&self) -> Facet;
}
