//! The library panel (left): an icon rail and seven tabs: Media, Templates, Text, Transitions, Effects, Markers and
//! Captions. Cards can be clicked or double-clicked, and dragged onto the timeline.

use fig_timeline::{MediaKind, SelectionGroup, SelectionKey};
use rune::{Color, Cursor, Drag, El, Phase, TextEdit, Tx, col, el, icon, row, text};

use crate::app::AppState;
use crate::editor::Editor;
use crate::icons;
use crate::menu::Item;
use crate::theme::{RADIUS_CARD, Tokens, font, hex, mono};
use crate::timeline::geometry::{HEADER_W, lanes};
use crate::widgets::{self, Kind};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub enum Tab {
    #[default]
    Media,
    Templates,
    Text,
    Transitions,
    Effects,
    Markers,
    Captions,
}

const TABS: [(Tab, &str, &rune::Icon); 7] = [
    (Tab::Media, "Media", &icons::FILM),
    (Tab::Templates, "Templates", &icons::CLAPPERBOARD),
    (Tab::Text, "Text", &icons::TYPE),
    (Tab::Transitions, "Transitions", &icons::BLEND),
    (Tab::Effects, "Effects", &icons::WAND_SPARKLES),
    (Tab::Markers, "Markers", &icons::FLAG),
    (Tab::Captions, "Captions", &icons::CAPTIONS),
];

pub struct LibraryState {
    pub tab: Tab,
    pub search: TextEdit,
    pub category: Option<String>,
}

impl LibraryState {
    pub fn new() -> LibraryState {
        LibraryState { tab: Tab::Media, search: TextEdit::new(""), category: None }
    }
}

/// What a library drag carries.
#[derive(Clone, Debug)]
pub enum Payload {
    Media(String),
    Effect(String),
    Transition(String),
    Text(usize),
}

pub struct LibDrag {
    pub payload: Payload,
    pub label: String,
    /// The pointer, in window points.
    pub x: f32,
    pub y: f32,
    pub started: bool,
}

pub fn rail(e: &Editor, t: &Tokens) -> El {
    let mut r = col().w(44.0).h_full().py(8.0).px(4.0).gap(4.0).fill(t.surface_bg);
    for (tab, name, glyph) in TABS {
        let on = tab == e.library.tab && !e.panels.left_collapsed;
        r = r.child(
            el().w(36.0)
                .h(36.0)
                .radius(8.0)
                .fill(if on { t.accent_soft } else { Color::TRANSPARENT })
                .hover(t.hover_bg)
                .center()
                .help(name.to_string())
                .child(icon(glyph, 18.0).color(if on { t.accent_text } else { t.text_secondary }))
                .on_press(move |s: &mut AppState| {
                    if let Some(e) = s.editor.as_mut() {
                        if e.library.tab == tab && !e.panels.left_collapsed {
                            return;
                        }
                        e.library.tab = tab;
                        e.library.category = None;
                        e.library.search = TextEdit::new("");
                        e.panels.left_collapsed = false;
                    }
                    s.save_workspace();
                }),
        );
    }
    r
}

pub fn view(app: &AppState, e: &Editor, t: &Tokens) -> El {
    let tab = e.library.tab;
    let (title, count) = match tab {
        Tab::Media => ("Media", e.project.media.iter().filter(|m| m.reverse_of.is_none()).count()),
        Tab::Templates => ("Templates", 0),
        Tab::Text => ("Text", fig_timeline::text_presets().len()),
        Tab::Transitions => ("Transitions", fig_fx::transitions::all().len()),
        Tab::Effects => ("Effects", fig_fx::effects::all().len()),
        Tab::Markers => ("Markers", fig_edit::markers::all_markers(e.doc()).len()),
        Tab::Captions => ("Captions", e.ed.caption_cues(0.0, f64::MAX).len()),
    };
    let header = row()
        .w_full()
        .h(28.0)
        .gap(6.0)
        .center_y()
        .child(text(title).font(font(14.0, 600.0)).color(t.text_primary))
        .child(text(if count > 0 { count.to_string() } else { String::new() }).font(font(12.0, 400.0)).color(t.text_muted).grow(1.0))
        .child(
            widgets::icon_button(&icons::CHEVRON_LEFT, 14.0, 26.0, t)
                .help("Hide this panel (Ctrl+1)")
                .on_press_tx(|s: &mut AppState, tx: &mut Tx| crate::actions::run(s, crate::commands::Command::MediaPanel, tx)),
        );
    let content = match tab {
        Tab::Media => media_tab(app, e, t),
        Tab::Templates => templates_tab(app, e, t),
        Tab::Text => text_tab(e, t),
        Tab::Transitions => transitions_tab(e, t),
        Tab::Effects => effects_tab(e, t),
        Tab::Markers => markers_tab(e, t),
        Tab::Captions => captions_tab(e, t),
    };
    row()
        .w_full()
        .h_full()
        .child(rail(e, t))
        .child(el().w(1.0).h_full().fill(t.border))
        .child(col().grow(1.0).min_w(0.0).h_full().padding(10.0).gap(8.0).child(header).child(content))
}

fn search(e: &Editor, placeholder: &str, t: &Tokens) -> El {
    widgets::text_box(&e.library.search, placeholder, |s| &mut s.editor.as_mut().unwrap().library.search, t).w_full()
}

fn matches(e: &Editor, name: &str) -> bool {
    let q = e.library.search.text.trim().to_lowercase();
    q.is_empty() || name.to_lowercase().contains(&q)
}

fn scroll(content: El) -> El {
    el().w_full().grow(1.0).min_h(0.0).scroll(rune::ScrollSpec { stick_bottom: false }).child(content.w_full())
}

// ---- dragging onto the timeline --------------------------------------------------------------

/// A card's drag: past 8 points it becomes a library drag; letting go over the timeline drops it there, and a press
/// without a drag runs `click`.
fn draggable(card: El, key: String, payload: Payload, label: String, click: fn(&mut AppState, &Payload)) -> El {
    let k = key.clone();
    card.key(key).drag_threshold(0.0).on_drag(move |s: &mut AppState, d: &mut Drag, tx: &mut Tx| {
        let origin = rune::memo::window_layout(&k).unwrap_or([0.0; 4]);
        let (x, y) = (origin[0] + d.start_x + d.dx, origin[1] + d.start_y + d.dy);
        match d.phase {
            Phase::Down => {
                s.lib_drag = Some(LibDrag { payload: payload.clone(), label: label.clone(), x, y, started: false });
            }
            Phase::Move => {
                if let Some(drag) = s.lib_drag.as_mut() {
                    drag.x = x;
                    drag.y = y;
                    drag.started |= d.dx.abs() + d.dy.abs() > 8.0;
                }
                update_drop_preview(s, x, y);
            }
            Phase::Up => {
                let drag = s.lib_drag.take();
                if let Some(e) = s.editor.as_mut() {
                    e.tl.drop_preview = None;
                }
                match drag {
                    Some(drag) if drag.started && !d.cancelled() => drop_on_timeline(s, &drag.payload, x, y),
                    Some(drag) if !drag.started => click(s, &drag.payload),
                    _ => {}
                }
            }
        }
        tx.instant();
    })
}

fn canvas_point(x: f32, y: f32) -> Option<(f32, f32)> {
    let r = rune::memo::window_layout("timeline.canvas")?;
    (x >= r[0] && x <= r[0] + r[2] && y >= r[1] && y <= r[1] + r[3]).then(|| (x - r[0], y - r[1]))
}

fn update_drop_preview(app: &mut AppState, x: f32, y: f32) {
    let Some(e) = app.editor.as_mut() else { return };
    match canvas_point(x, y).filter(|(cx, _)| *cx > HEADER_W) {
        Some((cx, cy)) => {
            let lanes = lanes(e.doc(), e.media(), e.tl.scale);
            let ti = e.tl.track_at(&lanes, cy);
            e.tl.drop_preview = Some((ti, e.ed.snap_time(e.tl.t_of(cx).max(0.0))));
        }
        None => e.tl.drop_preview = None,
    }
}

fn drop_on_timeline(app: &mut AppState, payload: &Payload, x: f32, y: f32) {
    let Some((cx, cy)) = canvas_point(x, y) else { return };
    let Some(e) = app.editor.as_mut() else { return };
    let lanes = lanes(e.doc(), e.media(), e.tl.scale);
    let ti = e.tl.track_at(&lanes, cy);
    let track = ti.map(|i| e.doc().tracks[i].id.clone());
    let at = e.ed.snap_time(e.tl.t_of(cx).max(0.0));
    let hit = crate::timeline::input::hit(e, cx, cy);
    match payload {
        Payload::Media(id) => {
            crate::media::place_media(app, id, track.as_deref(), at);
        }
        Payload::Text(i) => {
            if let Some(p) = fig_timeline::text_presets().get(*i) {
                let id = e.ed.add_text_preset(track.as_deref(), at, p);
                e.ed.selection.select_only(&id);
            }
        }
        Payload::Effect(type_id) => {
            let owner = match hit {
                crate::timeline::geometry::Hit::Clip { clip, .. } => Some(clip),
                _ => track,
            };
            match owner {
                Some(owner) => add_effect(app, &owner, type_id),
                None => e.say("Drop an effect on a clip or a track"),
            }
        }
        Payload::Transition(type_id) => apply_transition_near(app, type_id, track.as_deref(), at),
    }
}

/// Puts a transition on the cut nearest `at` on the track (within 15 px), or after the clip under it.
fn apply_transition_near(app: &mut AppState, type_id: &str, track: Option<&str>, at: f64) {
    let Some(e) = app.editor.as_mut() else { return };
    let Some(track) = track else { return };
    let reach = 15.0 / f64::from(e.tl.pps);
    let Some(entry) = fig_fx::transitions::find(type_id) else { return };
    let r = fig_timeline::TransitionRef { type_id: type_id.to_string(), duration_sec: entry.default_duration_sec, params: Default::default() };
    let cut = e.ed.find_track(track).and_then(|t| {
        t.clips.windows(2).find(|w| (w[0].end_sec() - w[1].start_sec).abs() < 1e-3 && (w[0].end_sec() - at).abs() <= reach.max(0.25)).map(|w| (w[0].id.clone(), w[1].id.clone()))
    });
    let ok = match cut {
        Some((a, b)) => e.ed.apply_transition_at_cut(&a, &b, &r),
        None => match e.ed.find_clip_at(track, at).map(|c| c.id.clone()) {
            Some(id) => e.ed.apply_transition_after(&id, &r),
            None => false,
        },
    };
    if !ok {
        let why = e.ed.transition_validation_message.clone().unwrap_or_else(|| "Drop a transition on a cut between two clips".into());
        e.say(why);
    }
}

pub fn add_effect(app: &mut AppState, owner: &str, type_id: &str) {
    let Some(e) = app.editor.as_mut() else { return };
    let Some(entry) = fig_fx::effects::find(type_id) else { return };
    let mut fx = entry.create_instance();
    // on a track, an effect covers three seconds from the playhead
    if e.ed.find_track(owner).is_some() {
        fx.start_sec = Some(e.playhead);
        fx.dur_sec = Some(3.0);
    }
    let id = fx.id.clone();
    if e.ed.add_effect(owner, fx) {
        e.inspector.effect = Some(id);
        e.inspector.tab = Some(fig_timeline::Facet::EFFECTS);
        e.say(format!("Added {}", entry.display_name));
    }
}

pub fn drag_ghost(d: &LibDrag, t: &Tokens) -> El {
    if !d.started {
        return el();
    }
    el().absolute(d.x + 10.0, d.y + 8.0)
        .px(10.0)
        .h(26.0)
        .radius(6.0)
        .fill(t.accent.alpha(0.85))
        .center()
        .inert()
        .child(text(d.label.clone()).font(font(12.0, 600.0)).color(t.on_accent))
}

// ---- tabs -----------------------------------------------------------------------------------

fn media_tab(app: &AppState, e: &Editor, t: &Tokens) -> El {
    let top = row()
        .w_full()
        .gap(6.0)
        .child(search(e, "Search media", t).grow(1.0))
        .child(widgets::icon_button(&icons::PLUS, 14.0, 30.0, t).help("Import media (Ctrl+I)").on_press_tx(|_: &mut AppState, tx: &mut Tx| crate::media::import_dialog(tx)))
        .child(widgets::icon_button(&icons::ELLIPSIS, 14.0, 30.0, t).key("lib.media.more").on_press(|s: &mut AppState| {
            let items = vec![Item::new("Collect sources into the project folder", |s, _| crate::dialogs::collect_sources(s))];
            crate::menu::open_below(s, "lib.media.more", items);
        }));
    let media: Vec<_> = e.project.media.iter().filter(|m| m.reverse_of.is_none() && matches(e, m.file_name())).collect();
    if e.project.media.is_empty() {
        return col().w_full().grow(1.0).gap(8.0).child(top).child(
            col()
                .w_full()
                .grow(1.0)
                .center()
                .gap(8.0)
                .child(icon(&icons::FILM, 28.0).color(t.text_muted))
                .child(text("Drop media here or use Import").font(font(12.5, 400.0)).color(t.text_muted)),
        );
    }
    if media.is_empty() {
        return col().w_full().gap(8.0).child(top).child(text("No media matches your search").font(font(12.0, 400.0)).color(t.text_muted));
    }
    let selected: Vec<String> = e.ed.selection.items().iter().filter(|k| k.group == SelectionGroup::Media).map(|k| k.id.clone()).collect();
    let mut grid = row().w_full().flex_wrap().gap(8.0);
    for m in media {
        let thumb = m.thumbnail.as_deref().and_then(|p| app.media.thumbnail(p)).or_else(|| (m.kind == MediaKind::Image).then(|| app.media.thumbnail(&m.url)).flatten());
        let picked = selected.contains(&m.id);
        let mut well = el().w_full().h(72.0).fill(hex("#0e0f11")).radius(8.0).clip_rounded(8.0);
        well = match thumb {
            Some(p) => well.child(el().w_full().h_full().pixels_fit(p, rune::SurfaceFit::Fill)),
            None => well.center().child(icon(if m.kind == MediaKind::Audio { &icons::AUDIO_WAVEFORM } else { &icons::FILM }, 22.0).color(t.text_faint)),
        };
        if m.duration_sec > 0.0 && m.kind != MediaKind::Image {
            let s = m.duration_sec.round() as i64;
            well = well.child(
                el().absolute(0.0, 4.0).w_full().pr(4.0).child(
                    row().w_full().justify_end().child(el().px(5.0).h(16.0).radius(4.0).fill(hex("#a0000000")).center().child(text(format!("{}:{:02}", s / 60, s % 60)).font(mono(10.5, 500.0)).color(Color::WHITE))),
                ),
            );
        }
        well = well.child(
            el().absolute(4.0, 4.0)
                .w(18.0)
                .h(16.0)
                .radius(5.0)
                .fill(hex("#a0000000"))
                .center()
                .help(if m.offline { "The file is missing" } else if m.in_project { "In the project folder" } else { "Linked from elsewhere" })
                .child(icon(if m.offline { &icons::CIRCLE_ALERT } else if m.in_project { &icons::FOLDER_CHECK } else { &icons::LINK }, 11.0).color(if m.offline { hex("#ff6b6b") } else { Color::WHITE })),
        );
        if m.proxy_status == fig_timeline::ProxyStatus::Ready {
            well = well.child(el().absolute(4.0, 52.0).px(5.0).h(15.0).radius(4.0).fill(t.accent_soft).center().child(text("Proxy").font(font(9.5, 600.0)).color(t.accent_text)));
        }
        let (id_add, id_menu, id_dbl) = (m.id.clone(), m.id.clone(), m.id.clone());
        let name = m.file_name().to_string();
        let card = col()
            .w(118.0)
            .padding(4.0)
            .gap(4.0)
            .radius(RADIUS_CARD)
            .fill(t.card_bg)
            .border(if picked { 2.0 } else { 1.0 }, if picked { t.accent } else { t.border })
            .hover(t.card_bg_alt)
            .cursor(Cursor::Pointer)
            .child(well.child(
                el().absolute(96.0, 46.0).w(22.0).h(22.0).radius(11.0).fill(t.accent).center().help("Add to the timeline at the playhead").child(icon(&icons::PLUS, 13.0).color(t.on_accent)).on_press(move |s: &mut AppState| crate::media::add_to_timeline(s, &id_add)),
            ))
            .child(text(name.clone()).font(font(12.0, 400.0)).color(t.text_secondary).truncate().help(name.clone()))
            .on_double_press(move |s: &mut AppState| crate::media::add_to_timeline(s, &id_dbl))
            .on_secondary_press(move |s: &mut AppState, p: rune::PointerPress, _tx: &mut Tx| {
                let (a, b, c, d) = (id_menu.clone(), id_menu.clone(), id_menu.clone(), id_menu.clone());
                let items = vec![
                    Item::new("Add to timeline at the playhead", move |s, _| crate::media::add_to_timeline(s, &a)),
                    Item::new("Show in folder", move |s, _| {
                        let url = s.editor.as_ref().and_then(|e| e.find_media(&b)).map(|m| m.url.clone()).unwrap_or_default();
                        if let Err(err) = crate::media::reveal(&url) {
                            crate::toasts::show(s, err);
                        }
                    }),
                    Item::new("Generate proxy", move |s, _| crate::media::request_proxy(s, &c, true)),
                    Item::Separator,
                    Item::new("Remove", move |s, _| remove_media(s, &d)).danger(),
                ];
                crate::menu::open(s, crate::menu::Popup::at(p.x, p.y, items));
            });
        grid = grid.child(draggable(card, format!("lib.media.{}", m.id), Payload::Media(m.id.clone()), name, |s, p| {
            if let (Payload::Media(id), Some(e)) = (p, s.editor.as_mut()) {
                e.ed.selection.replace(SelectionKey::new(SelectionGroup::Media, id));
            }
        }));
    }
    col().w_full().grow(1.0).min_h(0.0).gap(8.0).child(top).child(scroll(grid))
}

fn remove_media(app: &mut AppState, id: &str) {
    let Some(e) = app.editor.as_mut() else { return };
    let used = e.doc().clips().any(|c| c.media_id() == Some(id));
    if used {
        e.say("That media is on the timeline: delete its clips first");
        return;
    }
    fig_media::library::Library::remove(&mut e.project, id);
    e.media_rev += 1;
}

fn templates_tab(app: &AppState, e: &Editor, t: &Tokens) -> El {
    let store = fig_project::templates::TemplateStore::new(crate::settings::templates_dir());
    let mut list = col().w_full().gap(8.0);
    let templates: Vec<_> = fig_project::templates::built_in_templates().into_iter().chain(store.list()).filter(|tp| matches(e, &tp.name)).collect();
    for tp in templates {
        let tid = tp.clone();
        list = list.child(
            col()
                .w_full()
                .padding(10.0)
                .gap(4.0)
                .radius(RADIUS_CARD)
                .fill(t.card_bg)
                .child(text(tp.name.clone()).font(font(12.5, 600.0)).color(t.text_primary))
                .child(widgets::note(tp.description.clone(), t))
                .child(
                    row()
                        .gap(8.0)
                        .center_y()
                        .child(text(format!("{} · {} · {}", tp.duration_label(), tp.canvas_label(), tp.slot_label())).font(font(11.0, 400.0)).color(t.text_muted).grow(1.0))
                        .child(widgets::button("Use", Kind::Primary, t).h(26.0).on_press(move |s: &mut AppState| use_template(s, &tid))),
                ),
        );
    }
    let _ = app;
    col()
        .w_full()
        .grow(1.0)
        .min_h(0.0)
        .gap(8.0)
        .child(row().w_full().gap(6.0).child(search(e, "Search templates", t).grow(1.0)).child(
            widgets::icon_button(&icons::PLUS, 14.0, 30.0, t).help("Save this timeline as a template").on_press(|s: &mut AppState| save_template(s)),
        ))
        .child(scroll(list))
}

/// Fills a template's slots with the selected media items (in order) and opens it as a new timeline.
fn use_template(app: &mut AppState, tp: &fig_project::templates::VideoTemplate) {
    let prefs = app.preferences.clone();
    let Some(e) = app.editor.as_mut() else { return };
    let picked: Vec<_> = e.ed.selection.items().iter().filter(|k| k.group == SelectionGroup::Media).filter_map(|k| e.find_media(&k.id).cloned()).collect();
    let mut pool = if picked.is_empty() { e.project.media.iter().filter(|m| m.reverse_of.is_none()).cloned().collect() } else { picked };
    let mut assignments = std::collections::HashMap::new();
    for slot in &tp.slots {
        if let Some(i) = pool.iter().position(|m| m.kind == slot.kind || (slot.kind == MediaKind::Video && m.kind == MediaKind::Image)) {
            assignments.insert(slot.id.clone(), pool.remove(i));
        }
    }
    match fig_project::templates::instantiate(tp, &assignments) {
        Ok(mut tl) => {
            tl.name = Some(tp.name.clone());
            e.commit_to_project();
            e.project.timelines.push(tl);
            let i = e.project.timelines.len() - 1;
            e.switch_timeline(i, &prefs);
            e.say(format!("Opened \u{201c}{}\u{201d} as a new timeline", tp.name));
        }
        Err(err) => e.say(err),
    }
}

fn save_template(app: &mut AppState) {
    crate::dialogs::prompt(app, "Save as template", "Template name", "My template", "Save", |s, name| {
        let Some(e) = s.editor.as_ref() else { return };
        let tp = fig_project::templates::from_timeline(name.trim(), "Custom", e.doc(), &e.project.media, None);
        let store = fig_project::templates::TemplateStore::new(crate::settings::templates_dir());
        match store.save(&tp) {
            Ok(()) => crate::toasts::show(s, "Template saved"),
            Err(err) => crate::toasts::show(s, format!("Couldn't save the template: {err}")),
        }
    });
}

fn text_tab(e: &Editor, t: &Tokens) -> El {
    let mut grid = row().w_full().flex_wrap().gap(8.0);
    for (i, p) in fig_timeline::text_presets().into_iter().enumerate() {
        if !matches(e, p.name) {
            continue;
        }
        let colour = hex(&format!("#{:08x}", p.style.color));
        let card = col()
            .w(118.0)
            .gap(4.0)
            .padding(4.0)
            .radius(RADIUS_CARD)
            .fill(t.card_bg)
            .hover(t.card_bg_alt)
            .cursor(Cursor::Pointer)
            .child(
                el().w_full()
                    .h(44.0)
                    .radius(8.0)
                    .fill(hex("#0e0f11"))
                    .center()
                    .child(text(p.text.lines().next().unwrap_or("Aa").to_string()).font(font(13.0, if p.style.bold { 700.0 } else { 400.0 })).color(colour).truncate().max_w(110.0)),
            )
            .child(text(p.name).font(font(11.0, 400.0)).color(t.text_secondary).truncate());
        grid = grid.child(draggable(card, format!("lib.text.{i}"), Payload::Text(i), p.name.to_string(), |s, p| {
            if let (Payload::Text(i), Some(e)) = (p, s.editor.as_mut())
                && let Some(preset) = fig_timeline::text_presets().get(*i)
            {
                let at = e.playhead;
                let id = e.ed.add_text_preset(None, at, preset);
                e.ed.selection.select_only(&id);
            }
        }));
    }
    col()
        .w_full()
        .grow(1.0)
        .min_h(0.0)
        .gap(8.0)
        .child(
            row()
                .gap(6.0)
                .child(widgets::icon_text_button(&icons::PLUS, "Add text", Kind::Primary, t).on_press(|s: &mut AppState| {
                    if let Some(e) = s.editor.as_mut() {
                        let at = e.playhead;
                        let id = e.ed.add_text(None, at, "Your text", fig_timeline::TextStyle::default(), 3.0, fig_timeline::LayerTransform::default());
                        e.ed.selection.select_only(&id);
                    }
                }))
                .child(widgets::button("Auto captions…", Kind::Normal, t).on_press(|s: &mut AppState| crate::dialogs::captions::open(s))),
        )
        .child(widgets::note("Click a style to add it at the playhead, or drag it onto a video track.", t))
        .child(scroll(grid))
}

fn transitions_tab(e: &Editor, t: &Tokens) -> El {
    let mut grid = row().w_full().flex_wrap().gap(8.0);
    for entry in fig_fx::transitions::all() {
        if !matches(e, entry.display_name) {
            continue;
        }
        let card = col()
            .w(118.0)
            .gap(4.0)
            .padding(4.0)
            .radius(RADIUS_CARD)
            .fill(t.card_bg)
            .hover(t.card_bg_alt)
            .cursor(Cursor::Pointer)
            .help(entry.description.to_string())
            .child(el().w_full().h(44.0).radius(8.0).fill(hex("#0e0f11")).center().child(icon(&icons::ARROW_RIGHT_LEFT, 18.0).color(hex("#eab308"))))
            .child(text(entry.display_name).font(font(11.0, 400.0)).color(t.text_secondary).truncate());
        grid = grid.child(draggable(card, format!("lib.tr.{}", entry.type_id), Payload::Transition(entry.type_id.to_string()), entry.display_name.to_string(), |s, p| {
            // a click puts it after the selected clip
            let Payload::Transition(type_id) = p else { return };
            let Some(e) = s.editor.as_ref() else { return };
            let Some(clip) = e.ed.selection.selected_clip_ids().into_iter().next() else {
                if let Some(e) = s.editor.as_mut() {
                    e.say("Select a clip, or drag the transition onto a cut");
                }
                return;
            };
            let end = e.ed.find_clip(&clip).map_or(0.0, |c| c.end_sec());
            let track = e.ed.find_clip_track_id(&clip);
            apply_transition_near(s, type_id, track.as_deref(), end - 1e-3);
        }));
    }
    col().w_full().grow(1.0).min_h(0.0).gap(8.0).child(search(e, "Search transitions", t)).child(widgets::note("Drag a transition onto a cut.", t)).child(scroll(grid))
}

fn effects_tab(e: &Editor, t: &Tokens) -> El {
    let mut list = col().w_full().gap(6.0);
    let presets = fig_fx::presets::built_in();
    let presets: Vec<_> = presets.into_iter().filter(|p| matches(e, &p.name)).collect();
    if !presets.is_empty() {
        list = list.child(widgets::heading("Presets", t));
        for p in presets {
            let preset = p.clone();
            list = list.child(
                row()
                    .w_full()
                    .padding(8.0)
                    .gap(8.0)
                    .radius(8.0)
                    .fill(t.card_bg)
                    .hover(t.card_bg_alt)
                    .center_y()
                    .child(el().w(28.0).h(28.0).radius(14.0).fill(hex("#2a3d61")).center().child(icon(&icons::SPARKLE, 14.0).color(t.accent_text)))
                    .child(col().grow(1.0).min_w(0.0).child(text(p.name.clone()).font(font(12.0, 600.0)).color(t.text_primary).truncate()).child(text(p.description.clone()).font(font(10.5, 400.0)).color(t.text_muted).truncate()))
                    .child(widgets::icon_button(&icons::PLUS, 13.0, 24.0, t).help("Add to the selected clip").on_press(move |s: &mut AppState| add_preset(s, &preset))),
            );
        }
    }
    let mut by_cat: Vec<(String, Vec<std::sync::Arc<fig_fx::effects::EffectEntry>>)> = Vec::new();
    for entry in fig_fx::effects::all() {
        if !matches(e, &entry.display_name) {
            continue;
        }
        match by_cat.iter_mut().find(|(c, _)| *c == entry.category) {
            Some((_, l)) => l.push(entry),
            None => by_cat.push((entry.category.clone(), vec![entry])),
        }
    }
    for (cat, entries) in by_cat {
        list = list.child(widgets::heading(cat, t).pt(6.0));
        for entry in entries {
            let tid = entry.type_id.clone();
            let card = row()
                .w_full()
                .padding(8.0)
                .gap(8.0)
                .radius(8.0)
                .fill(t.card_bg)
                .hover(t.card_bg_alt)
                .center_y()
                .cursor(Cursor::Pointer)
                .child(el().w(28.0).h(28.0).radius(6.0).fill(hex("#6f5526")).center().child(icon(&icons::WAND_SPARKLES, 14.0).color(hex("#e0bd6e"))))
                .child(col().grow(1.0).min_w(0.0).child(text(entry.display_name.clone()).font(font(12.0, 600.0)).color(t.text_primary).truncate()).child(text(entry.description.clone()).font(font(10.5, 400.0)).color(t.text_muted).truncate()))
                .child(widgets::icon_button(&icons::PLUS, 13.0, 24.0, t).help("Add to the selected clip or track").on_press(move |s: &mut AppState| add_to_selection(s, &tid)));
            list = list.child(draggable(card, format!("lib.fx.{}", entry.type_id), Payload::Effect(entry.type_id.clone()), entry.display_name.clone(), |s, p| {
                if let Payload::Effect(t) = p {
                    add_to_selection(s, t);
                }
            }));
        }
    }
    col()
        .w_full()
        .grow(1.0)
        .min_h(0.0)
        .gap(8.0)
        .child(row().w_full().gap(6.0).child(search(e, "Search effects", t).grow(1.0)).child(
            widgets::icon_button(&icons::PLUS, 14.0, 30.0, t).help("New graph effect").on_press(|s: &mut AppState| crate::graph::open(s, None)),
        ))
        .child(scroll(list))
        .child(widgets::note("Drag onto a video track, or select a track or clip and press +.", t))
}

fn add_to_selection(app: &mut AppState, type_id: &str) {
    let Some(e) = app.editor.as_ref() else { return };
    let owner = e.ed.selection.selected_clip_ids().into_iter().next().or_else(|| e.tl.track_selected.clone());
    match owner {
        Some(owner) => add_effect(app, &owner, type_id),
        None => {
            if let Some(e) = app.editor.as_mut() {
                e.say("Select a clip or a track first");
            }
        }
    }
}

fn add_preset(app: &mut AppState, preset: &fig_fx::presets::EffectPreset) {
    let Some(e) = app.editor.as_mut() else { return };
    let Some(owner) = e.ed.selection.selected_clip_ids().into_iter().next() else {
        e.say("Select a clip first");
        return;
    };
    let Some(clip) = e.ed.find_clip(&owner).cloned() else { return };
    let at = (e.playhead - clip.start_sec).clamp(0.0, clip.dur_sec);
    if let Some(fx) = fig_fx::presets::instantiate(preset, clip.dur_sec, at) {
        e.ed.add_effect(&owner, fx);
        e.say(format!("Added {}", preset.name));
    }
}

fn markers_tab(e: &Editor, t: &Tokens) -> El {
    let all = fig_edit::markers::all_markers(e.doc());
    let mut list = col().w_full().gap(4.0);
    for loc in all.into_iter().filter(|l| matches(e, &l.marker.name)) {
        let m = loc.marker.clone();
        let picked = e.ed.selection.is_marker_selected(&m.id);
        let at = loc.timeline_sec();
        let id = m.id.clone();
        let sub = match &loc.owner {
            fig_edit::markers::MarkerOwner::Clip(_) => format!("{} · on a clip", e.timecode(at)),
            _ if m.is_range() => format!("{} – {}", e.timecode(at), e.timecode(at + m.dur_sec)),
            _ => e.timecode(at),
        };
        list = list.child(
            row()
                .w_full()
                .min_h(46.0)
                .gap(8.0)
                .radius(6.0)
                .fill(if picked { hex("#222f48") } else { t.card_bg })
                .border(1.0, if picked { t.accent } else { Color::TRANSPARENT })
                .hover(t.card_bg_alt)
                .center_y()
                .child(el().w(4.0).h(30.0).ml(6.0).radius(2.0).fill(hex(if m.color.is_empty() { "#ffd60a" } else { &m.color })))
                .child(col().grow(1.0).min_w(0.0).child(text(m.name.clone()).font(font(12.0, 600.0)).color(t.text_primary).truncate()).child(text(sub).font(font(11.0, 400.0)).color(t.text_muted)))
                .on_press_tx(move |s: &mut AppState, tx: &mut Tx| {
                    let mods = tx.mods();
                    if let Some(e) = s.editor.as_mut() {
                        e.ed.select_marker(&id, mods.cmd, mods.shift, None);
                    }
                    if !mods.cmd && !mods.shift {
                        crate::editor::seek(s, at, false);
                    }
                }),
        );
    }
    col()
        .w_full()
        .grow(1.0)
        .min_h(0.0)
        .gap(8.0)
        .child(search(e, "Search markers", t))
        .child(
            row()
                .gap(6.0)
                .child(widgets::button("+ Point", Kind::Normal, t).on_press_tx(|s: &mut AppState, tx: &mut Tx| crate::actions::run(s, crate::commands::Command::AddMarker, tx)))
                .child(widgets::button("+ Range", Kind::Normal, t).on_press(|s: &mut AppState| {
                    if let Some(e) = s.editor.as_mut() {
                        let at = e.playhead;
                        let n = e.doc().markers.iter().filter(|m| m.is_range()).count() + 1;
                        e.ed.add_range_marker(at, 2.0, &format!("Range {n}"), "#5f9cf8");
                    }
                }))
                .child(widgets::button("Split video at markers", Kind::Ghost, t).on_press(|s: &mut AppState| {
                    if let Some(e) = s.editor.as_mut() {
                        let n = e.ed.split_video_at_selected_markers();
                        e.say(format!("{n} cut(s) made"));
                    }
                })),
        )
        .child(widgets::note("Ctrl-click to toggle · Shift-click for a range · Delete to remove", t))
        .child(scroll(list))
        .child(widgets::button("Export marked ranges", Kind::Primary, t).on_press_tx(|s: &mut AppState, tx: &mut Tx| crate::actions::run(s, crate::commands::Command::ExportRanges, tx)))
}

fn captions_tab(e: &Editor, t: &Tokens) -> El {
    let cues = e.ed.caption_cues(0.0, f64::MAX);
    let mut list = col().w_full().gap(4.0);
    let current = cues.iter().position(|c| e.playhead >= c.start_sec && e.playhead < c.end_sec);
    for (i, cue) in cues.iter().enumerate() {
        let at = cue.start_sec;
        let id = e
            .doc()
            .clips()
            .find(|c| c.as_text().is_some_and(|t| t.is_caption) && c.start_sec <= at + 1e-6 && at < c.end_sec())
            .map_or_else(String::new, |c| c.id.clone());
        let picked = e.ed.selection.is_selected(&id);
        list = list.child(
            row()
                .w_full()
                .padding(8.0)
                .gap(8.0)
                .radius(6.0)
                .fill(if picked { hex("#222f48") } else { t.card_bg })
                .border(1.0, if current == Some(i) { t.accent } else { Color::TRANSPARENT })
                .hover(t.card_bg_alt)
                .child(text(e.timecode(at)).font(mono(10.5, 400.0)).color(t.text_muted).w(76.0))
                .child(text(cue.text.clone()).font(font(12.0, 400.0)).color(t.text_secondary).wrap().grow(1.0).min_w(0.0))
                .on_press(move |s: &mut AppState| {
                    if let Some(e) = s.editor.as_mut() {
                        e.ed.selection.select_only(&id);
                        e.inspector.tab = Some(fig_timeline::Facet::TEXT);
                    }
                    crate::editor::seek(s, at, false);
                }),
        );
    }
    let empty = cues.is_empty();
    let mut c = col().w_full().grow(1.0).min_h(0.0).gap(8.0).child(
        row()
            .gap(6.0)
            .child(widgets::button("Auto captions…", Kind::Primary, t).on_press(|s: &mut AppState| crate::dialogs::captions::open(s)))
            .child(widgets::button("Export…", Kind::Normal, t).on_press_tx(|s: &mut AppState, tx: &mut Tx| crate::dialogs::export_captions(s, tx))),
    );
    if empty {
        c = c.child(col().w_full().grow(1.0).center().gap(6.0).child(icon(&icons::CAPTIONS, 26.0).color(t.text_muted)).child(text("No captions yet").font(font(12.5, 500.0)).color(t.text_muted)));
    } else {
        c = c.child(widgets::note("Click a caption to edit its words in the inspector.", t)).child(scroll(list));
    }
    c
}
