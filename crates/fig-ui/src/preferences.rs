//! Preferences (`PreferencesView.axaml`): a card over the window with seven pages. Every value lives in
//! `~/.fig/preferences.json`, in the C# build's format, saved as soon as it changes.

use std::rc::Rc;

use rune::{Color, El, KeyEvent, col, el, icon, row, text};

use crate::app::AppState;
use crate::commands::CATALOG;
use crate::icons;
use crate::settings::{EncoderChoice, display_gesture};
use crate::theme::{Tokens, font, hex, mono};
use crate::widgets::{self, Kind};

#[derive(Default)]
pub struct PreferencesState {
    pub page: usize,
    /// The shortcut being recorded (its catalog id).
    pub recording: Option<&'static str>,
}

const PAGES: [(&str, &rune::Icon); 7] = [
    ("General", &icons::SETTINGS),
    ("Editing", &icons::SCISSORS),
    ("Playback", &icons::PLAY),
    ("Export", &icons::UPLOAD),
    ("Keyboard", &icons::KEYBOARD),
    ("Rendering", &icons::MONITOR),
    ("Storage", &icons::HARD_DRIVE),
];

fn setting(name: &str, note: &str, control: El, t: &Tokens) -> El {
    row()
        .w_full()
        .py(10.0)
        .gap(16.0)
        .center_y()
        .child(col().grow(1.0).min_w(0.0).gap(2.0).child(text(name.to_string()).font(font(13.0, 600.0)).color(t.text_primary)).child(widgets::note(note.to_string(), t)))
        .child(control)
}

fn toggle(on: bool, f: fn(&mut AppState), t: &Tokens) -> El {
    widgets::switch(on, t).on_press(move |s: &mut AppState| {
        f(s);
        s.save_preferences();
    })
}

fn stepper(value: String, down: fn(&mut AppState), up: fn(&mut AppState), t: &Tokens) -> El {
    row()
        .gap(4.0)
        .center_y()
        .child(widgets::icon_button(&icons::MINUS, 12.0, 26.0, t).on_press(move |s: &mut AppState| {
            down(s);
            s.save_preferences();
        }))
        .child(text(value).font(mono(12.0, 400.0)).color(t.text_secondary).min_w(64.0).align(rune::Align::Center))
        .child(widgets::icon_button(&icons::PLUS, 12.0, 26.0, t).on_press(move |s: &mut AppState| {
            up(s);
            s.save_preferences();
        }))
}

pub fn overlay(app: &AppState, st: &PreferencesState, t: &Tokens) -> El {
    let p = &app.preferences;
    let mut rail = col().w(190.0).gap(2.0);
    for (i, (name, glyph)) in PAGES.iter().enumerate() {
        let on = st.page == i;
        rail = rail.child(
            row()
                .w_full()
                .h(34.0)
                .px(10.0)
                .gap(10.0)
                .radius(8.0)
                .fill(if on { hex("#2a2d34") } else { Color::TRANSPARENT })
                .hover(t.chip_bg)
                .center_y()
                .child(icon(glyph, 15.0).color(if on { t.text_primary } else { t.text_muted }))
                .child(text(*name).font(font(13.0, 500.0)).color(if on { t.text_primary } else { t.text_secondary }))
                .on_press(move |s: &mut AppState| {
                    if let Some(p) = s.preferences_open.as_mut() {
                        p.page = i;
                        p.recording = None;
                    }
                }),
        );
    }
    let page = match st.page {
        0 => col()
            .child(setting(
                "Autosave after",
                "How long after a change the project saves itself.",
                stepper(
                    format!("{:.1} s", p.general.autosave_seconds),
                    |s| s.preferences.general.autosave_seconds = (s.preferences.general.autosave_seconds - 0.2).max(0.2),
                    |s| s.preferences.general.autosave_seconds = (s.preferences.general.autosave_seconds + 0.2).min(30.0),
                    t,
                ),
                t,
            ))
            .child(setting(
                "Ask before closing unsaved projects",
                "Projects save themselves, so this only matters if a save failed.",
                toggle(p.general.ask_before_closing_unsaved, |s| s.preferences.general.ask_before_closing_unsaved ^= true, t),
                t,
            )),
        1 => col()
            .child(setting("Snap", "Clips, markers and the playhead catch on edges, markers and the grid while dragged.", toggle(p.editing.snap, |s| s.preferences.editing.snap ^= true, t), t))
            .child(setting(
                "Snap distance",
                "How close (in pixels) something must come to catch.",
                stepper(
                    format!("{:.0} px", p.editing.snap_reach_px),
                    |s| s.preferences.editing.snap_reach_px = (s.preferences.editing.snap_reach_px - 1.0).max(2.0),
                    |s| s.preferences.editing.snap_reach_px = (s.preferences.editing.snap_reach_px + 1.0).min(40.0),
                    t,
                ),
                t,
            ))
            .child(setting("Select linked clips together", "Clicking a video also selects its sound (Alt-click picks one).", toggle(p.editing.linked_selection, |s| s.preferences.editing.linked_selection ^= true, t), t))
            .child(setting(
                "Undo steps",
                "How many edits can be undone. Takes effect for the next project opened.",
                stepper(p.editing.undo_steps.to_string(), |s| s.preferences.editing.undo_steps = (s.preferences.editing.undo_steps - 10).max(10), |s| s.preferences.editing.undo_steps = (s.preferences.editing.undo_steps + 10).min(1000), t),
                t,
            )),
        2 => col()
            .child(setting("Audio scrubbing", "Hear the sound while dragging the playhead.", toggle(p.playback.audio_scrub, |s| {
                s.preferences.playback.audio_scrub ^= true;
                let on = s.preferences.playback.audio_scrub;
                if let Some(e) = s.editor.as_mut() {
                    e.audio_scrub = on;
                }
            }, t), t))
            .child(setting("Loop playback", "Start over at the end.", toggle(p.playback.loop_playback, |s| {
                s.preferences.playback.loop_playback ^= true;
                let on = s.preferences.playback.loop_playback;
                if let Some(e) = s.editor.as_mut() {
                    e.loop_playback = on;
                }
            }, t), t))
            .child(setting(
                "Sound",
                if app.playback.has_sound() { "Playing through the default output." } else { "No sound device: this build has no audio (it needs alsa-lib-devel and --features audio)." },
                el(),
                t,
            )),
        3 => col()
            .child(setting(
                "Quality",
                "Constant quality (CRF) exports start at: lower is better and bigger.",
                stepper(format!("CRF {}", p.export.quality), |s| s.preferences.export.quality = (s.preferences.export.quality - 1).max(0), |s| s.preferences.export.quality = (s.preferences.export.quality + 1).min(51), t),
                t,
            ))
            .child(setting("Export folder", &p.export.folder.clone().unwrap_or_else(|| "~/Videos".into()), widgets::button("Reset", Kind::Ghost, t).on_press(|s: &mut AppState| {
                s.preferences.export.folder = None;
                s.save_preferences();
            }), t))
            .child(setting(
                "Encoder",
                "Automatic uses the graphics card's encoder when there is one.",
                widgets::dropdown(
                    "prefs.encoder",
                    format!("{:?}", p.export.encoder),
                    t,
                )
                .w(160.0)
                .on_press(|s: &mut AppState| {
                    let items = [EncoderChoice::Automatic, EncoderChoice::Nvenc, EncoderChoice::QuickSync, EncoderChoice::Amf, EncoderChoice::Vaapi, EncoderChoice::Software]
                        .into_iter()
                        .map(|c| {
                            crate::menu::Item::new(format!("{c:?}"), move |s, _| {
                                s.preferences.export.encoder = c;
                                s.save_preferences();
                            })
                        })
                        .collect();
                    crate::menu::open_below(s, "prefs.encoder", items);
                }),
                t,
            )),
        4 => keyboard_page(app, st, t),
        5 => col().child(setting("Renderer", "Fig draws the preview and exports with the same CPU renderer (every core), so what you see is what you export.", el(), t)),
        _ => storage_page(app, t),
    };
    let card = row()
        .w(860.0)
        .h(520.0)
        .padding(20.0)
        .gap(20.0)
        .radius(14.0)
        .fill(t.panel_bg)
        .border(1.0, t.border)
        .shadow(16.0, 48.0, Color::BLACK.alpha(0.5))
        .child(col().gap(10.0).child(text("Preferences").font(font(15.0, 600.0)).color(t.text_primary).pb(6.0)).child(rail))
        .child(
            col()
                .grow(1.0)
                .min_w(0.0)
                .h_full()
                .child(
                    row()
                        .w_full()
                        .justify_end()
                        .gap(6.0)
                        .child(widgets::button("Reset to defaults", Kind::Ghost, t).on_press(|s: &mut AppState| {
                            let keep = s.preferences.unknown.clone();
                            s.preferences = crate::settings::Preferences::default();
                            s.preferences.unknown = keep;
                            s.save_preferences();
                        }))
                        .child(widgets::icon_button(&icons::X, 14.0, 28.0, t).on_press(|s: &mut AppState| s.preferences_open = None)),
                )
                .child(col().w_full().grow(1.0).min_h(0.0).scroll(rune::ScrollSpec { stick_bottom: false }).child(page.w_full())),
        );
    el().absolute(0.0, 46.0).w_full().h_full().fill(Color::BLACK.alpha(0.35)).center().child(card.modal(true, |s: &mut AppState| s.preferences_open = None))
}

fn keyboard_page(app: &AppState, st: &PreferencesState, t: &Tokens) -> El {
    let mut c = col().w_full().gap(2.0).child(widgets::note("Click a shortcut, then press the keys to use. Backspace clears it.", t).pb(8.0));
    let mut section = "";
    for e in CATALOG {
        if e.section != section {
            section = e.section;
            c = c.child(text(section).font(font(12.0, 600.0)).color(t.text_muted).pt(10.0));
        }
        let current = app.preferences.keyboard.shortcuts.get(e.id).cloned().unwrap_or_else(|| e.default.to_string());
        let recording = st.recording == Some(e.id);
        let id = e.id;
        let changed = app.preferences.keyboard.shortcuts.contains_key(e.id);
        c = c.child(
            row()
                .w_full()
                .h(32.0)
                .gap(8.0)
                .center_y()
                .child(text(e.label).font(font(12.5, 400.0)).color(t.text_secondary).grow(1.0))
                .child(
                    el().min_w(140.0)
                        .h(26.0)
                        .px(10.0)
                        .radius(6.0)
                        .fill(if recording { t.accent_soft } else { t.card_bg })
                        .border(1.0, if recording { t.accent } else { t.border })
                        .hover(t.hover_bg)
                        .center()
                        .child(text(if recording { "Press keys…".to_string() } else if current.is_empty() { "None".into() } else { display_gesture(&current) }).font(mono(11.5, 400.0)).color(if changed { t.accent_text } else { t.text_secondary }))
                        .on_press(move |s: &mut AppState| {
                            if let Some(p) = s.preferences_open.as_mut() {
                                p.recording = Some(id);
                            }
                        }),
                )
                .children(changed.then(|| {
                    widgets::icon_button(&icons::ROTATE_CCW, 12.0, 24.0, t).help("Back to the default").on_press(move |s: &mut AppState| {
                        s.preferences.keyboard.shortcuts.remove(id);
                        s.save_preferences();
                    })
                })),
        );
    }
    c
}

/// A key pressed while recording a shortcut: stored in the C# gesture syntax.
pub fn record_key(app: &mut AppState, ev: &KeyEvent) -> bool {
    let Some(id) = app.preferences_open.as_mut().and_then(|p| p.recording.take()) else { return false };
    let key = match &ev.key {
        rune::Key::Backspace => {
            app.preferences.keyboard.shortcuts.insert(id.to_string(), String::new());
            app.save_preferences();
            return true;
        }
        rune::Key::Command(c) => name_of(*c),
        rune::Key::Text(t) if t == " " => "Space".into(),
        rune::Key::Text(t) => match t.chars().next() {
            Some(c) => name_of(c),
            None => return true,
        },
        rune::Key::Left => "Left".into(),
        rune::Key::Right => "Right".into(),
        rune::Key::Up => "Up".into(),
        rune::Key::Down => "Down".into(),
        rune::Key::Home => "Home".into(),
        rune::Key::End => "End".into(),
        rune::Key::PageUp => "PageUp".into(),
        rune::Key::PageDown => "PageDown".into(),
        rune::Key::Delete => "Delete".into(),
        rune::Key::Enter => "Enter".into(),
        rune::Key::Tab => "Tab".into(),
        rune::Key::Function(n) => format!("F{n}"),
        _ => return true,
    };
    let mut g = String::new();
    if ev.mods.cmd {
        g.push_str("Ctrl+");
    }
    if ev.mods.shift {
        g.push_str("Shift+");
    }
    if ev.mods.alt {
        g.push_str("Alt+");
    }
    g.push_str(&key);
    app.preferences.keyboard.shortcuts.insert(id.to_string(), g);
    app.save_preferences();
    true
}

fn name_of(c: char) -> String {
    match c {
        ',' => "OemComma".into(),
        '.' => "OemPeriod".into(),
        '+' | '=' => "OemPlus".into(),
        '-' => "OemMinus".into(),
        '/' => "OemQuestion".into(),
        ';' => "OemSemicolon".into(),
        '[' => "OemOpenBrackets".into(),
        ']' => "OemCloseBrackets".into(),
        c if c.is_ascii_digit() => format!("D{c}"),
        c => c.to_ascii_uppercase().to_string(),
    }
}

fn dir_size(path: &std::path::Path) -> u64 {
    let Ok(rd) = std::fs::read_dir(path) else { return 0 };
    rd.flatten().map(|e| match e.metadata() {
        Ok(m) if m.is_dir() => dir_size(&e.path()),
        Ok(m) => m.len(),
        Err(_) => 0,
    }).sum()
}

fn storage_page(app: &AppState, t: &Tokens) -> El {
    let models = crate::settings::speech_models_dir();
    let caches: u64 = app.store.list_projects().iter().map(|p| dir_size(&app.store.cache_directory(&p.id))).sum();
    let mb = |b: u64| format!("{:.0} MB", b as f64 / 1e6);
    let _ = Rc::new(());
    col()
        .child(setting("Previews and proxies", &format!("{} across all projects. They are made again when needed.", mb(caches)), widgets::button("Clear", Kind::Normal, t).on_press(|s: &mut AppState| {
            let ids: Vec<String> = s.store.list_projects().into_iter().map(|p| p.id).collect();
            let open = s.editor.as_ref().map(|e| e.id.clone());
            for id in ids.into_iter().filter(|id| Some(id) != open.as_ref()) {
                let _ = std::fs::remove_dir_all(s.store.cache_directory(&id));
            }
            crate::toasts::show(s, "Cleared (the open project's previews are kept)");
        }), t))
        .child(setting("Speech models", &format!("{} in {}", mb(dir_size(&models)), models.display()), el(), t))
}
