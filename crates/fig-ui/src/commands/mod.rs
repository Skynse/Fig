//! Every action the editor has, in one table: what it's called, where it's listed, and the keys it starts with.
//! Menus, the timeline toolbar, the shortcut cheat sheet and Preferences' keyboard page all read this table, and
//! `run` is the one place that does each thing.

pub mod actions;
pub mod keys;

use crate::settings::{KeyCombo, SpecialKey, parse_gesture};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum Command {
    // project
    Save,
    SaveAs,
    SavePackage,
    CloseProject,
    Preferences,
    Exit,
    Import,
    Export,
    ExportRanges,
    ExportCaptions,
    ExportResolve,
    // editing
    Undo,
    Redo,
    Cut,
    Copy,
    Paste,
    Duplicate,
    Split,
    SplitAll,
    TrimStart,
    TrimEnd,
    Freeze,
    FreezeInsert,
    RippleDelete,
    LiftDelete,
    Delete,
    ToggleEnabled,
    Group,
    Ungroup,
    Magnet,
    Snapping,
    SelectTool,
    BladeTool,
    SelectAll,
    DetachAudio,
    Reverse,
    AddMarker,
    DeleteMarker,
    RangesFromBookmarks,
    AutoCaptions,
    // playback
    PlayPause,
    Loop,
    FrameBack,
    FrameForward,
    SecondBack,
    SecondForward,
    ShuttleForward,
    ShuttleBack,
    ShuttleStop,
    AudioScrub,
    PreviousEdit,
    NextEdit,
    GoStart,
    GoEnd,
    // view
    MediaPanel,
    Inspector,
    ResetLayout,
    ZoomIn,
    ZoomOut,
    ZoomFit,
    FitTracks,
    TallerTracks,
    ShorterTracks,
    Shortcuts,
}

/// Every command, in the enum's order.
pub const ALL: &[Command] = &[
    Command::Save, Command::SaveAs, Command::SavePackage, Command::CloseProject, Command::Preferences, Command::Exit,
    Command::Import, Command::Export, Command::ExportRanges, Command::ExportCaptions, Command::ExportResolve, Command::Undo, Command::Redo,
    Command::Cut, Command::Copy, Command::Paste, Command::Duplicate, Command::Split, Command::SplitAll,
    Command::TrimStart, Command::TrimEnd, Command::Freeze, Command::FreezeInsert, Command::RippleDelete, Command::LiftDelete, Command::Delete,
    Command::ToggleEnabled, Command::Group, Command::Ungroup, Command::Magnet, Command::Snapping, Command::SelectTool,
    Command::BladeTool, Command::SelectAll, Command::DetachAudio, Command::Reverse, Command::AddMarker,
    Command::DeleteMarker, Command::RangesFromBookmarks, Command::AutoCaptions, Command::PlayPause, Command::Loop,
    Command::FrameBack, Command::FrameForward, Command::SecondBack, Command::SecondForward, Command::ShuttleForward,
    Command::ShuttleBack, Command::ShuttleStop, Command::AudioScrub, Command::PreviousEdit, Command::NextEdit,
    Command::GoStart, Command::GoEnd, Command::MediaPanel, Command::Inspector, Command::ResetLayout, Command::ZoomIn,
    Command::ZoomOut, Command::ZoomFit, Command::FitTracks, Command::TallerTracks, Command::ShorterTracks,
    Command::Shortcuts,
];

pub struct Entry {
    pub command: Command,
    /// The key preferences.json stores a changed shortcut under (C#'s ShortcutCatalog ids).
    pub id: &'static str,
    pub section: &'static str,
    pub label: &'static str,
    /// The keys it starts with, in the C# gesture syntax ("Ctrl+Shift+B"); empty for none.
    pub default: &'static str,
}

const fn e(command: Command, id: &'static str, section: &'static str, label: &'static str, default: &'static str) -> Entry {
    Entry { command, id, section, label, default }
}

use Command::*;

/// The rebindable shortcuts, in the cheat sheet's order.
pub const CATALOG: &[Entry] = &[
    e(Save, "project.save", "Project", "Save", "Ctrl+S"),
    e(CloseProject, "project.close", "Project", "Close project", "Ctrl+W"),
    e(Preferences, "app.preferences", "Project", "Preferences", "Ctrl+OemComma"),
    e(Import, "file.import", "Project", "Import media", "Ctrl+I"),
    e(Export, "file.export", "Project", "Export", "Ctrl+Shift+E"),
    e(Undo, "edit.undo", "Editing", "Undo", "Ctrl+Z"),
    e(Redo, "edit.redo", "Editing", "Redo", "Ctrl+Y"),
    e(Split, "edit.split", "Editing", "Split at playhead", "Ctrl+B"),
    e(SplitAll, "edit.splitAll", "Editing", "Split every track at the playhead", "Ctrl+Shift+B"),
    e(TrimStart, "edit.trimStart", "Editing", "Delete before the playhead", "Q"),
    e(TrimEnd, "edit.trimEnd", "Editing", "Delete after the playhead", "W"),
    e(Freeze, "edit.freeze", "Editing", "Freeze frame (hold to the end of the clip)", ""),
    e(FreezeInsert, "edit.freezeInsert", "Editing", "Insert a 3 s freeze frame", ""),
    e(Group, "edit.group", "Editing", "Group clips (move and cut together)", "Ctrl+G"),
    e(Ungroup, "edit.ungroup", "Editing", "Ungroup clips", "Ctrl+Shift+G"),
    e(Magnet, "edit.magnet", "Editing", "Main track magnet on or off", ""),
    e(Snapping, "edit.snap", "Editing", "Snapping on or off", "N"),
    e(SelectTool, "tool.select", "Editing", "Select tool", "A"),
    e(BladeTool, "tool.blade", "Editing", "Blade tool (click a clip to cut it)", "B"),
    e(Copy, "edit.copy", "Editing", "Copy", "Ctrl+C"),
    e(Cut, "edit.cut", "Editing", "Cut", "Ctrl+X"),
    e(Paste, "edit.paste", "Editing", "Paste", "Ctrl+V"),
    e(Duplicate, "edit.duplicate", "Editing", "Duplicate", "Ctrl+D"),
    e(ToggleEnabled, "edit.toggleEnabled", "Editing", "Enable / disable", "Ctrl+E"),
    e(Delete, "edit.delete", "Editing", "Delete", "Delete"),
    e(RippleDelete, "edit.rippleDelete", "Editing", "Ripple delete", "Shift+Delete"),
    e(SelectAll, "edit.selectAll", "Editing", "Select all", "Ctrl+A"),
    e(AddMarker, "marker.add", "Editing", "Add a marker at the playhead", "M"),
    e(PlayPause, "play.toggle", "Playback", "Play / pause", "Space"),
    e(Loop, "play.loop", "Playback", "Loop playback", "Ctrl+L"),
    e(FrameBack, "play.frameBack", "Playback", "Previous frame", "Left"),
    e(FrameForward, "play.frameForward", "Playback", "Next frame", "Right"),
    e(SecondBack, "play.secondBack", "Playback", "Back one second", "Shift+Left"),
    e(SecondForward, "play.secondForward", "Playback", "Forward one second", "Shift+Right"),
    e(ShuttleForward, "play.shuttleForward", "Playback", "Play forward (faster each press)", "L"),
    e(ShuttleBack, "play.shuttleBack", "Playback", "Play backward (faster each press)", "J"),
    e(ShuttleStop, "play.shuttleStop", "Playback", "Stop", "K"),
    e(AudioScrub, "play.audioScrub", "Playback", "Audio scrubbing on or off", "Shift+S"),
    e(PreviousEdit, "play.previousEdit", "Playback", "Previous cut", "Up"),
    e(NextEdit, "play.nextEdit", "Playback", "Next cut", "Down"),
    e(GoStart, "play.start", "Playback", "Go to the start", "Home"),
    e(GoEnd, "play.end", "Playback", "Go to the end", "End"),
    e(MediaPanel, "view.mediaPanel", "View", "Show or hide the media panel", "Ctrl+D1"),
    e(Inspector, "view.inspector", "View", "Show or hide the inspector", "Ctrl+D2"),
    e(ZoomIn, "view.zoomIn", "View", "Zoom the timeline in", "Ctrl+OemPlus"),
    e(ZoomOut, "view.zoomOut", "View", "Zoom the timeline out", "Ctrl+OemMinus"),
    e(ZoomFit, "view.zoomFit", "View", "Zoom to fit the sequence", "Shift+Z"),
];

/// Fixed keys the cheat sheet lists but nobody rebinds.
pub const FIXED: &[(&str, &str)] = &[
    ("Delete", "Delete the selection, leaving a gap; with a gap selected, close it"),
    ("Ctrl+Wheel", "Zoom in and out"),
    ("Shift+Wheel", "Scroll sideways"),
    ("Alt+Wheel", "Taller / shorter tracks"),
    ("Middle-drag", "Pan the timeline"),
    ("Ctrl+drag a clip", "Copy it"),
    ("Ctrl+drag a clip edge", "Roll the cut"),
    ("Drag an effect bar", "Move it in time, or onto another track or clip"),
    ("Ctrl+drag an effect bar", "Copy it, with the other selected bars"),
    ("Alt+drag", "Move without snapping"),
    ("Shift+drag", "Select with a box"),
    ("Blade: click", "Cut there (Shift: every track)"),
    ("Escape", "Put the blade away, else clear the selection; cancels a drag"),
];

pub fn entry(command: Command) -> Option<&'static Entry> {
    CATALOG.iter().find(|e| e.command == command)
}

/// The gesture a command is bound to now: the user's change from Preferences, else its default.
pub fn gesture_of(command: Command, overrides: &std::collections::BTreeMap<String, String>) -> String {
    match entry(command) {
        Some(e) => overrides.get(e.id).cloned().unwrap_or_else(|| e.default.to_string()),
        None => String::new(),
    }
}

/// "Ctrl+B", for menus and tooltips; empty when unbound.
pub fn shortcut_label(command: Command, overrides: &std::collections::BTreeMap<String, String>) -> String {
    let g = gesture_of(command, overrides);
    if g.is_empty() { String::new() } else { crate::settings::display_gesture(&g) }
}

/// Whether a key event matches a parsed gesture.
pub fn matches(combo: &KeyCombo, key: &rune::Key, mods: rune::Mods) -> bool {
    if combo.ctrl != mods.cmd || combo.shift != mods.shift || combo.alt != mods.alt {
        // a shifted punctuation key ("Ctrl++") arrives with Shift held on most layouts
        if !(combo.key == '+' && combo.ctrl == mods.cmd && !combo.shift && mods.shift) {
            return false;
        }
    }
    match (combo.special, key) {
        (Some(SpecialKey::Left), rune::Key::Left)
        | (Some(SpecialKey::Right), rune::Key::Right)
        | (Some(SpecialKey::Up), rune::Key::Up)
        | (Some(SpecialKey::Down), rune::Key::Down)
        | (Some(SpecialKey::Home), rune::Key::Home)
        | (Some(SpecialKey::End), rune::Key::End)
        | (Some(SpecialKey::PageUp), rune::Key::PageUp)
        | (Some(SpecialKey::PageDown), rune::Key::PageDown)
        | (Some(SpecialKey::Delete), rune::Key::Delete)
        | (Some(SpecialKey::Backspace), rune::Key::Backspace)
        | (Some(SpecialKey::Enter), rune::Key::Enter)
        | (Some(SpecialKey::Tab), rune::Key::Tab)
        | (Some(SpecialKey::Escape), rune::Key::Escape) => true,
        (Some(SpecialKey::Space), rune::Key::Text(t)) => t == " ",
        (Some(SpecialKey::Space), rune::Key::Command(' ')) => true,
        (Some(SpecialKey::Function(n)), rune::Key::Function(m)) => n == *m,
        (None, rune::Key::Command(c)) => c.to_ascii_lowercase() == combo.key || (combo.key == '+' && *c == '='),
        (None, rune::Key::Text(t)) => {
            let mut chars = t.chars();
            let c = chars.next();
            chars.next().is_none() && c.is_some_and(|c| c.to_ascii_lowercase() == combo.key)
        }
        _ => false,
    }
}

/// The command a key starts, given the user's changes.
pub fn for_key(key: &rune::Key, mods: rune::Mods, overrides: &std::collections::BTreeMap<String, String>) -> Option<Command> {
    CATALOG.iter().find_map(|e| {
        let g = overrides.get(e.id).map_or(e.default, String::as_str);
        let combo = parse_gesture(g)?;
        matches(&combo, key, mods).then_some(e.command)
    })
}
