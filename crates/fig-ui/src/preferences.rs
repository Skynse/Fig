//! Preferences: the centred modal card over the editor (`PreferencesView.axaml`, inventory §1.7).
//!
//! Seven pages, exactly the C# ones: General, Editing, Playback, Export, Keyboard, Rendering, Storage.
//! Every value is read from and written to `~/.fig/preferences.json` in the C# format through
//! [`services`], so a setting changed in either build is the setting the other one sees.

use rune::{col, el, icon, row, text, El};

use crate::app::AppState;
use crate::icons;
use crate::services::{display_gesture, EncoderChoice, RendererChoice};
use crate::theme::{font, mono, Tokens, RADIUS_CONTROL, RADIUS_PANEL};

pub const PAGES: [&str; 7] = ["General", "Editing", "Playback", "Export", "Keyboard", "Rendering", "Storage"];

#[derive(Clone, Copy)]
enum Toggle {
    AskBeforeClosing,
    Snap,
    LinkedSelection,
    AudioScrub,
    Loop,
}

#[derive(Clone, Copy)]
enum Step {
    AutosaveDown,
    AutosaveUp,
    SnapReachDown,
    SnapReachUp,
    UndoDown,
    UndoUp,
    QualityDown,
    QualityUp,
}

pub fn overlay(state: &AppState, t: &Tokens) -> El {
    let page = state.preferences_page.min(PAGES.len() - 1);
    let mut rail = col().w(190.0).h_full().padding(10.0).gap(2.0);
    for (i, label) in PAGES.iter().enumerate() {
        let selected = page == i;
        let mut item = el()
            .w_full()
            .h(32.0)
            .radius(RADIUS_CONTROL)
            .padding(10.0)
            .center_y()
            .child(text(*label).font(font(12.0, 500.0)).color(if selected { t.text_primary } else { t.text_secondary }))
            .on_press(move |s: &mut AppState| s.preferences_page = i);
        if selected {
            item = item.fill(t.hover_bg);
        }
        rail = rail.child(item);
    }

    let content = col()
        .w_full()
        .grow(1.0)
        .h_full()
        .min_w(0.0)
        .padding(18.0)
        .gap(10.0)
        .child(text(PAGES[page]).font(font(16.0, 600.0)).color(t.text_primary))
        .child(crate::widgets::rule(t))
        .child(page_body(page, state, t))
        .child(el().grow(1.0));

    let card = row()
        .w(820.0)
        .h(520.0)
        .fill(t.panel_bg)
        .radius(RADIUS_PANEL)
        .clip_rounded(RADIUS_PANEL)
        .border(1.0, t.border)
        .child(rail)
        .child(el().w(1.0).h_full().fill(t.border))
        .child(content)
        .child(
            icon(&icons::X, 14.0)
                .color(t.text_muted)
                .absolute(786.0, 14.0)
                .on_press(|s: &mut AppState| s.preferences_open = false),
        );

    // the scrim is below the card: a click outside closes, a click on a control must not.
    el().absolute(0.0, 0.0)
        .w_full()
        .h_full()
        .child(
            el().absolute(0.0, 0.0)
                .w_full()
                .h_full()
                .fill(crate::theme::hex("#000000b4"))
                .on_press(|s: &mut AppState| s.preferences_open = false),
        )
        .child(el().absolute(0.0, 0.0).w_full().h_full().center().child(card))
}

fn page_body(page: usize, state: &AppState, t: &Tokens) -> El {
    let prefs = &state.preferences;
    let mut body = col().w_full().gap(2.0);
    match page {
        0 => {
            body = body
                .child(stepper(
                    "Autosave interval",
                    &format!("{:.1} s", prefs.general.autosave_seconds),
                    Step::AutosaveDown,
                    Step::AutosaveUp,
                    t,
                ))
                .child(toggle(
                    "Ask before closing unsaved work",
                    prefs.general.ask_before_closing_unsaved,
                    Toggle::AskBeforeClosing,
                    t,
                ));
        }
        1 => {
            body = body
                .child(toggle("Snapping", prefs.editing.snap, Toggle::Snap, t))
                .child(stepper(
                    "Snap reach",
                    &format!("{:.0} px", prefs.editing.snap_reach_px),
                    Step::SnapReachDown,
                    Step::SnapReachUp,
                    t,
                ))
                .child(toggle("Linked selection", prefs.editing.linked_selection, Toggle::LinkedSelection, t))
                .child(stepper(
                    "Undo steps",
                    &format!("{}", prefs.editing.undo_steps),
                    Step::UndoDown,
                    Step::UndoUp,
                    t,
                ));
        }
        2 => {
            body = body
                .child(toggle("Audio scrubbing", prefs.playback.audio_scrub, Toggle::AudioScrub, t))
                .child(toggle("Loop playback", prefs.playback.loop_playback, Toggle::Loop, t))
                .child(setting("Playback device", "System default", t));
        }
        3 => {
            body = body
                .child(stepper(
                    "Quality (H.264 CRF)",
                    &format!("{}", prefs.export.quality),
                    Step::QualityDown,
                    Step::QualityUp,
                    t,
                ))
                .child(setting("Folder", prefs.export.folder.as_deref().unwrap_or("Videos folder"), t))
                .child(setting("Encoder", encoder_name(prefs.export.encoder), t))
                .child(
                    crate::widgets::button("Cycle encoder", t, false).on_press(|s: &mut AppState| {
                        let prefs = &mut s.preferences;
                        prefs.export.encoder = match prefs.export.encoder {
                            EncoderChoice::Automatic => EncoderChoice::Nvenc,
                            EncoderChoice::Nvenc => EncoderChoice::QuickSync,
                            EncoderChoice::QuickSync => EncoderChoice::Amf,
                            EncoderChoice::Amf => EncoderChoice::Vaapi,
                            EncoderChoice::Vaapi => EncoderChoice::Software,
                            EncoderChoice::Software => EncoderChoice::Automatic,
                        };
                        s.save_preferences();
                    }),
                );
        }
        4 => {
            body = body.child(section_label("Shortcuts", t));
            for shortcut in state.shortcuts.rows.iter().take(14) {
                body = body.child(
                    row()
                        .w_full()
                        .h(24.0)
                        .center_y()
                        .child(text(shortcut.label).font(font(11.0, 400.0)).color(t.text_secondary))
                        .child(el().grow(1.0))
                        .child(text(crate::shortcuts::Shortcuts::gesture_for(prefs, shortcut)).font(mono(11.0, 400.0)).color(t.text_faint)),
                );
            }
        }
        5 => {
            body = body
                .child(setting("Renderer", renderer_name(prefs.rendering.renderer), t))
                .child(
                    crate::widgets::button("Cycle renderer", t, false).on_press(|s: &mut AppState| {
                        let prefs = &mut s.preferences;
                        prefs.rendering.renderer = match prefs.rendering.renderer {
                            RendererChoice::Automatic => RendererChoice::Gpu,
                            RendererChoice::Gpu => RendererChoice::Cpu,
                            RendererChoice::Cpu => RendererChoice::Automatic,
                        };
                        s.save_preferences();
                    }),
                )
                .child(text("Automatic uses the GPU when the window's canvas is GPU-backed.").font(font(11.0, 400.0)).color(t.text_faint));
        }
        _ => {
            let data = crate::services::data_dir();
            body = body
                .child(setting("Data folder", &data.to_string_lossy(), t))
                .child(setting("Projects", &crate::services::projects_dir().to_string_lossy(), t))
                .child(setting("Presets", &crate::services::presets_dir().to_string_lossy(), t))
                .child(setting("Templates", &crate::services::templates_dir().to_string_lossy(), t))
                .child(setting("Logs", &crate::services::log_dir().to_string_lossy(), t))
                .child(setting("Preferences file", &crate::services::preferences_path().to_string_lossy(), t))
                .child(
                    crate::widgets::button("Open data folder", t, false).on_press(|s: &mut AppState| {
                        s.toasts.show(format!("Data folder: {}", crate::services::data_dir().display()));
                    }),
                );
        }
    }
    body
}

fn setting(label: &str, value: &str, t: &Tokens) -> El {
    row()
        .w_full()
        .h(32.0)
        .center_y()
        .child(text(label).font(font(12.0, 400.0)).color(t.text_secondary))
        .child(el().grow(1.0))
        .child(text(value.to_string()).font(font(12.0, 400.0)).color(t.text_muted))
}

fn toggle(label: &str, on: bool, which: Toggle, t: &Tokens) -> El {
    row()
        .w_full()
        .h(32.0)
        .center_y()
        .child(text(label).font(font(12.0, 400.0)).color(t.text_secondary))
        .child(el().grow(1.0))
        .child(
            el().w(32.0)
                .h(18.0)
                .radius(9.0)
                .fill(if on { t.accent } else { t.card_bg_alt })
                .child(
                    el().w(14.0)
                        .h(14.0)
                        .radius(7.0)
                        .fill(rune::Color::WHITE)
                        .absolute(if on { 16.0 } else { 2.0 }, 2.0),
                ),
        )
        .on_press(move |s: &mut AppState| {
            let prefs = &mut s.preferences;
            match which {
                Toggle::AskBeforeClosing => prefs.general.ask_before_closing_unsaved = !prefs.general.ask_before_closing_unsaved,
                Toggle::Snap => prefs.editing.snap = !prefs.editing.snap,
                Toggle::LinkedSelection => prefs.editing.linked_selection = !prefs.editing.linked_selection,
                Toggle::AudioScrub => prefs.playback.audio_scrub = !prefs.playback.audio_scrub,
                Toggle::Loop => prefs.playback.loop_playback = !prefs.playback.loop_playback,
            }
            s.save_preferences();
            if let Some(editor) = s.editor.as_mut() {
                editor.editor.snap.magnetic = s.preferences.editing.snap;
                editor.transport.loop_playback = s.preferences.playback.loop_playback;
                editor.transport.audio_scrub = s.preferences.playback.audio_scrub;
            }
        })
}

fn stepper(label: &str, value: &str, down: Step, up: Step, t: &Tokens) -> El {
    let button = |icon_def: &'static rune::Icon, step: Step| {
        el().w(24.0)
            .h(24.0)
            .radius(6.0)
            .fill(t.card_bg)
            .center()
            .child(icon(icon_def, 12.0).color(t.text_muted))
            .on_press(move |s: &mut AppState| {
                let prefs = &mut s.preferences;
                match step {
                    Step::AutosaveDown => prefs.general.autosave_seconds = (prefs.general.autosave_seconds - 0.5).clamp(0.2, 30.0),
                    Step::AutosaveUp => prefs.general.autosave_seconds = (prefs.general.autosave_seconds + 0.5).clamp(0.2, 30.0),
                    Step::SnapReachDown => prefs.editing.snap_reach_px = (prefs.editing.snap_reach_px - 1.0).clamp(2.0, 40.0),
                    Step::SnapReachUp => prefs.editing.snap_reach_px = (prefs.editing.snap_reach_px + 1.0).clamp(2.0, 40.0),
                    Step::UndoDown => prefs.editing.undo_steps = (prefs.editing.undo_steps - 10).clamp(10, 1000),
                    Step::UndoUp => prefs.editing.undo_steps = (prefs.editing.undo_steps + 10).clamp(10, 1000),
                    Step::QualityDown => prefs.export.quality = (prefs.export.quality - 1).clamp(0, 51),
                    Step::QualityUp => prefs.export.quality = (prefs.export.quality + 1).clamp(0, 51),
                }
                s.save_preferences();
                if let Some(editor) = s.editor.as_mut() {
                    editor.editor.snap.magnetic = s.preferences.editing.snap;
                    editor.editor.snap.window_sec =
                        s.preferences.editing.snap_reach_px / f64::from(editor.timeline.pps.max(1.0));
                }
            })
    };
    row()
        .w_full()
        .h(32.0)
        .center_y()
        .child(text(label).font(font(12.0, 400.0)).color(t.text_secondary))
        .child(el().grow(1.0))
        .child(text(value.to_string()).font(font(12.0, 400.0)).color(t.text_muted))
        .child(el().w(8.0))
        .child(button(&icons::MINUS, down))
        .child(el().w(4.0))
        .child(button(&icons::PLUS, up))
}

fn section_label(label: &str, t: &Tokens) -> El {
    text(label).font(font(11.0, 600.0)).color(t.text_muted).padding(0.0).w_full()
}

fn encoder_name(encoder: EncoderChoice) -> &'static str {
    match encoder {
        EncoderChoice::Automatic => "Automatic",
        EncoderChoice::Nvenc => "NVIDIA NVENC",
        EncoderChoice::QuickSync => "Intel Quick Sync",
        EncoderChoice::Amf => "AMD AMF",
        EncoderChoice::Vaapi => "VA-API",
        EncoderChoice::Software => "libx264 (CPU)",
    }
}

fn renderer_name(renderer: RendererChoice) -> &'static str {
    match renderer {
        RendererChoice::Automatic => "Automatic",
        RendererChoice::Gpu => "GPU shaders",
        RendererChoice::Cpu => "CPU",
    }
}

/// Kept for the shortcut dialog's display formatting.
pub fn gesture_text(gesture: &str) -> String {
    display_gesture(gesture)
}
