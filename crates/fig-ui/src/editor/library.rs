//! The library panel (left): an icon rail and seven tabs: Media, Templates, Text, Transitions, Effects, Markers and
//! Captions. Cards can be clicked or double-clicked, and dragged onto the timeline.

use fig_timeline::{MediaKind, SelectionGroup, SelectionKey};
use rune::{Color, Cursor, Drag, El, Phase, TextEdit, Tx, col, el, icon, row, text, text_input};

use crate::app::AppState;
use crate::editor::Editor;
use crate::ui::icons;
use crate::ui::menu::Item;
use crate::ui::theme::{RADIUS_CARD, Tokens, font, hex, mono};
use crate::timeline::geometry::HEADER_W;
use crate::ui::widgets::{self, Kind};

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
    /// Effect presets saved from an edit (`~/.fig/presets`).
    pub user_presets: Vec<fig_fx::presets::EffectPreset>,
    pub captions: CaptionsPanel,
}

/// The Captions tab (`CaptionsPanelViewModel`): which captions track it lists, find and replace, and the caption
/// being typed in.
#[derive(Default)]
pub struct CaptionsPanel {
    pub track: usize,
    pub find: TextEdit,
    pub replace: TextEdit,
    pub match_case: bool,
    pub editing: Option<(String, TextEdit)>,
}

impl LibraryState {
    pub fn new() -> LibraryState {
        let mut l = LibraryState {
            tab: Tab::Media,
            search: TextEdit::new(""),
            category: None,
            user_presets: Vec::new(),
            captions: CaptionsPanel::default(),
        };
        l.load_presets();
        l
    }

    pub fn load_presets(&mut self) {
        self.user_presets = fig_fx::presets::EffectPresetStore::new(crate::settings::presets_dir()).list();
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
                .on_press_tx(|s: &mut AppState, tx: &mut Tx| crate::commands::actions::run(s, crate::commands::Command::MediaPanel, tx)),
        );
    let content = match tab {
        Tab::Media => media_tab(app, e, t),
        Tab::Templates => templates_tab(app, e, t),
        Tab::Text => text_tab(e, t),
        Tab::Transitions => transitions_tab(e, t),
        Tab::Effects => effects_tab(e, t),
        Tab::Markers => markers_tab(e, t),
        Tab::Captions => captions_tab(app, e, t),
    };
    row()
        .w_full()
        .h_full()
        .child(rail(e, t))
        .child(el().w(1.0).h_full().fill(t.border))
        .child(col().grow(1.0).min_w(0.0).min_h(0.0).h_full().padding(10.0).gap(8.0).child(header).child(content))
}

fn search(e: &Editor, placeholder: &str, t: &Tokens) -> El {
    widgets::text_box(&e.library.search, placeholder, |s| &mut s.editor.as_mut().unwrap().library.search, t).w_full()
}

fn matches(e: &Editor, name: &str) -> bool {
    let q = e.library.search.text.trim().to_lowercase();
    q.is_empty() || name.to_lowercase().contains(&q)
}

fn scroll(content: El) -> El {
    col().w_full().grow(1.0).min_h(0.0).scroll(rune::ScrollSpec { stick_bottom: false }).child(content.w_full())
}

// ---- dragging onto the timeline --------------------------------------------------------------

/// A card's drag: past 8 points it becomes a library drag; letting go over the timeline drops it there, and a press
/// without a drag runs `click`.
fn draggable(card: El, key: String, payload: Payload, label: String, click: fn(&mut AppState, &Payload)) -> El {
    let k = key.clone();
    crate::ui::layout::tracked(card, key).drag_threshold(0.0).on_drag(move |s: &mut AppState, d: &mut Drag, tx: &mut Tx| {
        let origin = crate::ui::layout::rect(&k).unwrap_or([0.0; 4]);
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
    let r = crate::ui::layout::rect("timeline.canvas")?;
    (x >= r[0] && x <= r[0] + r[2] && y >= r[1] && y <= r[1] + r[3]).then(|| (x - r[0], y - r[1]))
}

fn update_drop_preview(app: &mut AppState, x: f32, y: f32) {
    let Some(e) = app.editor.as_mut() else { return };
    match canvas_point(x, y).filter(|(cx, _)| *cx > HEADER_W) {
        Some((cx, cy)) => {
            let lanes = e.lanes();
            let ti = e.tl.track_at(&lanes, cy);
            e.tl.drop_preview = Some((ti, e.ed.snap_time(e.tl.t_of(cx).max(0.0))));
        }
        None => e.tl.drop_preview = None,
    }
}

fn drop_on_timeline(app: &mut AppState, payload: &Payload, x: f32, y: f32) {
    let Some((cx, cy)) = canvas_point(x, y) else { return };
    let Some(e) = app.editor.as_mut() else { return };
    let lanes = e.lanes();
    let ti = e.tl.track_at(&lanes, cy);
    let track = ti.map(|i| e.doc().tracks[i].id.clone());
    let at = e.ed.snap_time(e.tl.t_of(cx).max(0.0));
    let hit = crate::timeline::input::hit(e, cx, cy);
    match payload {
        Payload::Media(id) => {
            crate::editor::media::place_media(app, id, track.as_deref(), at);
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
                Some(owner) => {
                    add_effect(app, &owner, type_id);
                }
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

/// Adds an effect to a clip or a track and shows it in the inspector. Returns its id.
pub fn add_effect(app: &mut AppState, owner: &str, type_id: &str) -> Option<String> {
    let e = app.editor.as_mut()?;
    let mut fx = fig_fx::effects::find(type_id)?.create_instance();
    // on a track, an effect covers three seconds from the playhead
    if e.ed.find_track(owner).is_some() {
        fx.start_sec = Some(e.playhead);
        fx.dur_sec = Some(3.0);
    }
    add_effect_instance(app, owner, fx)
}

/// Adds an effect made elsewhere and shows it in the inspector; returns its id.
pub fn add_effect_instance(app: &mut AppState, owner: &str, fx: fig_timeline::EffectInstance) -> Option<String> {
    let e = app.editor.as_mut()?;
    let name = fig_fx::effects::find(&fx.type_id)?.display_name.clone();
    let id = fx.id.clone();
    if e.ed.add_effect(owner, fx) {
        e.inspector.effect = Some(id.clone());
        e.inspector.tab = Some(fig_timeline::Facet::EFFECTS);
        e.say(format!("Added {name}"));
        return Some(id);
    }
    None
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
        .child(widgets::icon_button(&icons::PLUS, 14.0, 30.0, t).help("Import media (Ctrl+I)").on_press_tx(|_: &mut AppState, tx: &mut Tx| crate::editor::media::import_dialog(tx)))
        .child(widgets::icon_button(&icons::ELLIPSIS, 14.0, 30.0, t).key("lib.media.more").on_press(|s: &mut AppState| {
            let items = vec![Item::new("Collect sources into the project folder", |s, _| crate::dialogs::collect_sources(s))];
            crate::ui::menu::open_below(s, "lib.media.more", items);
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
    let mut grid = row().w_full().flex_wrap().items_start().gap(8.0);
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
        if m.uses_proxy() {
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
                // the add button in the picture's lower right corner, inside it
                el().absolute(0.0, 46.0).w_full().pr(4.0).child(
                    row().w_full().justify_end().child(
                        el().w(22.0)
                            .h(22.0)
                            .radius(11.0)
                            .fill(t.accent)
                            .center()
                            .help("Add to the timeline at the playhead")
                            .child(icon(&icons::PLUS, 13.0).color(t.on_accent))
                            .on_press(move |s: &mut AppState| crate::editor::media::add_to_timeline(s, &id_add)),
                    ),
                ),
            ))
            .child(text(name.clone()).font(font(12.0, 400.0)).color(t.text_secondary).truncate().help(name.clone()))
            .on_double_press(move |s: &mut AppState| crate::editor::media::add_to_timeline(s, &id_dbl))
            .on_secondary_press(move |s: &mut AppState, p: rune::PointerPress, _tx: &mut Tx| {
                let (a, b, c, d, f) = (id_menu.clone(), id_menu.clone(), id_menu.clone(), id_menu.clone(), id_menu.clone());
                let proxy = s.editor.as_ref().and_then(|e| e.find_media(&id_menu)).filter(|m| m.has_proxy()).map(|m| m.proxy_off);
                let mut items = vec![
                    Item::new("Add to timeline at the playhead", move |s, _| crate::editor::media::add_to_timeline(s, &a)),
                    Item::new("Show in folder", move |s, _| {
                        let url = s.editor.as_ref().and_then(|e| e.find_media(&b)).map(|m| m.url.clone()).unwrap_or_default();
                        if let Err(err) = crate::editor::media::reveal(&url) {
                            crate::ui::toasts::show(s, err);
                        }
                    }),
                    Item::new(if proxy.is_some() { "Remake proxy" } else { "Generate proxy" }, move |s, _| crate::editor::media::request_proxy(s, &c, true)),
                ];
                if let Some(off) = proxy {
                    items.push(Item::new("Use proxy", move |s, _| crate::editor::media::set_proxy_off(s, &f, !off)).checked(!off));
                }
                items.extend([Item::Separator, Item::new("Remove", move |s, _| remove_media(s, &d)).danger()]);
                crate::ui::menu::open(s, crate::ui::menu::Popup::at(p.x, p.y, items));
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
            Ok(()) => crate::ui::toasts::show(s, "Template saved"),
            Err(err) => crate::ui::toasts::show(s, format!("Couldn't save the template: {err}")),
        }
    });
}

fn text_tab(e: &Editor, t: &Tokens) -> El {
    let mut grid = row().w_full().flex_wrap().items_start().gap(8.0);
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
    let mut grid = row().w_full().flex_wrap().items_start().gap(8.0);
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
            .child(match card_preview(entry.type_id, true) {
                Some(p) => el().w_full().h(44.0).radius(8.0).clip_rounded(8.0).pixels(&p),
                None => el().w_full().h(44.0).radius(8.0).fill(hex("#0e0f11")).center().child(icon(&icons::ARROW_RIGHT_LEFT, 18.0).color(hex("#eab308"))),
            })
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

const EFFECT_CATEGORIES: [&str; 10] = ["All", "Motion", "Blur", "Light", "Glitch", "Retro", "Distort", "Color", "Stylize", "Custom"];

/// A row card in the Effects tab.
fn effect_card(icon_fill: &str, glyph: &'static rune::Icon, glyph_color: Color, name: String, description: String, preview: Option<rune::Pixels>, t: &Tokens) -> El {
    let picture = match preview {
        Some(p) => el().w(48.0).h(32.0).radius(5.0).clip_rounded(5.0).pixels(&p),
        None => el().w(28.0).h(28.0).radius(6.0).fill(hex(icon_fill)).center().child(icon(glyph, 14.0).color(glyph_color)),
    };
    row()
        .w_full()
        .padding(8.0)
        .gap(8.0)
        .radius(8.0)
        .fill(t.card_bg)
        .hover(t.card_bg_alt)
        .center_y()
        .cursor(Cursor::Pointer)
        .child(picture)
        .child(
            col()
                .grow(1.0)
                .min_w(0.0)
                .child(text(name).font(font(12.0, 600.0)).color(t.text_primary).truncate())
                .child(text(description).font(font(10.5, 400.0)).color(t.text_muted).truncate()),
        )
}

fn effects_tab(e: &Editor, t: &Tokens) -> El {
    let category = e.library.category.clone().unwrap_or_else(|| "All".into());
    let in_category = |c: &str| category == "All" || c.eq_ignore_ascii_case(&category);
    let mut list = col().w_full().gap(6.0);
    // presets: the built-in ones, then the ones saved from an edit
    let presets: Vec<_> = fig_fx::presets::built_in()
        .into_iter()
        .chain(e.library.user_presets.iter().cloned())
        .filter(|p| matches(e, &p.name) && (category == "All" || category == "Motion"))
        .collect();
    let any_presets = !presets.is_empty();
    if any_presets {
        list = list.child(widgets::heading("Presets", t));
        for p in presets {
            let (add, id, name) = (p.clone(), p.id.clone(), p.name.clone());
            let mut card = effect_card("#2a3d61", &icons::SPARKLE, t.accent_text, p.name.clone(), p.description.clone(), None, t)
                .help("Drag onto a video clip, or select a clip and press +")
                .child(widgets::icon_button(&icons::PLUS, 13.0, 24.0, t).help("Add to the selected clip").on_press(move |s: &mut AppState| add_preset(s, &add)));
            let menu_preset = p.clone();
            let user = !p.built_in;
            card = card.on_secondary_press(move |s: &mut AppState, at: rune::PointerPress, _tx: &mut Tx| {
                let add = menu_preset.clone();
                let (id, name) = (id.clone(), name.clone());
                let mut items = vec![Item::new("Add to the selected clip", move |s, _| add_preset(s, &add))];
                if user {
                    items.push(Item::new("Delete preset", move |s, _| delete_preset(s, &id, &name)).danger());
                }
                crate::ui::menu::open(s, crate::ui::menu::Popup::at(at.x, at.y, items));
            });
            list = list.child(card);
        }
    }
    let mut by_cat: Vec<(String, Vec<std::sync::Arc<fig_fx::effects::EffectEntry>>)> = Vec::new();
    for entry in fig_fx::effects::all() {
        let custom = entry.type_id.starts_with("graph:");
        if !matches(e, &entry.display_name) || !(in_category(&entry.category) || (custom && category == "Custom")) {
            continue;
        }
        match by_cat.iter_mut().find(|(c, _)| *c == entry.category) {
            Some((_, l)) => l.push(entry),
            None => by_cat.push((entry.category.clone(), vec![entry])),
        }
    }
    if by_cat.is_empty() && !any_presets {
        list = list.child(text("No effects match your search").font(font(12.0, 400.0)).color(t.text_muted).pt(24.0));
    }
    for (cat, entries) in by_cat {
        list = list.child(widgets::heading(cat, t).pt(6.0));
        for entry in entries {
            let (tid, menu_tid, name) = (entry.type_id.clone(), entry.type_id.clone(), entry.display_name.clone());
            let custom = entry.type_id.starts_with("graph:");
            let card = effect_card("#6f5526", &icons::WAND_SPARKLES, hex("#e0bd6e"), entry.display_name.clone(), entry.description.clone(), card_preview(&entry.type_id, false), t)
                .help("Drag onto a video clip")
                .child(widgets::icon_button(&icons::PLUS, 13.0, 24.0, t).help("Add to the selected clip or track").on_press(move |s: &mut AppState| add_to_selection(s, &tid)))
                .on_secondary_press(move |s: &mut AppState, at: rune::PointerPress, _tx: &mut Tx| {
                    let items = effect_menu(&menu_tid, &name, custom);
                    crate::ui::menu::open(s, crate::ui::menu::Popup::at(at.x, at.y, items));
                });
            list = list.child(draggable(card, format!("lib.fx.{}", entry.type_id), Payload::Effect(entry.type_id.clone()), entry.display_name.clone(), |s, p| {
                if let Payload::Effect(t) = p {
                    add_to_selection(s, t);
                }
            }));
        }
    }
    let pick = category.clone();
    col()
        .w_full()
        .grow(1.0)
        .min_h(0.0)
        .gap(8.0)
        .child(widgets::note("Drag onto a video track, or select a track or clip and press +.", t))
        .child(
            row()
                .w_full()
                .gap(6.0)
                .child(search(e, "Search effects", t).grow(1.0))
                .child(widgets::icon_button(&icons::PLUS, 14.0, 30.0, t).key("lib.fx.new").help("Build a new effect from nodes").on_press(|s: &mut AppState| crate::editor::graph::open(s, None)))
                .child(widgets::icon_button(&icons::IMPORT, 14.0, 30.0, t).help("Import an effect file").on_press_tx(|_s: &mut AppState, tx: &mut Tx| import_graph(tx))),
        )
        .child(widgets::dropdown("lib.fx.category", pick, t).w_full().on_press(|s: &mut AppState| {
            let items = EFFECT_CATEGORIES
                .iter()
                .map(|c| {
                    let c = c.to_string();
                    Item::new(c.clone(), move |s, _| {
                        if let Some(e) = s.editor.as_mut() {
                            e.library.category = (c != "All").then(|| c.clone());
                        }
                    })
                })
                .collect();
            crate::ui::menu::open_below(s, "lib.fx.category", items);
        }))
        .child(scroll(list))
}

/// The right-click menu on an effect in the library; a custom (node) effect can also be edited, copied, exported and
/// deleted.
fn effect_menu(type_id: &str, name: &str, custom: bool) -> Vec<Item> {
    let tid = type_id.to_string();
    let mut items = vec![Item::new("Add to the selected clip", move |s, _| add_to_selection(s, &tid))];
    if !custom {
        return items;
    }
    let id = type_id.trim_start_matches("graph:").to_string();
    let (t1, i2, i3, i4, n3, n4) = (type_id.to_string(), id.clone(), id.clone(), id, name.to_string(), name.to_string());
    items.extend([
        Item::new("Edit graph…", move |s, _| crate::editor::graph::open(s, Some(&t1))),
        Item::new("Duplicate", move |s, _| {
            if let Some(e) = s.editor.as_mut()
                && let Some(copy) = e.graphs.duplicate(&i2).map(|g| g.name.clone())
            {
                e.media_rev += 1;
                e.say(format!("Created \"{copy}\""));
            }
        }),
        Item::new("Export…", move |_s, tx| export_graph(tx, &i3, &n3)),
        Item::new("Delete", move |s, _| {
            let Some(e) = s.editor.as_mut() else { return };
            let type_id = format!("graph:{i4}");
            let uses = e.doc().tracks.iter().map(|t| t.effects.iter().chain(t.clips.iter().flat_map(|c| &c.effects)).filter(|f| f.type_id == type_id).count()).sum::<usize>();
            e.graphs.delete(&i4);
            e.media_rev += 1;
            e.say(if uses > 0 {
                format!("Deleted \"{n4}\". {uses} use{} on the timeline will show the original picture.", if uses == 1 { "" } else { "s" })
            } else {
                format!("Deleted \"{n4}\"")
            });
        })
        .danger(),
    ]);
    items
}

fn import_graph(tx: &mut Tx) {
    let options = rune::file_dialog::FileDialogOptions { title: "Import effect".into(), ..Default::default() }.filter("Fig effect", &["json"]);
    tx.open_file(options, |s: &mut AppState, result, _tx| {
        let Ok(Some(path)) = result else { return };
        let Some(e) = s.editor.as_mut() else { return };
        let imported = e.graphs.import(&path).map(|g| g.name.clone());
        match imported {
            Ok(name) => {
                e.media_rev += 1;
                e.say(format!("Imported \"{name}\""));
            }
            Err(_) => e.say("That file is not a valid effect."),
        }
    });
}

fn export_graph(tx: &mut Tx, id: &str, name: &str) {
    let options = rune::file_dialog::FileDialogOptions {
        title: "Export effect".into(),
        file_name: Some(format!("{name}.figgraph.json")),
        ..Default::default()
    }
    .filter("Fig effect", &["json"]);
    let (id, name) = (id.to_string(), name.to_string());
    tx.save_file(options, move |s: &mut AppState, result, _tx| {
        let Ok(Some(path)) = result else { return };
        let Some(e) = s.editor.as_mut() else { return };
        match e.graphs.export(&id, &path) {
            Ok(()) => e.say(format!("Exported \"{name}\"")),
            Err(_) => e.say("Could not write that file."),
        }
    });
}

/// Saves an effect as the user's own preset (`SaveEffectAsPresetAsync`).
pub fn save_effect_preset(app: &mut AppState, owner: &str, fx: &str) {
    let (owner, fx) = (owner.to_string(), fx.to_string());
    crate::dialogs::prompt(app, "Save as preset", "Preset name", "My preset", "Save", move |s, name| {
        let Some(e) = s.editor.as_mut() else { return };
        let Some(effect) = e.ed.find_effect(&owner, &fx) else { return };
        let mut preset = fig_fx::presets::from_effect(effect, name);
        preset.description = "Saved from your own edit.".into();
        match fig_fx::presets::EffectPresetStore::new(crate::settings::presets_dir()).save(&preset) {
            Ok(()) => {
                e.library.load_presets();
                e.library.tab = Tab::Effects;
                e.say(format!("Saved \"{name}\" \u{2014} find it under Effects \u{2192} Presets"));
            }
            Err(err) => e.say(format!("Couldn't save the preset: {err}")),
        }
    });
}

fn delete_preset(app: &mut AppState, id: &str, name: &str) {
    let Some(e) = app.editor.as_mut() else { return };
    if fig_fx::presets::EffectPresetStore::new(crate::settings::presets_dir()).delete(id) {
        e.library.load_presets();
        e.say(format!("Deleted \"{name}\""));
    }
}

fn add_to_selection(app: &mut AppState, type_id: &str) {
    let Some(e) = app.editor.as_ref() else { return };
    let owner = e.ed.selection.selected_clip_ids().into_iter().next().or_else(|| e.tl.track_selected.clone());
    match owner {
        Some(owner) => {
            add_effect(app, &owner, type_id);
        }
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

fn marker_row(e: &Editor, loc: &fig_edit::MarkerLocation, sub: String, t: &Tokens) -> El {
    let m = loc.marker.clone();
    let picked = e.ed.selection.is_marker_selected(&m.id);
    let at = loc.timeline_sec();
    let (id, menu_id) = (m.id.clone(), m.id.clone());
    let clip = match &loc.owner {
        fig_edit::MarkerOwner::Clip(c) => Some(c.clone()),
        _ => None,
    };
    let name = if m.name.trim().is_empty() { if m.is_range() { "Range".into() } else { "Marker".into() } } else { m.name.clone() };
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
        .child(col().grow(1.0).min_w(0.0).child(text(name).font(font(12.0, 600.0)).color(t.text_primary).truncate()).child(text(sub).font(font(11.0, 400.0)).color(t.text_muted).truncate()))
        .on_press_tx(move |s: &mut AppState, tx: &mut Tx| {
            let mods = tx.mods();
            if let Some(e) = s.editor.as_mut() {
                e.ed.select_marker(&id, mods.cmd, mods.shift, None);
            }
            if !mods.cmd && !mods.shift {
                crate::editor::seek(s, at, false);
            }
        })
        .on_secondary_press(move |s: &mut AppState, p: rune::PointerPress, _tx: &mut Tx| {
            let Some(e) = s.editor.as_mut() else { return };
            if !e.ed.selection.is_marker_selected(&menu_id) {
                e.ed.selection.select_marker(&menu_id);
            }
            let (jump, edit) = (menu_id.clone(), menu_id.clone());
            let mut items = vec![
                Item::new("Jump to marker", move |s, _| {
                    if let Some(e) = s.editor.as_mut() {
                        e.ed.select_marker(&jump, false, false, None);
                    }
                    crate::editor::seek(s, at, false);
                }),
                Item::new("Edit marker...", move |s, _| {
                    if let Some(e) = s.editor.as_mut() {
                        e.ed.select_marker(&edit, false, false, None);
                        e.inspector.tab = Some(fig_timeline::Facet::MARKER);
                        e.panels.right_collapsed = false;
                    }
                }),
                Item::new("Split video at selected markers", |s, _| split_at_markers(s)),
                Item::new("Delete selected markers", |s, tx| crate::commands::actions::run(s, crate::commands::Command::DeleteMarker, tx)).danger(),
            ];
            if let Some(clip) = clip.clone() {
                let c2 = clip.clone();
                items.push(Item::Separator);
                items.push(Item::new("Clear generated beats on this clip", move |s, _| {
                    if let Some(e) = s.editor.as_mut() {
                        e.ed.clear_clip_markers(&clip, true);
                    }
                }));
                items.push(Item::new("Clear all markers on this clip", move |s, _| {
                    if let Some(e) = s.editor.as_mut() {
                        e.ed.clear_clip_markers(&c2, false);
                    }
                }));
            }
            crate::ui::menu::open(s, crate::ui::menu::Popup::at(p.x, p.y, items));
        })
}

fn split_at_markers(app: &mut AppState) {
    if let Some(e) = app.editor.as_mut() {
        if e.ed.selection.selected_marker_ids().is_empty() {
            e.say("Select one or more markers first");
            return;
        }
        let n = e.ed.split_video_at_selected_markers();
        e.say(format!("{n} cut(s) made"));
    }
}

fn clock(sec: f64) -> String {
    let s = sec.max(0.0).round() as i64;
    format!("{:02}:{:02}", s / 60, s % 60)
}

fn markers_tab(e: &Editor, t: &Tokens) -> El {
    let doc = e.doc();
    let q = e.library.search.text.trim().to_lowercase();
    let hit = |name: &str, extra: &str| q.is_empty() || name.to_lowercase().contains(&q) || extra.to_lowercase().contains(&q);
    let (mut sequence, mut on_clips) = (Vec::new(), Vec::new());
    for loc in fig_edit::markers::all_markers(doc) {
        let m = &loc.marker;
        match &loc.owner {
            fig_edit::MarkerOwner::Timeline if hit(&m.name, "") => {
                let sub = if m.is_range() { format!("Range · {} → {}", clock(m.start_sec), clock(m.end_sec())) } else { format!("Point · {}", clock(m.start_sec)) };
                sequence.push((loc.timeline_sec(), marker_row(e, &loc, sub, t)));
            }
            fig_edit::MarkerOwner::Track(id) => {
                let label = crate::timeline::track_name(doc, doc.find_track(id).unwrap_or(&doc.tracks[0]));
                if hit(&m.name, &label) {
                    sequence.push((loc.timeline_sec(), marker_row(e, &loc, format!("Track {label} · {}", clock(m.start_sec)), t)));
                }
            }
            fig_edit::MarkerOwner::Clip(id) => {
                let clip = doc.find_clip(id);
                let name = clip.and_then(|c| c.media_id()).and_then(|m| e.find_media(m)).map_or("Clip".to_string(), |a| a.file_name().to_string());
                if hit(&m.name, &name) {
                    on_clips.push((loc.timeline_sec(), marker_row(e, &loc, format!("{name} · {}", clock(loc.timeline_sec())), t)));
                }
            }
            _ => {}
        }
    }
    let mut list = col().w_full().gap(4.0);
    if sequence.is_empty() && on_clips.is_empty() {
        list = list.child(
            col()
                .w_full()
                .py(24.0)
                .gap(4.0)
                .items_center()
                .child(text("No markers yet").font(font(12.5, 600.0)).color(t.text_secondary))
                .child(widgets::note("Press M to bookmark the playhead, or add a range to export a section.", t)),
        );
    }
    if !sequence.is_empty() {
        list = list.child(widgets::heading("Sequence markers", t));
        list = list.children(sequence.into_iter().map(|(_, r)| r));
    }
    if !on_clips.is_empty() {
        list = list.child(widgets::heading("Clip markers", t).pt(8.0));
        list = list.children(on_clips.into_iter().map(|(_, r)| r));
        list = list.child(widgets::note("Clip markers move with their clip.", t));
    }
    let selected = e.ed.selection.selected_marker_ids().len();
    col()
        .w_full()
        .grow(1.0)
        .min_h(0.0)
        .gap(8.0)
        .child(widgets::button("Export marked ranges", Kind::Primary, t).key("lib.export_ranges").help("Save each range marker as its own video").on_press_tx(|s: &mut AppState, tx: &mut Tx| crate::commands::actions::run(s, crate::commands::Command::ExportRanges, tx)))
        .child(search(e, "Search markers", t))
        .child(
            row()
                .gap(6.0)
                .child(widgets::button("+ Point", Kind::Normal, t).help("Add a bookmark at the playhead (M)").on_press_tx(|s: &mut AppState, tx: &mut Tx| crate::commands::actions::run(s, crate::commands::Command::AddMarker, tx)))
                .child(widgets::button("+ Range", Kind::Normal, t).help("Add a range that starts at the playhead").on_press(|s: &mut AppState| {
                    if let Some(e) = s.editor.as_mut() {
                        let at = e.playhead;
                        let n = e.doc().markers.iter().filter(|m| m.is_range()).count() + 1;
                        e.ed.add_range_marker(at, 2.0, &format!("Range {n}"), "#5f9cf8");
                    }
                })),
        )
        .child(
            row()
                .w_full()
                .gap(6.0)
                .center_y()
                .child(text(format!("{selected} selected")).font(font(11.0, 400.0)).color(t.text_muted).grow(1.0))
                .child(widgets::button("Select all", Kind::Ghost, t).h(24.0).on_press(|s: &mut AppState| {
                    if let Some(e) = s.editor.as_mut() {
                        let ids: Vec<String> = fig_edit::markers::all_markers(e.doc()).into_iter().map(|l| l.marker.id).collect();
                        e.ed.selection.select_markers(ids, None);
                    }
                }))
                .child(widgets::button("Delete selected", Kind::Ghost, t).h(24.0).on_press_tx(|s: &mut AppState, tx: &mut Tx| crate::commands::actions::run(s, crate::commands::Command::DeleteMarker, tx))),
        )
        .child(widgets::button("Split video at markers", Kind::Normal, t).help("Split video on all tracks at selected marker positions. Linked audio follows; all cuts undo together.").on_press(split_at_markers))
        .child(widgets::note("Ctrl-click to toggle · Shift-click for a range · Delete to remove", t))
        .child(scroll(list))
}

fn caption_tracks(e: &Editor) -> Vec<(String, String)> {
    e.doc()
        .tracks
        .iter()
        .filter(|t| !fig_edit::captions_on(t).is_empty())
        .map(|t| (t.id.clone(), t.name.clone().filter(|n| !n.trim().is_empty()).unwrap_or_else(|| "Captions".into())))
        .collect()
}

fn captions_panel(app: &mut AppState) -> Option<&mut CaptionsPanel> {
    Some(&mut app.editor.as_mut()?.library.captions)
}

/// Starts typing in a caption (and shows it).
fn edit_caption(app: &mut AppState, id: &str) {
    let Some(e) = app.editor.as_mut() else { return };
    let Some(clip) = e.ed.find_clip(id) else { return };
    let (at, words) = (clip.start_sec, clip.as_text().map(|t| t.text.clone()).unwrap_or_default());
    let mut edit = TextEdit::new(words);
    edit.focused = true;
    e.library.captions.editing = Some((id.to_string(), edit));
    e.ed.selection.select_only(id);
    if !app.playback.is_playing() {
        crate::editor::seek(app, at, false);
    }
}

fn captions_tab(app: &AppState, e: &Editor, t: &Tokens) -> El {
    let panel = &e.library.captions;
    let tracks = caption_tracks(e);
    let mut c = col().w_full().grow(1.0).min_h(0.0).gap(8.0).child(
        row()
            .gap(6.0)
            .child(widgets::button("Auto captions…", Kind::Primary, t).help("Write what is said on the timeline as captions").on_press(|s: &mut AppState| crate::dialogs::captions::open(s)))
            .child(widgets::button("Export…", Kind::Normal, t).help("Save the captions as SRT subtitles or a TXT transcript").on_press_tx(|s: &mut AppState, tx: &mut Tx| crate::dialogs::export_captions(s, tx))),
    );
    if tracks.is_empty() {
        return c.child(
            col()
                .w_full()
                .grow(1.0)
                .center()
                .gap(6.0)
                .child(icon(&icons::CAPTIONS, 26.0).color(t.text_muted))
                .child(text("No captions yet").font(font(12.5, 500.0)).color(t.text_muted))
                .child(widgets::note("Auto captions listens to the timeline and writes what is said.", t)),
        );
    }
    let ti = panel.track.min(tracks.len() - 1);
    if tracks.len() > 1 {
        let names = tracks.iter().map(|t| t.1.clone()).collect::<Vec<_>>();
        c = c.child(widgets::dropdown("lib.cap.track", names[ti].clone(), t).w_full().on_press(move |s: &mut AppState| {
            let items = names
                .iter()
                .enumerate()
                .map(|(i, n)| {
                    Item::new(n.clone(), move |s, _| {
                        if let Some(p) = captions_panel(s) {
                            p.track = i;
                            p.editing = None;
                        }
                    })
                })
                .collect();
            crate::ui::menu::open_below(s, "lib.cap.track", items);
        }));
    }
    // find and replace
    let find = panel.find.text.clone();
    let matches = |words: &str| {
        find.is_empty() || if panel.match_case { words.contains(&find) } else { words.to_lowercase().contains(&find.to_lowercase()) }
    };
    let track = e.ed.find_track(&tracks[ti].0);
    let captions: Vec<&fig_timeline::Clip> = track.map(fig_edit::captions_on).unwrap_or_default().into_iter().filter(|c| matches(&c.as_text().unwrap().text)).collect();
    c = c
        .child(widgets::text_box(&panel.find, "Find in captions", |s| &mut s.editor.as_mut().unwrap().library.captions.find, t).w_full())
        .children((!find.is_empty()).then(|| {
            let track_id = tracks[ti].0.clone();
            col()
                .w_full()
                .gap(6.0)
                .child(widgets::text_box(&panel.replace, "Replace with", |s| &mut s.editor.as_mut().unwrap().library.captions.replace, t).w_full())
                .child(
                    row()
                        .w_full()
                        .gap(8.0)
                        .center_y()
                        .child(widgets::checkbox(panel.match_case, "Match case", t).on_press(|s: &mut AppState| {
                            if let Some(p) = captions_panel(s) {
                                p.match_case = !p.match_case;
                            }
                        }))
                        .child(text(if captions.len() == 1 { "1 caption".to_string() } else { format!("{} captions", captions.len()) }).font(font(11.0, 400.0)).color(t.text_muted).grow(1.0))
                        .child(widgets::button("Replace all", Kind::Normal, t).h(26.0).on_press(move |s: &mut AppState| {
                            let Some(e) = s.editor.as_mut() else { return };
                            let p = &e.library.captions;
                            let (find, with, case) = (p.find.text.clone(), p.replace.text.clone(), p.match_case);
                            let n = e.ed.replace_in_captions(&find, &with, case, Some(&track_id));
                            e.library.captions.editing = None;
                            e.say(if n == 0 { format!("\u{201c}{find}\u{201d} isn't in the captions") } else { format!("Replaced {n} \u{d7} \u{201c}{find}\u{201d}") });
                        })),
                )
        }));
    let mut list = col().w_full().gap(4.0);
    for cap in &captions {
        let words = cap.as_text().unwrap().text.clone();
        let (at, end) = (cap.start_sec, cap.end_sec());
        let current = e.playhead >= at && e.playhead < end;
        let picked = e.ed.selection.is_selected(&cap.id);
        let editing = panel.editing.as_ref().filter(|(id, _)| *id == cap.id).map(|(_, edit)| edit);
        let id = cap.id.clone();
        let mut card = col()
            .w_full()
            .padding(8.0)
            .gap(4.0)
            .radius(6.0)
            .fill(if picked { hex("#222f48") } else { t.card_bg })
            .border(1.0, if current { t.accent } else { Color::TRANSPARENT })
            .child(text(format!("{} \u{2192} {}", e.timecode(at), e.timecode(end))).font(mono(10.5, 400.0)).color(t.text_muted));
        match editing {
            Some(edit) => {
                let (m, sp, d) = (id.clone(), id.clone(), id.clone());
                card = card
                    .child(
                        text_input(edit, font(12.5, 400.0))
                            .w_full()
                            .padding(6.0)
                            .radius(6.0)
                            .fill(hex("#1b1d22"))
                            .border(1.0, t.accent)
                            .color(t.text_primary)
                            .on_edit(|s: &mut AppState| &mut s.editor.as_mut().unwrap().library.captions.editing.as_mut().unwrap().1)
                            .on_input_change(|s: &mut AppState, _tx: &mut Tx| {
                                let Some(e) = s.editor.as_mut() else { return };
                                let Some((id, edit)) = e.library.captions.editing.clone() else { return };
                                e.ed.set_text(&[id], &edit.text);
                            })
                            .on_submit(|s: &mut AppState, _tx: &mut Tx| {
                                // Enter: on to the next caption
                                let Some(e) = s.editor.as_ref() else { return };
                                let Some((id, _)) = e.library.captions.editing.clone() else { return };
                                let next = e.ed.find_clip_track(&id).and_then(|tr| {
                                    let start = e.ed.find_clip(&id).map_or(0.0, |c| c.start_sec);
                                    fig_edit::captions_on(tr).into_iter().find(|c| c.start_sec > start + 1e-6).map(|c| c.id.clone())
                                });
                                match next {
                                    Some(n) => edit_caption(s, &n),
                                    None => {
                                        if let Some(p) = captions_panel(s) {
                                            p.editing = None;
                                        }
                                    }
                                }
                            }),
                    )
                    .child(
                        row()
                            .w_full()
                            .gap(4.0)
                            .child(widgets::note("Enter: next caption", t).grow(1.0))
                            .child(widgets::icon_button(&icons::MERGE, 13.0, 24.0, t).help("Merge with the next caption").on_press(move |s: &mut AppState| {
                                let Some(e) = s.editor.as_mut() else { return };
                                let joined = e.ed.merge_caption_with_next(&m);
                                match joined {
                                    Some(id) => edit_caption(s, &id),
                                    None => e.say("There is no caption after this one"),
                                }
                            }))
                            .child(widgets::icon_button(&icons::SCISSORS, 13.0, 24.0, t).help("Split at the cursor").on_press(move |s: &mut AppState| {
                                let Some(e) = s.editor.as_mut() else { return };
                                let caret = e.library.captions.editing.as_ref().map_or(0, |(_, ed)| ed.text[..ed.focus.min(ed.text.len())].chars().count());
                                match e.ed.split_caption(&sp, caret) {
                                    Some((_, right)) => edit_caption(s, &right),
                                    None => e.say("Put the cursor between two words to split the caption there."),
                                }
                            }))
                            .child(widgets::icon_button(&icons::TRASH_2, 13.0, 24.0, t).help("Delete this caption").on_press(move |s: &mut AppState| {
                                if let Some(e) = s.editor.as_mut() {
                                    e.ed.remove_caption(&d);
                                    e.library.captions.editing = None;
                                }
                            })),
                    );
            }
            None => {
                card = card.child(text(words).font(font(12.0, 400.0)).color(t.text_secondary).wrap()).hover(t.card_bg_alt).on_press(move |s: &mut AppState| edit_caption(s, &id));
            }
        }
        list = list.child(card);
    }
    c.child(
        widgets::checkbox(app.preferences.captions.style_all, "Style all captions together", t)
            .help("A change to one caption's font, colours, size or position applies to every caption on its track. What each one says stays its own.")
            .on_press(|s: &mut AppState| {
                s.preferences.captions.style_all = !s.preferences.captions.style_all;
                s.save_preferences();
            }),
    )
    .child(scroll(list))
}

thread_local! {
    static PREVIEWS: std::cell::RefCell<std::collections::HashMap<String, Option<rune::Pixels>>> = Default::default();
}

/// A card's picture: the effect on the test picture, or a transition halfway from it to its night version
/// (`EffectPreview`, `TransitionPreview`). Made once per type.
fn card_preview(type_id: &str, transition: bool) -> Option<rune::Pixels> {
    let key = format!("{}{type_id}", if transition { "t:" } else { "e:" });
    if let Some(p) = PREVIEWS.with_borrow(|m| m.get(&key).cloned()) {
        return p;
    }
    let source = crate::editor::graph::test_picture(96, 64);
    let frame = if transition {
        fig_fx::transitions::find(type_id).map(|entry| {
            let mut night = source.clone();
            for px in night.pixels.as_chunks_mut::<4>().0 {
                let (b, g, r) = (px[0], px[1], px[2]);
                (px[0], px[1], px[2]) = (r / 2 + 60, b / 3, g / 3 + 20);
            }
            entry.blend(&source, &night, 0.35, &Default::default())
        })
    } else {
        fig_fx::effects::find(type_id).map(|entry| {
            let params = entry.create_instance().params;
            entry.apply(&source, &params, 0.5).unwrap_or_else(|| source.clone())
        })
    };
    let pixels = frame.map(|f| crate::editor::graph::to_pixels(&f));
    PREVIEWS.with_borrow_mut(|m| m.insert(key, pixels.clone()));
    pixels
}
