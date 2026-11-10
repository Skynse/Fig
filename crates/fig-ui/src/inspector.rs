//! The inspector (`PropertiesViewModel`): the settings of what's selected, in tabs by kind (Text, Picture, Position,
//! Audio, Speed, Fades, Effects, Track, Marker, Transition, File). Clip settings are generic: every property the core
//! defines (`fig_timeline::properties`) gets a row of the right kind, with keyframe buttons when it can animate.

use std::rc::Rc;

use fig_timeline::properties::{self, ClipProperty};
use fig_timeline::{Clip, Facet, ParamDef, ParamKind, ParamValue, SelectionGroup, clip_time};
use rune::{Color, El, Phase, TextEdit, Tx, col, el, icon, row, text, text_input};

use crate::app::AppState;
use crate::editor::Editor;
use crate::icons;
use crate::menu::Item;
use crate::theme::{Tokens, font, hex, mono};
use crate::widgets::{self, Kind, OnValue};
use indexmap::IndexMap;

#[derive(Default)]
pub struct InspectorState {
    /// The tab picked by hand; else the first one the selection has.
    pub tab: Option<Facet>,
    /// The text clip's words being typed, and which clip they are for.
    pub text: Option<(String, TextEdit)>,
    /// A marker's name being typed.
    pub marker_name: Option<(String, TextEdit)>,
    /// The effect picked in the Effects tab.
    pub effect: Option<String>,
    /// The tempo match, while open: the song's and the clip's BPM being typed, and half/double time allowed.
    pub tempo: Option<Tempo>,
}

pub struct Tempo {
    pub song: TextEdit,
    pub clip: TextEdit,
    pub half_or_double: bool,
}

fn bpm(text: &str) -> Option<f64> {
    text.trim().trim_end_matches(|c: char| c.is_ascii_alphabetic() || c == ' ').trim().replace(',', ".").parse().ok()
}

const TABS: [(Facet, &str, &rune::Icon); 11] = [
    (Facet::TEXT, "Text", &icons::TYPE),
    (Facet::PICTURE, "Picture", &icons::IMAGE),
    (Facet::LAYOUT, "Position", &icons::MOVE),
    (Facet::SOUND, "Audio", &icons::VOLUME_2),
    (Facet::RETIME, "Speed", &icons::GAUGE),
    (Facet::FADES, "Fades", &icons::BLEND),
    (Facet::EFFECTS, "Effects", &icons::SPARKLES),
    (Facet::TRACK, "Track", &icons::LAYERS),
    (Facet::MARKER, "Marker", &icons::BOOKMARK),
    (Facet::TRANSITION, "Transition", &icons::ARROW_RIGHT_LEFT),
    (Facet::SOURCE, "File", &icons::FILE),
];

pub fn collapsed(t: &Tokens) -> El {
    col().w_full().h_full().pt(10.0).items_center().child(
        widgets::icon_button(&icons::CHEVRON_LEFT, 14.0, 28.0, t)
            .help("Show the inspector (Ctrl+2)")
            .on_press_tx(|s: &mut AppState, tx: &mut Tx| crate::actions::run(s, crate::commands::Command::Inspector, tx)),
    )
}

/// Keeps the inspector's typed fields in step with the selection (from `poll`).
pub fn sync(app: &mut AppState) {
    let Some(e) = app.editor.as_mut() else { return };
    let text_clip = e.ed.selection.selected_clip_ids().into_iter().find(|id| e.ed.find_clip(id).is_some_and(|c| c.as_text().is_some()));
    match text_clip {
        Some(id) => {
            let words = e.ed.find_clip(&id).and_then(|c| c.as_text()).map(|t| t.text.clone()).unwrap_or_default();
            let same = e.inspector.text.as_ref().is_some_and(|(cid, edit)| *cid == id && (edit.focused || edit.text == words));
            if !same {
                e.inspector.text = Some((id, TextEdit::new(words)));
            }
        }
        None => e.inspector.text = None,
    }
    let marker = e.ed.selection.selected_marker_id().map(str::to_string);
    match marker.and_then(|id| fig_edit::markers::find_marker(e.doc(), &id)) {
        Some(loc) => {
            let same = e.inspector.marker_name.as_ref().is_some_and(|(mid, edit)| *mid == loc.marker.id && (edit.focused || edit.text == loc.marker.name));
            if !same {
                e.inspector.marker_name = Some((loc.marker.id.clone(), TextEdit::new(loc.marker.name.clone())));
            }
        }
        None => e.inspector.marker_name = None,
    }
}

pub fn view(app: &AppState, e: &Editor, t: &Tokens) -> El {
    let facets = e.ed.shared_facets();
    let (title, subtitle) = e.ed.describe_selection();
    let header = row()
        .w_full()
        .pb(9.0)
        .gap(8.0)
        .center_y()
        .child(
            col()
                .grow(1.0)
                .min_w(0.0)
                .child(text(if e.ed.selection.is_empty() { "Inspector".to_string() } else { title }).font(font(14.0, 600.0)).color(t.text_primary).truncate())
                .child(text(subtitle).font(font(11.0, 400.0)).color(t.text_muted).truncate()),
        )
        .child(
            widgets::icon_button(&icons::CHEVRON_RIGHT, 14.0, 26.0, t)
                .help("Hide the inspector (Ctrl+2)")
                .on_press_tx(|s: &mut AppState, tx: &mut Tx| crate::actions::run(s, crate::commands::Command::Inspector, tx)),
        );
    let mut body = col().w_full().h_full().padding(12.0).gap(6.0).child(header).child(widgets::rule(t));
    if e.ed.selection.is_empty() || facets.is_empty() {
        return body.child(
            col()
                .w_full()
                .grow(1.0)
                .center()
                .gap(8.0)
                .child(el().w(48.0).h(48.0).radius(24.0).fill(t.card_bg).center().child(icon(&icons::PANELS_TOP_LEFT, 22.0).color(t.text_muted)))
                .child(text("Nothing selected").font(font(13.0, 600.0)).color(t.text_secondary))
                .child(
                    text("Select a clip, track, marker, transition, or media item to edit its settings.")
                        .font(font(12.0, 400.0))
                        .color(t.text_faint)
                        .wrap()
                        .max_w(230.0)
                        .align(rune::Align::Center),
                ),
        );
    }
    let tabs: Vec<_> = TABS.iter().filter(|(f, _, _)| facets.contains(*f)).collect();
    let current = e.inspector.tab.filter(|f| facets.contains(*f)).or_else(|| tabs.first().map(|x| x.0)).unwrap_or(Facet::empty());
    let mut strip = row().w_full().padding(3.0).gap(2.0).radius(10.0).fill(t.card_bg);
    for &&(facet, name, glyph) in &tabs {
        let on = facet == current;
        strip = strip.child(
            el().h(30.0)
                .grow(1.0)
                .radius(8.0)
                .fill(if on { t.hover_bg } else { Color::TRANSPARENT })
                .hover(t.chip_bg)
                .center()
                .help(name.to_string())
                .child(icon(glyph, 15.0).color(if on { t.text_primary } else { t.text_muted }))
                .on_press(move |s: &mut AppState| {
                    if let Some(e) = s.editor.as_mut() {
                        e.inspector.tab = Some(facet);
                    }
                }),
        );
    }
    body = body.child(strip);
    let content = match current {
        f if f == Facet::TEXT => text_section(app, e, t),
        f if f == Facet::RETIME => speed_section(app, e, t),
        f if f == Facet::EFFECTS => effects_section(app, e, t),
        f if f == Facet::TRACK => track_section(e, t),
        f if f == Facet::MARKER => marker_section(app, e, t),
        f if f == Facet::TRANSITION => transition_section(app, e, t),
        f if f == Facet::SOURCE => file_section(app, e, t),
        f if f == Facet::FADES => col().w_full().gap(4.0).child(property_groups(app, e, f, t)).child(fade_envelope(e, t)),
        f => property_groups(app, e, f, t),
    };
    body.child(el().w_full().grow(1.0).min_h(0.0).scroll(rune::ScrollSpec { stick_bottom: false }).child(content.w_full().pb(16.0)))
}

/// The selected clips that have this kind of setting.
fn targets(e: &Editor, facet: Facet) -> Vec<String> {
    e.ed.edit_targets(&e.ed.selection.selected_clip_ids(), facet)
}

/// The clips a style edit reaches (`StyleTargets`): with "Style all captions together" on, a caption's look is shared
/// by every caption on its track; what each says stays its own.
fn style_targets(app: &AppState, e: &Editor, facet: Facet) -> Vec<String> {
    let ids = targets(e, facet);
    if app.preferences.captions.style_all && facet != Facet::RETIME && facet != Facet::FADES {
        e.ed.with_caption_trackmates(&ids)
    } else {
        ids
    }
}

fn first_clip<'a>(e: &'a Editor, ids: &[String]) -> Option<&'a Clip> {
    ids.iter().find_map(|id| e.ed.find_clip(id))
}

/// Every property group shown in a tab, each with its rows and a Reset.
fn property_groups(app: &AppState, e: &Editor, tab: Facet, t: &Tokens) -> El {
    let ids = style_targets(app, e, tab);
    let Some(clip) = first_clip(e, &ids) else { return col() };
    let props: Vec<&'static ClipProperty> = properties::all()
        .iter()
        .copied()
        .filter(|p| p.shown_in() == tab && p.has(clip) && p.shown.is_none_or(|f| f(clip)))
        .collect();
    let mut groups: Vec<(&str, Vec<&'static ClipProperty>)> = Vec::new();
    for p in props {
        match groups.iter_mut().find(|(g, _)| *g == p.group) {
            Some((_, list)) => list.push(p),
            None => groups.push((p.group, vec![p])),
        }
    }
    let mut out = col().w_full();
    for (name, list) in groups {
        let reset_list: Vec<&'static ClipProperty> = list.clone();
        let ids_reset = ids.clone();
        let mut group = col()
            .w_full()
            .py(11.0)
            .gap(4.0)
            .child(
                row()
                    .w_full()
                    .center_y()
                    .child(widgets::heading(name, t).grow(1.0))
                    .child(widgets::button("Reset", Kind::Ghost, t).h(24.0).on_press(move |s: &mut AppState| {
                        if let Some(e) = s.editor.as_mut() {
                            let at = e.playhead;
                            for p in &reset_list {
                                e.ed.clear_keyframes(&fig_edit::KeyframeHostRef::of_clip_property(&ids_reset[0], p.key()), Some(p.key()));
                                e.ed.set_clip_property(&ids_reset, p, default_value(&p.def), at);
                            }
                        }
                    })),
            );
        for p in list {
            group = group.child(property_row(app, e, &ids, clip, p, t));
        }
        out = out.child(group).child(widgets::rule(t));
    }
    out
}

fn default_value(def: &ParamDef) -> ParamValue {
    match def.kind {
        ParamKind::Bool => ParamValue::Bool(def.default != 0.0),
        ParamKind::Color => ParamValue::Color(def.default as u32),
        ParamKind::List => ParamValue::Choice(def.default as i32),
        _ => ParamValue::Double(def.default),
    }
}

fn property_row(app: &AppState, e: &Editor, ids: &[String], clip: &Clip, p: &'static ClipProperty, t: &Tokens) -> El {
    let local = (e.playhead - clip.start_sec).clamp(0.0, clip.dur_sec);
    let value = p.value_at(clip, local);
    let label = text(p.def.label.clone()).font(font(11.5, 400.0)).color(t.text_muted).w(72.0).truncate().help(p.def.label.clone());
    let ids_v: Vec<String> = ids.to_vec();
    let set: Rc<dyn Fn(&mut AppState, ParamValue, Phase)> = Rc::new(move |s: &mut AppState, v: ParamValue, phase: Phase| {
        if let Some(e) = s.editor.as_mut() {
            let at = e.playhead;
            e.ed.set_clip_property(&ids_v, p, v, at);
            if phase == Phase::Up {
                e.ed.break_coalescing();
            }
        }
    });
    let control = match p.def.kind {
        ParamKind::Bool => {
            let on = value.as_bool();
            let set = set.clone();
            match p.icon {
                Some(_) => widgets::toggle_button(icon_for(p.key()), on, 26.0, t).on_press(move |s: &mut AppState| set(s, ParamValue::Bool(!on), Phase::Up)),
                None => widgets::switch(on, t).on_press(move |s: &mut AppState| set(s, ParamValue::Bool(!on), Phase::Up)),
            }
        }
        ParamKind::Color => color_control(&format!("prop.{}", p.key()), value.as_color(), set.clone(), t),
        ParamKind::List => {
            let key = format!("prop.{}", p.key());
            let choices = p.def.choices.clone();
            let current = choices.get(value.as_choice().max(0) as usize).cloned().unwrap_or_default();
            let set = set.clone();
            let k = key.clone();
            widgets::dropdown(key, current, t).on_press(move |s: &mut AppState| {
                let items = choices
                    .iter()
                    .enumerate()
                    .map(|(i, c)| {
                        let set = set.clone();
                        Item::new(c.clone(), move |s, _| set(s, ParamValue::Choice(i as i32), Phase::Up))
                    })
                    .collect();
                crate::menu::open_below(s, &k, items);
            })
        }
        _ => number_control(app, &format!("prop.{}", p.key()), &p.def, value.as_number(), set.clone(), t),
    };
    let mut r = row().w_full().min_h(28.0).gap(7.0).center_y().child(label).child(el().grow(1.0).min_w(0.0).child(control));
    if p.keyable && p.def.kind != ParamKind::Bool {
        r = r.child(keyframe_buttons(e, clip, p.key(), local, value.as_number(), t));
    }
    r
}

fn icon_for(key: &str) -> &'static rune::Icon {
    match key {
        "bold" => &icons::BOLD,
        "italic" => &icons::ITALIC,
        "uppercase" => &icons::CASE_UPPER,
        _ => &icons::CIRCLE,
    }
}

type OnParam = Rc<dyn Fn(&mut AppState, ParamValue, Phase)>;

fn number_control(app: &AppState, key: &str, def: &ParamDef, v: f64, set: OnParam, t: &Tokens) -> El {
    let (min, max) = (def.min, if def.max > def.min { def.max } else { def.min + 1.0 });
    let s1 = set.clone();
    let s2 = set;
    row()
        .w_full()
        .gap(7.0)
        .center_y()
        .child(el().grow(1.0).min_w(40.0).child(widgets::slider(
            format!("{key}.slider"),
            v,
            min,
            max,
            def.default,
            Rc::new(move |s, v, ph| s1(s, ParamValue::Double(v), ph)),
            t,
        )))
        .child(widgets::number_field(
            format!("{key}.field"),
            v,
            def.format_value(v),
            (max - min) / 400.0,
            (min, max),
            Rc::new(move |s, v, ph| s2(s, ParamValue::Double(v), ph)),
            app,
            t,
        ))
}

const SWATCHES: [u32; 12] = [
    0xFFFFFFFF, 0xFF000000, 0xFFFFD60A, 0xFFFF9F0A, 0xFFFF453A, 0xFFFF2D55, 0xFFBF5AF2, 0xFF5E5CE6, 0xFF0A84FF, 0xFF64D2FF, 0xFF30D158, 0xFF8E8E93,
];

fn argb_color(c: u32) -> Color {
    hex(&format!("#{c:08x}"))
}

fn color_control(key: &str, c: u32, set: OnParam, t: &Tokens) -> El {
    let k = key.to_string();
    row()
        .key(key.to_string())
        .gap(8.0)
        .center_y()
        .child(el().w(22.0).h(22.0).radius(4.0).fill(argb_color(c)).border(1.0, t.border))
        .child(text(format!("#{:06X}", c & 0xFFFFFF)).font(mono(11.0, 400.0)).color(t.text_secondary))
        .cursor(rune::Cursor::Pointer)
        .on_press(move |s: &mut AppState| {
            let mut items: Vec<Item> = SWATCHES
                .iter()
                .map(|&sw| {
                    let set = set.clone();
                    Item::new(format!("#{:06X}", sw & 0xFFFFFF), move |s, _| set(s, ParamValue::Color(sw), Phase::Up))
                })
                .collect();
            let set = set.clone();
            let key = k.clone();
            items.push(Item::Separator);
            items.push(Item::new("Type a colour…", move |s, _| {
                let set = set.clone();
                crate::widgets::begin_typing(s, &key, &format!("#{:06X}", c & 0xFFFFFF), Rc::new(move |s, text| {
                    let hex = text.trim().trim_start_matches('#');
                    if let Ok(v) = u32::from_str_radix(hex, 16) {
                        set(s, ParamValue::Color(0xFF000000 | v), Phase::Up);
                    }
                }));
            }));
            crate::menu::open_below(s, &k, items);
        })
}

/// Previous keyframe · add/remove a keyframe at the playhead · next keyframe.
fn keyframe_buttons(e: &Editor, clip: &Clip, key: &str, local: f64, value: f64, t: &Tokens) -> El {
    let times: Vec<f64> = clip.keyframes.get(key).map(|k| k.iter().map(|p| p.time_sec).collect()).unwrap_or_default();
    let on = times.iter().any(|k| (k - local).abs() < 1e-3);
    let prev = times.iter().copied().filter(|k| *k < local - 1e-3).fold(None, |a: Option<f64>, k| Some(a.map_or(k, |a| a.max(k))));
    let next = times.iter().copied().filter(|k| *k > local + 1e-3).fold(None, |a: Option<f64>, k| Some(a.map_or(k, |a| a.min(k))));
    let start = clip.start_sec;
    let (id, key) = (clip.id.clone(), key.to_string());
    let nav = |glyph: &'static rune::Icon, to: Option<f64>, tip: &str| {
        el().w(14.0)
            .h(20.0)
            .center()
            .help(tip.to_string())
            .opacity(if to.is_some() { 1.0 } else { 0.3 })
            .child(icon(glyph, 12.0).color(t.text_muted))
            .on_press(move |s: &mut AppState| {
                if let Some(k) = to {
                    crate::editor::seek(s, start + k, false);
                }
            })
    };
    let _ = e;
    row()
        .gap(1.0)
        .center_y()
        .child(nav(&icons::CHEVRON_LEFT, prev, "Previous keyframe"))
        .child(
            el().w(20.0)
                .h(20.0)
                .center()
                .help("Add or remove a keyframe at the playhead")
                .child(el().w(9.0).h(9.0).rotate(std::f32::consts::FRAC_PI_4).fill(if on { t.accent } else { Color::TRANSPARENT }).border(1.5, if on { t.accent } else { t.text_muted }))
                .on_press(move |s: &mut AppState| {
                    if let Some(e) = s.editor.as_mut() {
                        if on {
                            e.ed.remove_clip_keyframe(&id, &key, local);
                        } else {
                            e.ed.set_clip_keyframe(&id, &key, local, value);
                        }
                    }
                }),
        )
        .child(nav(&icons::CHEVRON_RIGHT, next, "Next keyframe"))
}

fn text_section(app: &AppState, e: &Editor, t: &Tokens) -> El {
    let mut c = col().w_full().gap(8.0).pt(8.0);
    if let Some((_, edit)) = &e.inspector.text {
        c = c.child(
            text_input(edit, font(13.0, 400.0))
                .placeholder("Title text", t.text_faint)
                .w_full()
                .min_h(64.0)
                .padding(8.0)
                .radius(8.0)
                .fill(hex("#1b1d22"))
                .border(1.0, if edit.focused { t.accent } else { t.border })
                .color(t.text_primary)
                .on_edit(|s: &mut AppState| &mut s.editor.as_mut().unwrap().inspector.text.as_mut().unwrap().1)
                .on_input_change(|s: &mut AppState, _tx: &mut Tx| {
                    let Some(e) = s.editor.as_mut() else { return };
                    let Some((_, edit)) = e.inspector.text.as_ref() else { return };
                    let words = edit.text.clone();
                    let ids = e.ed.edit_targets(&e.ed.selection.selected_clip_ids(), Facet::TEXT);
                    e.ed.set_text(&ids, &words);
                }),
        );
    }
    let ids = targets(e, Facet::TEXT);
    let family = first_clip(e, &ids).and_then(|c| c.as_text()).map(|t| t.style.font_family.clone()).unwrap_or_default();
    c = c.child(widgets::field_row(
        "Font",
        widgets::dropdown("insp.font", if family.is_empty() { "Default".to_string() } else { family }, t).on_press(|s: &mut AppState| {
            let mut items = vec![Item::new("Default", |s, _| set_font(s, ""))];
            for f in fig_render::layers::font_families().into_iter().take(400) {
                let name = f.clone();
                items.push(Item::new(f, move |s, _| set_font(s, &name)));
            }
            crate::menu::open_below(s, "insp.font", items);
        }),
        t,
    ));
    c.child(property_groups(app, e, Facet::TEXT, t))
}

fn set_font(app: &mut AppState, family: &str) {
    let ids = match app.editor.as_ref() {
        Some(e) => style_targets(app, e, Facet::TEXT),
        None => return,
    };
    let Some(e) = app.editor.as_mut() else { return };
    let family = family.to_string();
    e.ed.set_clip_value(&ids, "fontFamily", move |c| {
        if let Some(t) = c.as_text_mut() {
            t.style.font_family = family.clone();
        }
    });
}

fn speed_section(app: &AppState, e: &Editor, t: &Tokens) -> El {
    let ids = targets(e, Facet::RETIME);
    let Some(clip) = first_clip(e, &ids) else { return col() };
    let curve = clip_time::has_curve(clip);
    let speed = clip.speed;
    let ids_s = ids.clone();
    let set: widgets::OnValue = Rc::new(move |s: &mut AppState, v, phase| {
        if let Some(e) = s.editor.as_mut() {
            e.ed.set_speeds(&ids_s, v);
            if phase == Phase::Up {
                e.ed.break_coalescing();
            }
        }
    });
    let mut c = col().w_full().gap(8.0).pt(10.0).child(
        row()
            .w_full()
            .gap(6.0)
            .center_y()
            .child(widgets::heading("Retime", t).grow(1.0))
            .child(widgets::button("BPM", if e.inspector.tempo.is_some() { Kind::Primary } else { Kind::Ghost }, t).h(24.0).help("Set the speed from a song's tempo and the clip's own beat").on_press(|s: &mut AppState| {
                let Some(e) = s.editor.as_mut() else { return };
                e.inspector.tempo = match e.inspector.tempo.take() {
                    Some(_) => None,
                    None => {
                        let song = e.doc().song_bpm.map_or(String::new(), |b| format!("{b}"));
                        Some(Tempo { song: TextEdit::new(song), clip: TextEdit::new(""), half_or_double: true })
                    }
                };
            }))
            .child(widgets::button("Reset", Kind::Ghost, t).h(24.0).help("Play at normal speed (1.00×)").on_press({
                let ids = ids.clone();
                move |s: &mut AppState| {
                    if let Some(e) = s.editor.as_mut() {
                        e.ed.set_speeds(&ids, 1.0);
                        e.ed.break_coalescing();
                    }
                }
            })),
    );
    if let Some(tempo) = e.inspector.tempo.as_ref().filter(|_| !curve) {
        let matched = bpm(&tempo.song.text).zip(bpm(&tempo.clip.text)).and_then(|(s, c)| fig_edit::TempoMatch::new(s, c, tempo.half_or_double));
        let label = match &matched {
            None => "Enter both tempos".to_string(),
            Some(m) if m.describe().is_empty() => format!("Set speed to {:.2}×", m.speed),
            Some(m) => format!("Set speed to {:.2}× ({})", m.speed, m.describe()),
        };
        let field = |edit: &TextEdit, hint: &str, pick: fn(&mut AppState) -> &mut TextEdit| widgets::text_box(edit, hint, pick, t).w(72.0);
        let ids = ids.clone();
        c = c.child(
            col()
                .w_full()
                .padding(10.0)
                .gap(6.0)
                .radius(8.0)
                .fill(t.card_bg)
                .child(row().w_full().center_y().child(widgets::label("Song BPM", t).grow(1.0)).child(field(&tempo.song, "128", |s| &mut s.editor.as_mut().unwrap().inspector.tempo.as_mut().unwrap().song)))
                .child(
                    row()
                        .w_full()
                        .center_y()
                        .child(widgets::label("Clip BPM", t).grow(1.0).help("The beat the clip moves at now: a dancer's steps, a walk"))
                        .child(field(&tempo.clip, "100", |s| &mut s.editor.as_mut().unwrap().inspector.tempo.as_mut().unwrap().clip)),
                )
                .child(widgets::checkbox(tempo.half_or_double, "Allow half / double time", t).help("Land on every other beat, or twice a beat, when that needs a speed nearer 1×").on_press(|s: &mut AppState| {
                    if let Some(t) = s.editor.as_mut().and_then(|e| e.inspector.tempo.as_mut()) {
                        t.half_or_double = !t.half_or_double;
                    }
                }))
                .child(widgets::button(label, if matched.is_some() { Kind::Primary } else { Kind::Normal }, t).on_press(move |s: &mut AppState| {
                    let Some(e) = s.editor.as_mut() else { return };
                    let Some(tempo) = e.inspector.tempo.as_ref() else { return };
                    let Some(song) = bpm(&tempo.song.text) else { return };
                    let Some(m) = bpm(&tempo.clip.text).and_then(|c| fig_edit::TempoMatch::new(song, c, tempo.half_or_double)) else { return };
                    e.ed.edit(|ed| {
                        ed.set_song_bpm(Some(song));
                        ed.set_speeds(&ids, m.speed);
                    });
                    e.ed.break_coalescing();
                    e.say(if ids.len() > 1 {
                        format!("Matched {} clips to {song} BPM: {:.2}×", ids.len(), m.speed)
                    } else {
                        format!("Matched to {song} BPM: {:.2}×", m.speed)
                    });
                })),
        );
    }
    if curve {
        c = c.child(widgets::note("This clip's speed follows a curve: drag the points on its speed line in the timeline.", t));
    } else {
        c = c.child(
            row()
                .w_full()
                .gap(7.0)
                .center_y()
                .child(widgets::label("Speed", t).w(72.0))
                .child(el().grow(1.0).child(widgets::slider("insp.speed", speed.log2(), (0.25f64).log2(), 4f64.log2(), 0.0, {
                    let set = set.clone();
                    Rc::new(move |s, v, ph| set(s, 2f64.powf(v), ph))
                }, t)))
                .child(widgets::number_field("insp.speed.field", speed, format!("{speed:.2}×"), 0.01, (0.1, 8.0), set, app, t)),
        );
    }
    let id = clip.id.clone();
    if e.ed.can_have_speed_curve(&id) {
        c = c.child(widgets::button(if curve { "Remove speed curve" } else { "Add speed curve" }, Kind::Normal, t).on_press(move |s: &mut AppState| {
            if let Some(e) = s.editor.as_mut() {
                if curve {
                    e.ed.remove_speed_curve(&id);
                } else {
                    e.ed.add_speed_curve(&id);
                }
            }
        }));
    }
    let original = clip.source_out() - clip.source_in();
    c.child(
        col()
            .w_full()
            .padding(10.0)
            .gap(4.0)
            .radius(8.0)
            .fill(t.card_bg)
            .child(widgets::heading("Clip duration", t))
            .child(row().child(widgets::label("Original", t).grow(1.0)).child(text(e.timecode(original)).font(mono(11.0, 400.0)).color(t.text_secondary)))
            .child(row().child(widgets::label("After retiming", t).grow(1.0)).child(text(e.timecode(clip.dur_sec)).font(mono(11.0, 400.0)).color(t.text_secondary))),
    )
    .child(property_groups(app, e, Facet::RETIME, t))
}

fn fade_envelope(e: &Editor, t: &Tokens) -> El {
    let ids = targets(e, Facet::FADES);
    let Some(clip) = first_clip(e, &ids) else { return col() };
    let w = 220.0f32;
    let h = 60.0f32;
    let dur = clip.dur_sec.max(1e-6) as f32;
    let fi = (clip.fade_in_sec as f32 / dur * w).min(w);
    let fo = (clip.fade_out_sec as f32 / dur * w).min(w);
    let pts = vec![[0.0, h], [fi, 4.0], [w - fo, 4.0], [w, h]];
    col()
        .w_full()
        .gap(6.0)
        .pt(8.0)
        .child(widgets::heading("Fade envelope", t))
        .child(
            el().w(w)
                .h(h + 8.0)
                .radius(6.0)
                .fill(t.card_bg)
                .child(rune::canvas(rune::Shape::new().fill(pts, t.accent.alpha(0.35))).absolute(0.0, 4.0).w(w).h(h)),
        )
        .child(widgets::note("Adjust fades here or from the clip corners.", t))
}

fn effects_section(app: &AppState, e: &Editor, t: &Tokens) -> El {
    let ids = targets(e, Facet::EFFECTS);
    let owner = ids.first().cloned().or_else(|| e.ed.selection.selected_effects().first().map(|(o, _)| o.clone()));
    let Some(owner) = owner else { return col() };
    let effects = fig_edit::effects::owner_effects(e.doc(), &owner).cloned().unwrap_or_default();
    let mut c = col().w_full().gap(6.0).pt(10.0).child(
        row()
            .w_full()
            .center_y()
            .child(widgets::heading("Applied effects", t).grow(1.0))
            .child(widgets::button("Bypass all", Kind::Ghost, t).h(24.0).on_press({
                let owner = owner.clone();
                move |s: &mut AppState| {
                    if let Some(e) = s.editor.as_mut() {
                        let list: Vec<String> = fig_edit::effects::owner_effects(e.doc(), &owner).map(|v| v.iter().filter(|f| f.enabled).map(|f| f.id.clone()).collect()).unwrap_or_default();
                        e.ed.edit(|ed| {
                            for id in &list {
                                ed.toggle_effect(&owner, id);
                            }
                        });
                    }
                }
            })),
    );
    if effects.is_empty() {
        c = c.child(
            col()
                .w_full()
                .py(14.0)
                .gap(4.0)
                .items_center()
                .child(icon(&icons::WAND_SPARKLES, 22.0).color(t.text_muted))
                .child(text("No effects on this clip yet").font(font(12.5, 600.0)).color(t.text_secondary))
                .child(widgets::note("Add one below, or drag it from the Effects library onto the clip.", t)),
        );
    }
    let picked = e.inspector.effect.clone().filter(|id| effects.iter().any(|f| &f.id == id)).or_else(|| effects.first().map(|f| f.id.clone()));
    for fx in &effects {
        let entry = fig_fx::effects::find(&fx.type_id);
        let name = entry.as_ref().map_or(fx.type_id.clone(), |e| e.display_name.clone());
        let (o1, o2, id1, id2, id3) = (owner.clone(), owner.clone(), fx.id.clone(), fx.id.clone(), fx.id.clone());
        let on = picked.as_deref() == Some(fx.id.as_str());
        c = c.child(
            row()
                .w_full()
                .h(34.0)
                .px(10.0)
                .gap(8.0)
                .radius(8.0)
                .fill(if on { t.accent_softer } else { hex("#212328") })
                .border(1.0, if on { t.accent } else { Color::TRANSPARENT })
                .opacity(if fx.enabled { 1.0 } else { 0.55 })
                .center_y()
                .child(icon(&icons::SPARKLES, 14.0).color(t.accent_text))
                .child(text(name).font(font(12.0, 500.0)).color(t.text_primary).grow(1.0).truncate())
                .child(widgets::icon_button(&icons::POWER, 13.0, 24.0, t).help("Turn on or off").on_press(move |s: &mut AppState| {
                    if let Some(e) = s.editor.as_mut() {
                        e.ed.toggle_effect(&o1, &id1);
                    }
                }))
                .child(widgets::icon_button(&icons::X, 13.0, 24.0, t).help("Remove").on_press(move |s: &mut AppState| {
                    if let Some(e) = s.editor.as_mut() {
                        e.ed.remove_effect(&o2, &id2);
                    }
                }))
                .on_press({
                    let owner = owner.clone();
                    move |s: &mut AppState| {
                        if let Some(e) = s.editor.as_mut() {
                            e.inspector.effect = Some(id3.clone());
                            e.ed.selection.select_effect(&owner, &id3);
                        }
                    }
                }),
        );
    }
    if let Some(fx) = picked.and_then(|id| effects.iter().find(|f| f.id == id))
        && let Some(entry) = fig_fx::effects::find(&fx.type_id)
    {
        let start = e.ed.find_clip(&owner).map_or(0.0, |c| c.start_sec);
        let local = e.playhead - start;
        let values = fig_fx::effects::current_numeric_values(fx, local);
        if fx.type_id == fig_fx::effects::ids::TRANSFORM {
            c = c.child(camera_card(app, e, &owner, fx, t));
        }
        let mut card = col().w_full().padding(10.0).gap(4.0).radius(8.0).fill(t.card_bg_alt).child(text(entry.display_name.clone()).font(font(12.0, 600.0)).color(t.accent_text));
        for def in &entry.params {
            let v = values.get(&def.key).copied().unwrap_or(def.default);
            let (owner, fid, key, type_id) = (owner.clone(), fx.id.clone(), def.key.clone(), fx.type_id.clone());
            let set: OnParam = Rc::new(move |s: &mut AppState, val: ParamValue, phase: Phase| {
                if let Some(e) = s.editor.as_mut() {
                    let val = match val {
                        ParamValue::Double(n) => fig_fx::effects::to_value(&type_id, &key, n),
                        other => other,
                    };
                    e.ed.set_effect_param(&owner, &fid, &key, val);
                    if phase == Phase::Up {
                        e.ed.break_coalescing();
                    }
                }
            });
            let control = match def.kind {
                ParamKind::Bool => {
                    let on = v != 0.0;
                    let set = set.clone();
                    widgets::switch(on, t).on_press(move |s: &mut AppState| set(s, ParamValue::Bool(!on), Phase::Up))
                }
                ParamKind::Color => color_control(&format!("fx.{}.{}", fx.id, def.key), v as u32, set, t),
                ParamKind::List => {
                    let key = format!("fx.{}.{}", fx.id, def.key);
                    let choices = def.choices.clone();
                    let k = key.clone();
                    widgets::dropdown(key, choices.get(v as usize).cloned().unwrap_or_default(), t).on_press(move |s: &mut AppState| {
                        let items = choices
                            .iter()
                            .enumerate()
                            .map(|(i, c)| {
                                let set = set.clone();
                                Item::new(c.clone(), move |s, _| set(s, ParamValue::Choice(i as i32), Phase::Up))
                            })
                            .collect();
                        crate::menu::open_below(s, &k, items);
                    })
                }
                _ => number_control(app, &format!("fx.{}.{}", fx.id, def.key), def, v, set, t),
            };
            card = card.child(widgets::field_row(def.label.clone(), control, t));
        }
        if entry.is_custom {
            let type_id = fx.type_id.clone();
            card = card.child(widgets::button("Edit graph…", Kind::Normal, t).on_press(move |s: &mut AppState| crate::graph::open(s, Some(&type_id))));
        }
        c = c.child(card);
    }
    let o = owner.clone();
    c.child(widgets::icon_text_button(&icons::PLUS, "Add effect", Kind::Normal, t).key("insp.addfx").on_press(move |s: &mut AppState| {
        let mut by_cat: Vec<(String, Vec<Item>)> = Vec::new();
        for entry in fig_fx::effects::all() {
            let owner = o.clone();
            let type_id = entry.type_id.clone();
            let item = Item::new(entry.display_name.clone(), move |s, _| crate::library::add_effect(s, &owner, &type_id));
            match by_cat.iter_mut().find(|(c, _)| *c == entry.category) {
                Some((_, list)) => list.push(item),
                None => by_cat.push((entry.category.clone(), vec![item])),
            }
        }
        let items = by_cat.into_iter().map(|(c, list)| Item::submenu(c, list)).collect();
        crate::menu::open_below(s, "insp.addfx", items);
    }))
}

fn track_section(e: &Editor, t: &Tokens) -> El {
    let Some(track) = e.ed.selection.items().iter().find(|k| k.group == SelectionGroup::Track).and_then(|k| e.ed.find_track(&k.id)) else {
        return col();
    };
    let name = crate::timeline::track_name(e.doc(), track);
    let state = match (track.visible, track.muted) {
        (true, false) => "Shown, with sound",
        (true, true) => "Shown, muted",
        (false, false) => "Hidden, with sound",
        (false, true) => "Hidden, muted",
    };
    col()
        .w_full()
        .gap(8.0)
        .pt(10.0)
        .child(widgets::field_row("Name", text(name).font(font(12.5, 500.0)).color(t.text_primary), t))
        .child(widgets::field_row("Type", text(format!("{:?}", track.kind)).font(font(12.5, 400.0)).color(t.text_secondary), t))
        .child(widgets::field_row("State", text(state).font(font(12.5, 400.0)).color(t.text_secondary), t))
        .child(widgets::field_row("Clips", text(track.clips.len().to_string()).font(font(12.5, 400.0)).color(t.text_secondary), t))
}

const MARKER_COLOURS: [&str; 8] = ["#ffd60a", "#eab308", "#ff9f0a", "#ff453a", "#bf5af2", "#5f9cf8", "#64d2ff", "#30d158"];

fn marker_section(app: &AppState, e: &Editor, t: &Tokens) -> El {
    let Some(id) = e.ed.selection.selected_marker_id() else { return col() };
    let Some(loc) = fig_edit::markers::find_marker(e.doc(), id) else { return col() };
    let m = loc.marker.clone();
    let mut c = col().w_full().gap(8.0).pt(10.0);
    if let Some((_, edit)) = &e.inspector.marker_name {
        c = c.child(widgets::field_row(
            "Name",
            text_input(edit, font(12.5, 400.0))
                .single_line()
                .w_full()
                .h(28.0)
                .px(8.0)
                .radius(6.0)
                .fill(hex("#1b1d22"))
                .border(1.0, t.border)
                .color(t.text_primary)
                .on_edit(|s: &mut AppState| &mut s.editor.as_mut().unwrap().inspector.marker_name.as_mut().unwrap().1)
                .on_input_change(|s: &mut AppState, _tx: &mut Tx| {
                    let Some(e) = s.editor.as_mut() else { return };
                    let Some((id, edit)) = e.inspector.marker_name.clone() else { return };
                    e.ed.update_marker(&id, Some(&edit.text), None);
                }),
            t,
        ));
    }
    let mut swatches = row().gap(6.0);
    for col_hex in MARKER_COLOURS {
        let id = m.id.clone();
        let on = m.color.eq_ignore_ascii_case(col_hex);
        swatches = swatches.child(
            el().w(24.0)
                .h(24.0)
                .radius(4.0)
                .fill(hex(col_hex))
                .border(2.0, if on { hex("#ececef") } else { Color::TRANSPARENT })
                .hover(Color::WHITE.alpha(0.1))
                .on_press(move |s: &mut AppState| {
                    if let Some(e) = s.editor.as_mut() {
                        e.ed.update_marker(&id, None, Some(col_hex));
                    }
                }),
        );
    }
    c = c.child(widgets::field_row("Colour", swatches, t));
    let at = loc.timeline_sec();
    c = c.child(widgets::field_row("Position", text(e.timecode(at)).font(mono(11.5, 400.0)).color(t.text_secondary), t));
    if m.is_range() {
        c = c.child(widgets::field_row("Length", text(e.timecode(m.dur_sec)).font(mono(11.5, 400.0)).color(t.text_secondary), t));
        let id = m.id.clone();
        c = c.child(widgets::checkbox(m.include_in_export, "Include in batch export", t).on_press(move |s: &mut AppState| {
            if let Some(e) = s.editor.as_mut() {
                let on = fig_edit::markers::find_marker(e.doc(), &id).is_some_and(|l| l.marker.include_in_export);
                e.ed.edit_marker(&id, fig_edit::markers::MarkerEdit { include_in_export: Some(!on), ..Default::default() });
            }
        }));
    }
    let id = m.id.clone();
    let _ = app;
    c.child(row().gap(6.0).child(widgets::button("Jump to marker", Kind::Normal, t).on_press(move |s: &mut AppState| crate::editor::seek(s, at, false))).child(
        widgets::button("Delete marker", Kind::Danger, t).on_press(move |s: &mut AppState| {
            if let Some(e) = s.editor.as_mut() {
                e.ed.delete_marker(&id);
            }
        }),
    ))
}

fn transition_section(app: &AppState, e: &Editor, t: &Tokens) -> El {
    let Some(key) = e.ed.selection.selected_transition_key() else { return col() };
    let Some(cut) = fig_edit::transitions::find_cut(e.doc(), key) else { return col() };
    let tr = e.ed.find_clip(&cut.left_clip_id).and_then(|c| c.transition_out.clone()).unwrap_or_else(|| fig_timeline::TransitionRef {
        type_id: cut.type_id.clone(),
        duration_sec: cut.duration_sec,
        params: Default::default(),
    });
    let entry = fig_fx::transitions::find(&tr.type_id);
    let max = e.ed.max_transition_duration(&cut.left_clip_id, &cut.right_clip_id).max(0.05);
    let (l, r) = (cut.left_clip_id.clone(), cut.right_clip_id.clone());
    let set_dur: widgets::OnValue = Rc::new(move |s: &mut AppState, v, phase| {
        if let Some(e) = s.editor.as_mut() {
            e.ed.set_transition_duration(&l, &r, v);
            if phase == Phase::Up {
                e.ed.break_coalescing();
            }
        }
    });
    let mut c = col()
        .w_full()
        .gap(8.0)
        .pt(10.0)
        .child(text(entry.map_or(tr.type_id.clone(), |e| e.display_name.to_string())).font(font(15.0, 600.0)).color(t.text_primary))
        .child(widgets::note(entry.map_or("", |e| e.description), t))
        .child(
            row()
                .w_full()
                .gap(7.0)
                .center_y()
                .child(widgets::label("Duration", t).w(72.0))
                .child(el().grow(1.0).child(widgets::slider("tr.dur", tr.duration_sec, 0.05, max, 0.5, set_dur.clone(), t)))
                .child(widgets::number_field("tr.dur.field", tr.duration_sec, format!("{:.2} s", tr.duration_sec), 0.01, (0.05, max), set_dur, app, t)),
        );
    if let Some(entry) = entry {
        for def in &entry.params {
            let v = tr.params.get(&def.key).map_or(def.default, |p| p.as_number());
            let (l, r, key) = (cut.left_clip_id.clone(), cut.right_clip_id.clone(), def.key.clone());
            let set: OnParam = Rc::new(move |s: &mut AppState, val: ParamValue, phase: Phase| {
                if let Some(e) = s.editor.as_mut() {
                    e.ed.set_transition_param(&l, &r, &key, val);
                    if phase == Phase::Up {
                        e.ed.break_coalescing();
                    }
                }
            });
            c = c.child(widgets::field_row(def.label.clone(), number_control(app, &format!("tr.{}", def.key), def, v, set, t), t));
        }
    }
    let (l, r) = (cut.left_clip_id.clone(), cut.right_clip_id.clone());
    let cut_at = cut.cut_sec;
    c.child(
        row()
            .gap(6.0)
            .child(widgets::button("Preview transition", Kind::Normal, t).on_press(move |s: &mut AppState| {
                crate::editor::seek(s, (cut_at - 1.0).max(0.0), false);
                crate::editor::play(s, 1.0);
            }))
            .child(widgets::button("Remove", Kind::Danger, t).on_press(move |s: &mut AppState| {
                if let Some(e) = s.editor.as_mut() {
                    e.ed.remove_transition(&l, &r);
                }
            })),
    )
}

fn file_section(app: &AppState, e: &Editor, t: &Tokens) -> El {
    let ids = e.ed.selection.selected_clip_ids();
    let asset = ids
        .iter()
        .filter_map(|id| e.ed.find_clip(id))
        .find_map(|c| c.media_id())
        .or_else(|| e.ed.selection.items().iter().find(|k| k.group == SelectionGroup::Media).map(|k| k.id.as_str()))
        .and_then(|id| e.find_media(id));
    let Some(a) = asset else { return col() };
    let proxy = match a.proxy_status {
        fig_timeline::ProxyStatus::Ready => "Ready: previews play from a lighter copy",
        fig_timeline::ProxyStatus::Pending => "Being made…",
        fig_timeline::ProxyStatus::Failed => "Couldn't be made",
        _ => "None",
    };
    let id = a.id.clone();
    let url = a.url.clone();
    let _ = app;
    col()
        .w_full()
        .gap(8.0)
        .pt(10.0)
        .child(text(a.file_name().to_string()).font(font(13.0, 600.0)).color(t.text_primary).wrap_anywhere())
        .child(widgets::field_row("Kind", text(format!("{:?}", a.kind)).font(font(12.0, 400.0)).color(t.text_secondary), t))
        .child(widgets::field_row("Resolution", text(format!("{}×{}", a.width, a.height)).font(font(12.0, 400.0)).color(t.text_secondary), t))
        .child(widgets::field_row("Duration", text(e.timecode(a.duration_sec)).font(mono(11.5, 400.0)).color(t.text_secondary), t))
        .child(widgets::field_row("Location", text(if a.offline { "Missing".to_string() } else if a.in_project { "In the project folder".into() } else { "Linked".into() }).font(font(12.0, 400.0)).color(t.text_secondary), t))
        .child(widgets::rule(t))
        .child(widgets::heading("Proxy", t))
        .child(widgets::note(proxy, t))
        .child(
            row()
                .gap(6.0)
                .child(widgets::button("Make proxy", Kind::Normal, t).on_press(move |s: &mut AppState| crate::media::request_proxy(s, &id, true)))
                .child(widgets::button("Show in folder", Kind::Ghost, t).on_press(move |s: &mut AppState| {
                    if let Err(err) = crate::media::reveal(&url) {
                        crate::toasts::show(s, err);
                    }
                })),
        )
}

/// The Transform effect's camera (`TransformControlsViewModel`): its keyframes at the playhead, zoom and turn, and
/// the steps a zoom edit is made of. The frame itself is dragged on the preview.
fn camera_card(app: &AppState, e: &Editor, owner: &str, fx: &fig_timeline::EffectInstance, t: &Tokens) -> El {
    use crate::overlays::{edit_time, keyframe_times, set_camera};
    let origin = e.ed.find_clip(owner).map_or(0.0, |c| c.start_sec);
    let span = e.ed.find_clip(owner).map_or(f64::INFINITY, |c| c.dur_sec);
    let time = edit_time(e, fx, origin);
    let view = fig_fx::effects::transform_view_at(fx, time);
    let times = keyframe_times(fx);
    let on_key = times.iter().any(|k| (k - time).abs() < 1e-6);
    let animated = !times.is_empty();
    let summary = match times.len() {
        0 => "Not animated".to_string(),
        1 => "1 keyframe".to_string(),
        n => format!("{n} keyframes"),
    };
    let hint = if !animated {
        "The same look throughout the effect. Press the stopwatch to animate it."
    } else if on_key {
        "Editing this keyframe. Drag the frame or change a value."
    } else {
        "Move the playhead and drag the frame on the preview to add a keyframe there."
    };
    let (o, f) = (owner.to_string(), fx.id.clone());
    let act = move |run: fn(&mut AppState, &str, &str, f64, f64)| {
        let (o, f) = (o.clone(), f.clone());
        move |s: &mut AppState| run(s, &o, &f, origin, span)
    };
    let step = |forward: bool| {
        let times = times.clone();
        move |s: &mut AppState| {
            let target = if forward { times.iter().find(|k| **k > time + 1e-6) } else { times.iter().rev().find(|k| **k < time - 1e-6) };
            if let Some(k) = target {
                crate::editor::seek(s, origin + k, false);
            }
        }
    };
    let zoom: OnValue = Rc::new(|s: &mut AppState, v: f64, phase: Phase| {
        if let Some(e) = s.editor.as_mut()
            && let Some((_, fx, origin)) = crate::overlays::selected_transform(e)
        {
            let view = fig_fx::effects::transform_view_at(&fx, edit_time(e, &fx, origin));
            set_camera(e, view.center_x, view.center_y, v, None);
            if phase == Phase::Up {
                e.ed.break_coalescing();
            }
        }
    });
    let turn: OnValue = Rc::new(|s: &mut AppState, v: f64, phase: Phase| {
        if let Some(e) = s.editor.as_mut()
            && let Some((_, fx, origin)) = crate::overlays::selected_transform(e)
        {
            let view = fig_fx::effects::transform_view_at(&fx, edit_time(e, &fx, origin));
            set_camera(e, view.center_x, view.center_y, view.zoom, Some(v));
            if phase == Phase::Up {
                e.ed.break_coalescing();
            }
        }
    });
    let mut card = col()
        .w_full()
        .padding(10.0)
        .gap(6.0)
        .radius(8.0)
        .fill(t.card_bg_alt)
        .child(
            row()
                .w_full()
                .gap(4.0)
                .center_y()
                .child(text("Camera").font(font(12.0, 600.0)).color(t.accent_text).grow(1.0))
                .child(text(summary).font(font(11.0, 400.0)).color(t.text_muted)),
        )
        .child(
            row()
                .w_full()
                .gap(4.0)
                .center_y()
                .child(widgets::toggle_button(&icons::TIMER, animated, 26.0, t).help("Record a keyframe at the playhead. Keyframes you already have stay.").on_press(act(|s, o, f, origin, span| {
                    if let Some(e) = s.editor.as_mut() {
                        let at = (e.playhead - origin).clamp(0.0, span);
                        e.ed.record_effect_keyframe(o, f, at);
                    }
                })))
                .child(widgets::icon_button(&icons::CHEVRON_LEFT, 14.0, 26.0, t).help("Previous keyframe").on_press(step(false)))
                .child(widgets::toggle_button(&icons::DIAMOND, on_key, 26.0, t).help("Add a keyframe at the playhead, or remove the one here").on_press(act(|s, o, f, origin, _| {
                    let Some(e) = s.editor.as_mut() else { return };
                    let Some(fx) = e.ed.find_effect(o, f).cloned() else { return };
                    let at = edit_time(e, &fx, origin);
                    if fx.keyframes.is_empty() {
                        e.ed.record_effect_keyframe(o, f, at);
                    } else if keyframe_times(&fx).iter().any(|k| (k - at).abs() < 1e-6) {
                        e.ed.remove_effect_keyframe(o, f, at);
                    } else {
                        e.ed.add_effect_keyframe(o, f, at);
                    }
                })))
                .child(widgets::icon_button(&icons::CHEVRON_RIGHT, 14.0, 26.0, t).help("Next keyframe").on_press(step(true)))
                .child(el().grow(1.0))
                .child(text(e.timecode(origin + time)).font(mono(11.0, 400.0)).color(t.text_secondary)),
        )
        .child(widgets::field_row("Zoom", widgets::number_field("cam.zoom", view.zoom, format!("{:.2}×", view.zoom), 0.01, (1.0, 8.0), zoom, app, t), t))
        .child(widgets::field_row("Rotation", widgets::number_field("cam.turn", view.rotation_degrees, format!("{:.1}°", view.rotation_degrees), 0.5, (-180.0, 180.0), turn, app, t), t))
        .child(
            row()
                .w_full()
                .gap(6.0)
                .child(widgets::button("Back to full frame here", Kind::Normal, t).help("Add a keyframe at the playhead that zooms all the way back out").on_press(act(|s, o, f, origin, _| {
                    let Some(e) = s.editor.as_mut() else { return };
                    let Some(fx) = e.ed.find_effect(o, f).cloned() else { return };
                    let at = edit_time(e, &fx, origin);
                    let rotation = fig_fx::effects::transform_view_at(&fx, at).rotation_degrees;
                    if fx.keyframes.is_empty() {
                        e.ed.set_effect_animated(o, f, true, at);
                    }
                    let values: IndexMap<String, f64> = [("centerX", 0.5), ("centerY", 0.5), ("zoom", 1.0), ("rotation", rotation)].into_iter().map(|(k, v)| (k.to_string(), v)).collect();
                    e.ed.set_effect_keyframe(o, f, at, &values, None);
                    e.ed.break_coalescing();
                })))
                .child(widgets::button("Preview movement", Kind::Normal, t).help("Play the animated stretch").on_press(act(|s, o, f, origin, span| {
                    let Some(e) = s.editor.as_ref() else { return };
                    let Some(fx) = e.ed.find_effect(o, f).cloned() else { return };
                    let times = keyframe_times(&fx);
                    let from = if times.len() > 1 { times[0] } else { fx.start_sec.unwrap_or(0.0) };
                    let to = if times.len() > 1 { times[times.len() - 1] } else { from + fx.dur_sec.unwrap_or((span - from).min(5.0)) };
                    crate::editor::play_range(s, origin + (from - 0.3).max(0.0), origin + (to + 0.3).min(span));
                }))),
        );
    if animated {
        card = card.child(widgets::button("Clear keyframes", Kind::Ghost, t).help("Remove every keyframe and keep the look at the playhead").on_press(act(|s, o, f, origin, span| {
            if let Some(e) = s.editor.as_mut() {
                let at = (e.playhead - origin).clamp(0.0, span);
                e.ed.set_effect_animated(o, f, false, at);
            }
        })));
    }
    card.child(widgets::note(hint, t))
}
