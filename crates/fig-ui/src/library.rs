//! The library panel: the 44 px icon rail and the content for the selected tab.
//!
//! `EditorView.axaml`'s left column (inventory §4.2): Media, Templates, Text, Transitions, Effects,
//! Markers, Captions. The Media tab is the project's library grid; the others are catalogs. This
//! slice renders the rail, the header, the search field and the empty state; the grids and drag
//! sources come with P4.

use rune::{Align, El, Icon, el, icon, row, text};

use crate::app::AppState;
use crate::editor_state::EditorState;
use crate::icons;
use crate::theme::{font, Tokens, RADIUS_CONTROL, RADIUS_PANEL};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub enum LibraryTab {
    #[default]
    Media,
    Templates,
    Text,
    Transitions,
    Effects,
    Markers,
    Captions,
}

impl LibraryTab {
    pub const ALL: [LibraryTab; 7] = [
        LibraryTab::Media,
        LibraryTab::Templates,
        LibraryTab::Text,
        LibraryTab::Transitions,
        LibraryTab::Effects,
        LibraryTab::Markers,
        LibraryTab::Captions,
    ];

    pub fn title(self) -> &'static str {
        match self {
            LibraryTab::Media => "Media",
            LibraryTab::Templates => "Templates",
            LibraryTab::Text => "Text",
            LibraryTab::Transitions => "Transitions",
            LibraryTab::Effects => "Effects",
            LibraryTab::Markers => "Markers",
            LibraryTab::Captions => "Captions",
        }
    }

    pub fn icon(self) -> &'static Icon {
        match self {
            LibraryTab::Media => &icons::FILM,
            LibraryTab::Templates => &icons::CLAPPERBOARD,
            LibraryTab::Text => &icons::TYPE,
            LibraryTab::Transitions => &icons::BLEND,
            LibraryTab::Effects => &icons::WAND,
            LibraryTab::Markers => &icons::FLAG,
            LibraryTab::Captions => &icons::CAPTIONS,
        }
    }

    pub fn empty_hint(self) -> (&'static str, &'static str) {
        match self {
            LibraryTab::Media => ("Drop media here or use", "Import"),
            LibraryTab::Templates => ("No templates yet", ""),
            LibraryTab::Text => ("Click a style to add it at the playhead", ""),
            LibraryTab::Transitions => ("Drop a transition on a cut", ""),
            LibraryTab::Effects => ("Click an effect to add it to the clip", ""),
            LibraryTab::Markers => ("Markers in this sequence", ""),
            LibraryTab::Captions => ("Auto captions…", ""),
        }
    }
}

pub struct LibraryState {
    pub tab: LibraryTab,
    pub search: String,
}

impl LibraryState {
    pub fn new() -> LibraryState {
        LibraryState { tab: LibraryTab::Media, search: String::new() }
    }
}

pub fn panel(editor: &EditorState, t: &Tokens) -> El {
    row()
        .w_full()
        .h_full()
        .min_w(0.0)
        .fill(t.panel_bg)
        .radius(RADIUS_PANEL)
        .clip_rounded(RADIUS_PANEL)
        .child(rail(editor, t))
        .child(body(editor, t))
}

fn rail(editor: &EditorState, t: &Tokens) -> El {
    let mut rail = rune::col().w(44.0).h_full().padding(4.0).gap(4.0);
    for tab in LibraryTab::ALL {
        let selected = editor.library.tab == tab;
        let color = if selected { t.accent_text } else { t.text_muted };
        let mut item = el()
            .w(36.0)
            .h(36.0)
            .radius(RADIUS_CONTROL)
            .center()
            .child(icon(tab.icon(), 18.0).color(color))
            .on_press(move |s: &mut AppState| {
                s.editor().library.tab = tab;
            });
        if selected {
            item = item.fill(t.accent_soft);
        }
        rail = rail.child(item);
    }
    rail
}

fn body(editor: &EditorState, t: &Tokens) -> El {
    let tab = editor.library.tab;
    let (first, second) = tab.empty_hint();
    rune::col()
        .grow(1.0)
        .h_full()
        .min_w(0.0)
        .padding(12.0)
        .gap(10.0)
        .child(
            row()
                .w_full()
                .h(24.0)
                .center_y()
                .child(text(tab.title()).font(font(14.0, 600.0)).color(t.text_primary))
                .child(el().grow(1.0))
                .child(icon(&icons::CHEVRON_LEFT, 14.0).color(t.text_muted)),
        )
        .child(crate::widgets::field("Search media", t))
        .child(
            crate::widgets::button("Import media…", t, false).on_press_tx(|s: &mut AppState, tx: &mut rune::Tx| {
                let options = rune::FileDialogOptions {
                    title: "Import media".into(),
                    directory: std::env::current_dir().ok(),
                    file_name: None,
                    filters: Vec::new(),
                }
                .filter("Video", &["mp4", "mov", "mkv", "webm"])
                .filter("Audio", &["wav", "mp3", "m4a", "flac"])
                .filter("Images", &["png", "jpg", "jpeg", "gif", "webp"])
                .filter("All files", &["*"]);
                tx.open_file(options, |s: &mut AppState, result, _| match result {
                    Ok(Some(path)) => {
                        let editor = s.editor();
                        match fig_media::probe::probe(&path) {
                            Ok(asset) => {
                                editor.preview.build_media_thumbnails(std::slice::from_ref(&asset));
                                editor.project.media.push(asset);
                                editor.save = crate::editor_state::SaveState::Dirty;
                                s.toasts.show("Media imported");
                            }
                            Err(error) => s.toasts.show(format!("Couldn't read that file: {error}")),
                        }
                    }
                    Ok(None) => {}
                    Err(error) => s.toasts.show(format!("File dialog failed: {error}")),
                });
            }),
        )
        .child(tab_content(editor, t, first, second))
}

/// Snapshot scenes for each library tab.
pub fn scene_effects(state: &mut AppState) {
    state.route = crate::app::Route::Editor;
    state.editor().library.tab = LibraryTab::Effects;
}

pub fn scene_text(state: &mut AppState) {
    state.route = crate::app::Route::Editor;
    state.editor().library.tab = LibraryTab::Text;
}

/// The media grid: 126 px cards, 72 px thumbnail well, duration chip, add button (inventory §4.2).
fn media_grid(editor: &EditorState, t: &Tokens) -> El {
    let mut grid = rune::row().w_full().grow(1.0).min_h(0.0).flex_wrap().gap(8.0);
    for (i, asset) in editor.project.media.iter().enumerate() {
        grid = grid.child(media_card(editor, i, asset, t));
    }
    grid
}

fn media_card(editor: &EditorState, index: usize, asset: &fig_timeline::MediaAsset, t: &Tokens) -> El {
    let key = format!("media.{index}");
    let _ = &key;
    let thumb = editor.preview.thumbnail(&asset.id);
    let mut well = el()
        .w_full()
        .h(72.0)
        .radius(8.0)
        .fill(crate::theme::hex("#0e0f11"))
        .clip_rounded(8.0);
    if let Some(pixels) = thumb {
        well = well.child(el().w_full().h_full().pixels(&pixels));
    } else {
        well = well.center().child(icon(&icons::FILM, 20.0).color(t.text_faint));
    }

    let duration = format!("{}:{:02}", (asset.duration_sec as i64) / 60, (asset.duration_sec as i64) % 60);
    let name = std::path::Path::new(&asset.url)
        .file_name()
        .map_or_else(|| "Media".to_string(), |n| n.to_string_lossy().into_owned());

    rune::col()
        .key(format!("media.{index}"))
        .w(126.0)
        .h(112.0)
        .fill(t.card_bg)
        .radius(10.0)
        .border(1.0, t.border)
        .padding(4.0)
        .gap(4.0)
        .child(
            el().w_full()
                .h(72.0)
                .child(well)
                .child(
                    el().absolute(70.0, 4.0)
                        .h(16.0)
                        .radius(4.0)
                        .fill(crate::theme::hex("#000000a0"))
                        .padding(4.0)
                        .center_y()
                        .child(text(duration).font(font(10.0, 500.0)).color(rune::Color::WHITE)),
                ),
        )
        .child(
            text(name)
                .font(font(11.0, 400.0))
                .color(t.text_secondary)
                .w(118.0)
                .truncate(),
        )
        .on_press(move |s: &mut AppState| {
            let editor = s.editor();
            let Some(asset) = editor.project.media.get(index).cloned() else { return };
            let track_id = editor
                .doc()
                .tracks
                .iter()
                .find(|track| track.kind == fig_timeline::TrackKind::Video)
                .map(|track| track.id.clone());
            let at = editor.playhead;
            // the playhead may be over an existing clip: then it starts a new track for it
            let made = match track_id {
                Some(track) => editor
                    .editor
                    .add_media(&asset, &track, at)
                    .or_else(|| Some(editor.editor.add_media_new_track(&asset, at))),
                None => Some(editor.editor.add_media_new_track(&asset, at)),
            };
            if let Some(clip_id) = made {
                editor.editor.selection.select_only(&clip_id);
                editor.save = crate::editor_state::SaveState::Dirty;
            } else {
                s.toasts.show("No room for that clip here");
            }
        })
}

fn tab_content(editor: &EditorState, t: &Tokens, first: &str, second: &str) -> El {
    match editor.library.tab {
        LibraryTab::Media if !editor.project.media.is_empty() => media_grid(editor, t),
        LibraryTab::Effects => effects_list(t),
        LibraryTab::Transitions => transitions_list(t),
        LibraryTab::Text => text_presets(editor, t),
        LibraryTab::Markers => markers_list(editor, t),
        LibraryTab::Captions => captions_list(editor, t),
        _ => empty_state(editor.library.tab.icon(), first, second, t),
    }
}

fn empty_state(icon_def: &'static Icon, first: &str, second: &str, t: &Tokens) -> El {
    rune::col()
        .w_full()
        .grow(1.0)
        .min_h(0.0)
        .justify_center()
        .center_y()
        .gap(10.0)
        .child(icon(icon_def, 28.0).color(t.text_faint))
        .child(text(first).font(font(11.0, 400.0)).color(t.text_muted))
        .child(text(second).font(font(11.0, 400.0)).color(t.text_muted))
        .align(Align::Center)
}

fn effects_list(t: &Tokens) -> El {
    let mut list = rune::col().w_full().grow(1.0).min_h(0.0).gap(2.0).scroll(rune::ScrollSpec { stick_bottom: false });
    for entry in fig_fx::effects::all().iter().take(60) {
        let type_id = entry.type_id.clone();
        list = list.child(
            el().w_full()
                .h(34.0)
                .radius(RADIUS_CONTROL)
                .fill(t.card_bg)
                .padding(8.0)
                .center_y()
                .child(icon(&icons::WAND, 13.0).color(t.accent_text))
                .child(el().w(8.0))
                .child(text(entry.display_name.clone()).font(font(12.0, 500.0)).color(t.text_primary))
                .child(el().grow(1.0))
                .child(icon(&icons::PLUS, 12.0).color(t.text_muted))
                .on_press(move |s: &mut AppState| add_effect_to_selection(s, &type_id)),
        );
    }
    list
}

fn add_effect_to_selection(s: &mut AppState, type_id: &str) {
    let owner = {
        let editor = s.editor();
        let clip = editor.editor.selection.selected_clip_ids().into_iter().next();
        clip.or_else(|| {
            editor
                .editor
                .selection
                .primary()
                .filter(|key| key.group == fig_timeline::SelectionGroup::Track)
                .map(|key| key.id.clone())
        })
    };
    let Some(owner) = owner else {
        s.toasts.show("Select a clip or track first");
        return;
    };
    let mut effect = fig_timeline::EffectInstance { type_id: type_id.to_string(), ..fig_timeline::EffectInstance::default() };
    effect.type_id = type_id.to_string();
    let editor = s.editor();
    if editor.editor.add_effect(&owner, effect) {
        editor.save = crate::editor_state::SaveState::Dirty;
        s.toasts.show("Effect added");
    } else {
        s.toasts.show("Couldn't add that effect");
    }
}

fn transitions_list(t: &Tokens) -> El {
    let mut list = rune::col().w_full().grow(1.0).min_h(0.0).gap(2.0).scroll(rune::ScrollSpec { stick_bottom: false });
    for entry in fig_fx::transitions::all().iter().take(60) {
        list = list.child(
            el().w_full()
                .h(34.0)
                .radius(RADIUS_CONTROL)
                .fill(t.card_bg)
                .padding(8.0)
                .center_y()
                .child(icon(&icons::BLEND, 13.0).color(t.accent_text))
                .child(el().w(8.0))
                .child(text(entry.display_name.clone()).font(font(12.0, 500.0)).color(t.text_primary))
                .child(el().grow(1.0))
                .child(icon(&icons::PLUS, 12.0).color(t.text_muted))
                .on_press(|s: &mut AppState| s.toasts.show("Drag a transition onto a cut")),
        );
    }
    list
}

fn text_presets(editor: &EditorState, t: &Tokens) -> El {
    let mut list = rune::col().w_full().grow(1.0).min_h(0.0).gap(2.0).scroll(rune::ScrollSpec { stick_bottom: false });
    for preset in fig_timeline::text_presets() {
        let label = preset.name.to_string();
        let preset_copy = preset.clone();
        list = list.child(
            el().w_full()
                .h(38.0)
                .radius(RADIUS_CONTROL)
                .fill(t.card_bg)
                .padding(8.0)
                .center_y()
                .child(icon(&icons::TYPE, 13.0).color(t.accent_text))
                .child(el().w(8.0))
                .child(text(label).font(font(12.0, 500.0)).color(t.text_primary))
                .child(el().grow(1.0))
                .child(icon(&icons::PLUS, 12.0).color(t.text_muted))
                .on_press(move |s: &mut AppState| {
                    let editor = s.editor();
                    let at = editor.playhead;
                    let id = editor.editor.add_text_preset(None, at, &preset_copy);
                    editor.editor.selection.select_only(&id);
                    editor.save = crate::editor_state::SaveState::Dirty;
                }),
        );
    }
    let _ = editor;
    list
}

fn markers_list(editor: &EditorState, t: &Tokens) -> El {
    let markers = editor.editor.all_markers();
    let mut list = rune::col().w_full().grow(1.0).min_h(0.0).gap(2.0).scroll(rune::ScrollSpec { stick_bottom: false });
    if markers.is_empty() {
        return empty_state(&icons::FLAG, "No markers", "Add one with M", t);
    }
    for location in markers.iter().take(40) {
        let id = location.marker.id.clone();
        let name = location.marker.name.clone();
        let time = crate::editor_state::format_timecode(location.marker.start_sec);
        list = list.child(
            el().w_full()
                .h(32.0)
                .radius(RADIUS_CONTROL)
                .fill(t.card_bg)
                .padding(8.0)
                .center_y()
                .child(icon(&icons::FLAG, 12.0).color(t.marker_default))
                .child(el().w(8.0))
                .child(text(name).font(font(12.0, 500.0)).color(t.text_primary))
                .child(el().grow(1.0))
                .child(text(time).font(font(10.0, 400.0)).color(t.text_faint))
                .on_press(move |s: &mut AppState| {
                    s.editor().editor.select_marker(&id, false, false, None);
                }),
        );
    }
    list
}

fn captions_list(editor: &EditorState, t: &Tokens) -> El {
    let mut list = rune::col().w_full().grow(1.0).min_h(0.0).gap(2.0).scroll(rune::ScrollSpec { stick_bottom: false });
    let mut found = 0;
    for track in &editor.doc().tracks {
        for clip in &track.clips {
            if let fig_timeline::ClipBody::Text(body) = &clip.body {
                if !body.is_caption {
                    continue;
                }
                found += 1;
                let id = clip.id.clone();
                let words = body.text.clone();
                list = list.child(
                    el().w_full()
                        .h(32.0)
                        .radius(RADIUS_CONTROL)
                        .fill(t.card_bg)
                        .padding(8.0)
                        .center_y()
                        .child(icon(&icons::CAPTIONS, 12.0).color(t.accent_text))
                        .child(el().w(8.0))
                        .child(text(words).font(font(12.0, 400.0)).color(t.text_primary).truncate())
                        .on_press(move |s: &mut AppState| {
                            s.editor().editor.selection.select_only(&id);
                        }),
                );
            }
        }
    }
    if found == 0 {
        return empty_state(&icons::CAPTIONS, "No captions", "Auto captions…", t);
    }
    list
}
