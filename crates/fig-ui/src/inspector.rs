//! The inspector panel: what is selected, and the facet sections that edit it.
//!
//! The C# inspector never asks what kind of thing is selected: `PropertiesViewModel` intersects the
//! facets of the selection and `InspectorSections` returns one section per shared facet (inventory
//! §4.4). This module follows that shape: `resolve` the selection, then render sections for the
//! facets it has. Sliders mutate the live document (`begin_live`/`live_doc`/`commit_live`) so a
//! drag is one undo step, exactly like the timeline gestures.

use fig_edit::Selected;
use fig_timeline::{Clip, Marker, Track};
use rune::{Drag, El, Phase, Tx, col, el, icon, row, text};

use crate::app::AppState;
use crate::editor_state::EditorState;
use crate::icons;
use crate::theme::{font, mono, Tokens, RADIUS_CARD, RADIUS_CONTROL, RADIUS_PANEL};

pub struct InspectorState {
    /// Which facet tab is shown when the selection offers several (`Effects`, `Track`, ...).
    pub tab: usize,
}

impl InspectorState {
    pub fn new() -> InspectorState {
        InspectorState { tab: 0 }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Prop {
    Opacity,
    Volume,
    FadeIn,
    FadeOut,
}

pub fn panel(editor: &EditorState, t: &Tokens) -> El {
    let selected = editor.editor.selection.primary().and_then(|key| editor.editor.resolve(key));
    let mut body = col()
        .w_full()
        .h_full()
        .min_w(0.0)
        .fill(t.panel_bg)
        .radius(RADIUS_PANEL)
        .clip_rounded(RADIUS_PANEL)
        .padding(12.0)
        .gap(6.0)
        .child(
            row()
                .w_full()
                .h(24.0)
                .center_y()
                .child(text("Inspector").font(font(14.0, 600.0)).color(t.text_primary))
                .child(el().grow(1.0))
                .child(icon(&icons::CHEVRON_RIGHT, 14.0).color(t.text_muted)),
        );

    match selected {
        Some(Selected::Clip(clip)) => body = clip_sections(body, editor, &clip, t),
        Some(Selected::Track(track)) => body = track_sections(body, editor, &track, t),
        Some(Selected::Marker(marker)) => body = marker_sections(body, &marker, t),
        Some(Selected::Transition(cut)) => {
            body = body
                .child(text("Transition").font(font(11.0, 400.0)).color(t.text_muted))
                .child(section(t, "Transition", true))
                .child(value_row("Type", &cut.type_id, t))
                .child(value_row("Duration", &format!("{:.2} s", cut.duration_sec), t));
        }
        Some(Selected::Effect { effect, .. }) => {
            let name = fig_fx::effects::find(&effect.type_id).map_or_else(|| effect.type_id.clone(), |e| e.display_name.clone());
            body = body
                .child(text("Effect").font(font(11.0, 400.0)).color(t.text_muted))
                .child(section(t, &name, true))
                .child(value_row("Enabled", if effect.enabled { "Yes" } else { "No" }, t));
        }
        Some(Selected::Media(id)) => {
            body = body.child(text("Media").font(font(11.0, 400.0)).color(t.text_muted)).child(section(t, "Media", true));
            body = body.child(value_row("Library item", &id, t));
        }
        None => {
            body = body
                .child(text("No selection").font(font(11.0, 400.0)).color(t.text_muted))
                .child(
                    col()
                        .w_full()
                        .grow(1.0)
                        .min_h(0.0)
                        .center()
                        .gap(12.0)
                        .child(
                            el().w(40.0)
                                .h(40.0)
                                .radius(RADIUS_CARD)
                                .fill(t.chip_bg)
                                .center()
                                .child(icon(&icons::PANEL_RIGHT, 20.0).color(t.text_muted)),
                        )
                        .child(text("Nothing selected").font(font(13.0, 600.0)).color(t.text_secondary))
                        .child(
                            text("Select a clip, track, marker, transition, or media item to edit its settings.")
                                .font(font(11.0, 400.0))
                                .color(t.text_muted)
                                .w(170.0)
                                .wrap(),
                        ),
                );
        }
    }
    body
}

// ---------------- sections ----------------

fn section_header(t: &Tokens, title: &str) -> El {
    row()
        .w_full()
        .h(28.0)
        .center_y()
        .child(text(title).font(font(12.0, 600.0)).color(t.text_secondary))
}

fn section(t: &Tokens, title: &str, open: bool) -> El {
    let mut header = section_header(t, title);
    if open {
        header = header.child(el().grow(1.0)).child(icon(&icons::CHEVRON_RIGHT, 12.0).color(t.text_faint));
    }
    header
}

fn clip_sections(mut body: El, editor: &EditorState, clip: &Clip, t: &Tokens) -> El {
    body = body
        .child(text(clip_display_name(clip)).font(font(11.0, 400.0)).color(t.text_muted))
        .child(section(t, "Clip", true))
        .child(value_row("Start", &crate::editor_state::format_timecode(clip.start_sec), t))
        .child(value_row("Duration", &crate::editor_state::format_timecode(clip.dur_sec), t))
        .child(value_row("Speed", &format!("{:.2}×", clip.speed), t));

    if matches!(clip.kind(), fig_timeline::ClipKind::Video | fig_timeline::ClipKind::Image) {
        body = body
            .child(section(t, "Picture", true))
            .child(slider_row(editor, clip, Prop::Opacity, "Opacity", clip.opacity, t));
    }
    if matches!(clip.kind(), fig_timeline::ClipKind::Video | fig_timeline::ClipKind::Audio) {
        body = body
            .child(section(t, "Sound", true))
            .child(slider_row(editor, clip, Prop::Volume, "Volume", clip.volume, t))
            .child(slider_row(editor, clip, Prop::FadeIn, "Fade in", (clip.fade_in_sec / 2.0).clamp(0.0, 1.0), t))
            .child(slider_row(editor, clip, Prop::FadeOut, "Fade out", (clip.fade_out_sec / 2.0).clamp(0.0, 1.0), t));
    }

    body = body.child(section(t, "Effects", true));
    if clip.effects.is_empty() {
        body = body.child(
            text("No effects").font(font(11.0, 400.0)).color(t.text_faint).padding(4.0),
        );
    } else {
        for effect in &clip.effects {
            let name = fig_fx::effects::find(&effect.type_id).map_or_else(|| effect.type_id.clone(), |e| e.display_name.clone());
            body = body.child(effect_row(&name, effect.enabled, t));
        }
    }
    body.child(
        row()
            .w_full()
            .h(28.0)
            .radius(RADIUS_CONTROL)
            .border(1.0, t.border)
            .center()
            .child(text("+ Add effect").font(font(11.0, 500.0)).color(t.text_secondary))
            .on_press(|s: &mut AppState| s.toasts.show("Add effect")),
    )
}

fn clip_display_name(clip: &Clip) -> String {
    match clip.kind() {
        fig_timeline::ClipKind::Video => "Video clip",
        fig_timeline::ClipKind::Audio => "Audio clip",
        fig_timeline::ClipKind::Text => "Text clip",
        fig_timeline::ClipKind::Image => "Image clip",
    }
    .to_string()
}

fn track_sections(mut body: El, editor: &EditorState, track: &Track, t: &Tokens) -> El {
    body = body
        .child(text(editor.editor.track_label(&track.id)).font(font(11.0, 400.0)).color(t.text_muted))
        .child(section(t, "Track", true))
        .child(value_row("Name", track.name.as_deref().unwrap_or("—"), t))
        .child(toggle_row(
            "Visible",
            track.visible,
            t,
            |s: &mut AppState, _| {
                let id = s.editor().editor.selection.primary().map(|k| k.id.clone());
                if let Some(id) = id {
                    let visible = s.editor().editor.find_track(&id).is_some_and(|t| t.visible);
                    s.editor().editor.set_track_visible(&id, !visible);
                }
            },
        ))
        .child(toggle_row(
            "Muted",
            track.muted,
            t,
            |s: &mut AppState, _| {
                let id = s.editor().editor.selection.primary().map(|k| k.id.clone());
                if let Some(id) = id {
                    let muted = s.editor().editor.find_track(&id).is_some_and(|t| t.muted);
                    s.editor().editor.set_track_muted(&id, !muted);
                }
            },
        ));
    body.child(section(t, "Effects", true))
}

fn marker_sections(mut body: El, marker: &Marker, t: &Tokens) -> El {
    let range = if marker.dur_sec > 0.0 { format!("{:.2} s", marker.dur_sec) } else { "Point marker".to_string() };
    body = body
        .child(text(marker.name.clone()).font(font(11.0, 400.0)).color(t.text_muted))
        .child(section(t, "Marker", true))
        .child(value_row("Name", &marker.name, t))
        .child(value_row("Time", &crate::editor_state::format_timecode(marker.start_sec), t))
        .child(value_row("Color", &marker.color, t))
        .child(value_row("Range", &range, t));
    body
}

// ---------------- rows ----------------

fn value_row(label: &str, value: &str, t: &Tokens) -> El {
    row()
        .w_full()
        .h(28.0)
        .center_y()
        .child(text(label).font(font(11.0, 400.0)).color(t.text_secondary))
        .child(el().grow(1.0))
        .child(text(value).font(mono(11.0, 400.0)).color(t.text_muted))
}

fn effect_row(name: &str, enabled: bool, t: &Tokens) -> El {
    row()
        .w_full()
        .h(30.0)
        .radius(RADIUS_CONTROL)
        .fill(t.card_bg)
        .padding(8.0)
        .center_y()
        .child(icon(&icons::WAND, 13.0).color(t.accent_text))
        .child(el().w(8.0))
        .child(text(name).font(font(11.0, 500.0)).color(if enabled { t.text_primary } else { t.text_muted }))
        .child(el().grow(1.0))
        .child(text("fx").font(font(10.0, 400.0)).color(t.text_faint))
}

fn toggle_row(label: &str, value: bool, t: &Tokens, action: fn(&mut AppState, bool)) -> El {
    let knob = el().w(30.0)
        .h(16.0)
        .radius(8.0)
        .fill(if value { t.accent } else { t.card_bg_alt })
        .child(
            el().w(12.0)
                .h(12.0)
                .radius(6.0)
                .fill(rune::Color::WHITE)
                .absolute(if value { 16.0 } else { 2.0 }, 2.0),
        );
    row()
        .w_full()
        .h(28.0)
        .center_y()
        .child(text(label).font(font(11.0, 400.0)).color(t.text_secondary))
        .child(el().grow(1.0))
        .child(knob)
        .on_press(move |s: &mut AppState| action(s, value))
}

/// A slider that edits one clip property by dragging; the whole drag is one undo step.
fn slider_row(editor: &EditorState, clip: &Clip, prop: Prop, label: &str, shown: f64, t: &Tokens) -> El {
    let value = shown.clamp(0.0, 1.0) as f32;
    let key = format!("inspector.slider.{label}");
    let track = row()
        .w_full()
        .h(22.0)
        .center_y()
        .child(el().h(3.0).grow(value.max(0.001)).fill(t.accent))
        .child(el().w(8.0).h(8.0).radius(4.0).fill(rune::Color::WHITE))
        .child(el().h(3.0).grow((1.0 - value).max(0.001)).fill(t.card_bg_alt));

    let clip_id = clip.id.clone();
    let slider = row()
        .key(key.clone())
        .w_full()
        .h(22.0)
        .center_y()
        .on_drag(move |s: &mut AppState, drag: &mut Drag, _tx: &mut Tx| {
            let width = rune::memo::layout_size(&key).map_or(120.0, |size| size[0]).max(1.0);
            match drag.phase {
                Phase::Down => {
                    s.editor().editor.begin_live();
                    apply(s, &clip_id, prop, f64::from((drag.x / width).clamp(0.0, 1.0)));
                }
                Phase::Move => apply(s, &clip_id, prop, f64::from((drag.x / width).clamp(0.0, 1.0))),
                Phase::Up => {
                    s.editor().editor.commit_live();
                    s.editor().save = crate::editor_state::SaveState::Dirty;
                }
            }
        })
        .child(track);

    col()
        .w_full()
        .gap(2.0)
        .padding(0.0)
        .child(shown_label(label, shown, t))
        .child(slider)
}

fn shown_label(label: &str, shown: f64, t: &Tokens) -> El {
    let value = match label {
        "Opacity" | "Volume" => format!("{:.0}%", shown * 100.0),
        _ => format!("{:.2} s", shown * 2.0),
    };
    row()
        .w_full()
        .center_y()
        .child(text(label).font(font(11.0, 400.0)).color(t.text_secondary))
        .child(el().grow(1.0))
        .child(text(value).font(mono(11.0, 400.0)).color(t.text_muted))
}

fn apply(app: &mut AppState, clip_id: &str, prop: Prop, value: f64) {
    let editor = app.editor();
    let Some(clip) = editor.editor.live_doc().find_clip_mut(clip_id) else { return };
    match prop {
        Prop::Opacity => clip.opacity = value,
        Prop::Volume => clip.volume = value,
        Prop::FadeIn => clip.fade_in_sec = value * 2.0,
        Prop::FadeOut => clip.fade_out_sec = value * 2.0,
    }
    editor.save = crate::editor_state::SaveState::Dirty;
    editor.ui_revision += 1;
}
