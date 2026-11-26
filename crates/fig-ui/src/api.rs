//! The window's side of `fig_api`: every call runs on the UI thread through the app's handle, doing what the
//! matching menu item, inspector control or drag does. Extensions call it from their own threads.

use std::path::PathBuf;
use std::sync::mpsc;
use std::time::{Duration, Instant};

use fig_api::{
    CatalogEntry, ClipInfo, CommandInfo, EffectInfo, Error, ExportOptions, FigApi, Image, JobInfo, MarkerInfo,
    MediaInfo, ParamInfo, ProjectInfo, Result, Status, TimelineInfo, TrackChange, TrackInfo, TransitionInfo, Value,
};
use fig_timeline::{Clip, ClipBody, EffectInstance, Marker, ParamDef, ParamKind, ParamValue, TrackKind, TransitionRef};
use rune::Tx;

use crate::app::{AppState, Route};
use crate::commands::Command;
use crate::editor::Editor;

/// How long a call waits for the UI thread to take it.
const ANSWER: Duration = Duration::from_secs(20);

pub struct LiveApi;

/// Runs `f` on the UI thread and waits for its answer.
fn on_ui<T: Send + 'static>(f: impl FnOnce(&mut AppState, &mut Tx) -> Result<T> + Send + 'static) -> Result<T> {
    let handle = crate::app::handle().ok_or("Fig's window isn't up yet")?;
    let (send, answer) = mpsc::channel();
    handle.update(move |app, tx| {
        let _ = send.send(f(app, tx));
    });
    answer.recv_timeout(ANSWER).map_err(|_| Error::from("the editor didn't answer"))?
}

/// [`on_ui`] with the open project's editor.
fn with_editor<T: Send + 'static>(f: impl FnOnce(&mut Editor) -> Result<T> + Send + 'static) -> Result<T> {
    on_ui(move |app, _| f(editor(app)?))
}

fn editor(app: &mut AppState) -> Result<&mut Editor> {
    app.editor.as_mut().ok_or_else(|| Error::from("no project is open"))
}

/// Asks `check` on the UI thread every 100 ms until it answers, or gives up after `limit`.
fn wait_for<T: Send + 'static>(
    limit: Duration,
    what: &str,
    check: impl Fn(&mut AppState) -> Option<Result<T>> + Send + Sync + Clone + 'static,
) -> Result<T> {
    let until = Instant::now() + limit;
    loop {
        let check = check.clone();
        if let Some(done) = on_ui(move |app, _| Ok(check(app)))? {
            return done;
        }
        if Instant::now() >= until {
            return Err(format!("timed out waiting for {what}").into());
        }
        std::thread::sleep(Duration::from_millis(100));
    }
}

fn clip<'a>(e: &'a Editor, id: &str) -> Result<&'a Clip> {
    e.ed.find_clip(id).ok_or_else(|| format!("no clip {id}").into())
}

fn track_exists(e: &Editor, id: Option<&str>) -> Result<()> {
    match id {
        Some(id) if e.ed.find_track(id).is_none() => Err(format!("no track {id}").into()),
        _ => Ok(()),
    }
}

fn clips_exist(e: &Editor, ids: &[String]) -> Result<()> {
    if ids.is_empty() {
        return Err("no clips given".into());
    }
    ids.iter().try_for_each(|id| clip(e, id).map(|_| ()))
}

fn lower(d: impl std::fmt::Debug) -> String {
    format!("{d:?}").to_lowercase()
}

// ---- values

fn param_kind(kind: ParamKind) -> &'static str {
    match kind {
        ParamKind::Double => "number",
        ParamKind::Int => "integer",
        ParamKind::Bool => "bool",
        ParamKind::Color => "color",
        ParamKind::List => "choice",
    }
}

/// A value as JSON: colours as "#AARRGGBB", choices by name.
fn value_json(def: Option<&ParamDef>, v: ParamValue) -> serde_json::Value {
    match v {
        ParamValue::Double(n) => serde_json::json!(n),
        ParamValue::Int(n) => serde_json::json!(n),
        ParamValue::Bool(b) => serde_json::json!(b),
        ParamValue::Color(c) => serde_json::json!(format!("#{c:08X}")),
        ParamValue::Choice(i) => match def.and_then(|d| d.choices.get(i.max(0) as usize)) {
            Some(name) => serde_json::json!(name),
            None => serde_json::json!(i),
        },
    }
}

fn parse_color(s: &str) -> Option<u32> {
    let hex = s.trim().trim_start_matches('#');
    let n = u32::from_str_radix(hex, 16).ok()?;
    match hex.len() {
        6 => Some(0xFF00_0000 | n),
        8 => Some(n),
        _ => None,
    }
}

/// A caller's value as the parameter's type, within its range.
fn to_param(def: &ParamDef, v: &Value) -> Result<ParamValue> {
    let bad = || Error(format!("{} takes a {}", def.key, param_kind(def.kind)));
    let clamp = |n: f64| if def.min < def.max { n.clamp(def.min, def.max) } else { n };
    Ok(match (def.kind, v) {
        (ParamKind::Double, Value::Number(n)) => ParamValue::Double(clamp(*n)),
        (ParamKind::Int, Value::Number(n)) => ParamValue::Int(clamp(n.round()) as i32),
        (ParamKind::Bool, Value::Bool(b)) => ParamValue::Bool(*b),
        (ParamKind::Bool, Value::Number(n)) => ParamValue::Bool(*n != 0.0),
        (ParamKind::Color, Value::Number(n)) => ParamValue::Color(*n as u32),
        (ParamKind::Color, Value::Text(s)) => ParamValue::Color(parse_color(s).ok_or_else(bad)?),
        (ParamKind::List, Value::Number(n)) if (*n as usize) < def.choices.len().max(1) => {
            ParamValue::Choice(*n as i32)
        }
        (ParamKind::List, Value::Text(s)) => ParamValue::Choice(
            def.choices
                .iter()
                .position(|c| c.eq_ignore_ascii_case(s.trim()))
                .ok_or_else(|| Error(format!("{} is one of: {}", def.key, def.choices.join(", "))))? as i32,
        ),
        _ => return Err(bad()),
    })
}

fn param_info(def: &ParamDef, keyable: bool, value: Option<serde_json::Value>, group: String) -> ParamInfo {
    ParamInfo {
        key: def.key.clone(),
        label: def.label.clone(),
        kind: param_kind(def.kind).into(),
        min: def.min,
        max: def.max,
        default: def.default,
        choices: def.choices.clone(),
        keyable,
        value,
        group,
    }
}

// ---- reading

fn marker_info(m: &Marker) -> MarkerInfo {
    MarkerInfo {
        id: m.id.clone(),
        name: m.name.clone(),
        time: m.start_sec,
        duration: m.dur_sec,
        color: m.color.clone(),
    }
}

fn effect_info(fx: &EffectInstance) -> EffectInfo {
    let defs = fig_fx::effects::find(&fx.type_id).map(|e| e.params.clone()).unwrap_or_default();
    let params =
        fx.params.iter().map(|(k, v)| (k.clone(), value_json(defs.iter().find(|d| &d.key == k), *v))).collect();
    EffectInfo {
        id: fx.id.clone(),
        type_id: fx.type_id.clone(),
        enabled: fx.enabled,
        start: fx.start_sec,
        duration: fx.dur_sec,
        params,
    }
}

fn transition_info(t: &Option<TransitionRef>) -> Option<TransitionInfo> {
    t.as_ref().map(|t| TransitionInfo { type_id: t.type_id.clone(), duration: t.duration_sec })
}

fn clip_info(e: &Editor, c: &Clip) -> ClipInfo {
    let name = match &c.body {
        ClipBody::Text(t) => t.text.clone(),
        _ => c.media_id().and_then(|id| e.find_media(id)).map_or(String::new(), |m| m.file_name().to_string()),
    };
    let ranged = c.has_source_range();
    ClipInfo {
        id: c.id.clone(),
        kind: lower(c.kind()),
        name,
        media_id: c.media_id().map(str::to_string),
        start: c.start_sec,
        duration: c.dur_sec,
        source_in: ranged.then(|| c.source_in()),
        source_out: ranged.then(|| c.source_out()),
        speed: c.speed,
        enabled: c.enabled,
        linked_to: c.link_group_id.clone(),
        effects: c.effects.iter().map(effect_info).collect(),
        markers: c.markers.iter().map(marker_info).collect(),
        transition_in: transition_info(&c.transition_in),
        transition_out: transition_info(&c.transition_out),
    }
}

fn timeline_info(e: &Editor) -> TimelineInfo {
    let doc = e.doc();
    TimelineInfo {
        name: doc.name.clone(),
        fps: e.fps(),
        shape: doc.shape.label().to_string(),
        duration: e.duration(),
        tracks: doc
            .tracks
            .iter()
            .map(|t| TrackInfo {
                id: t.id.clone(),
                kind: lower(t.kind),
                label: e.ed.track_label(&t.id),
                visible: t.visible,
                muted: t.muted,
                clips: t.clips.iter().map(|c| clip_info(e, c)).collect(),
                effects: t.effects.iter().map(effect_info).collect(),
                markers: t.markers.iter().map(marker_info).collect(),
            })
            .collect(),
        markers: doc.markers.iter().map(marker_info).collect(),
    }
}

fn project_info(p: &fig_project::Project) -> ProjectInfo {
    ProjectInfo { id: p.id.clone(), name: p.name.clone(), updated: p.updated_at.to_rfc3339() }
}

/// The commands an outside caller may run: not the ones that open a dialog or a file picker, or quit.
fn runnable(c: Command) -> bool {
    use Command::*;
    !matches!(
        c,
        SaveAs
            | SavePackage
            | CloseProject
            | Preferences
            | Exit
            | Import
            | Export
            | ExportRanges
            | ExportCaptions
            | AutoCaptions
            | Shortcuts
    )
}

fn command_info(c: Command) -> CommandInfo {
    let name = format!("{c:?}");
    match crate::commands::entry(c) {
        Some(e) => CommandInfo { name, label: e.label.into(), section: e.section.into(), shortcut: e.default.into() },
        None => CommandInfo { label: name.clone(), name, section: String::new(), shortcut: String::new() },
    }
}

// ---- output

/// Renders the frame at `t` on this thread, its longer side at most `max_side`.
fn render_png(
    doc: &fig_timeline::Timeline,
    media: &[fig_timeline::MediaAsset],
    t: f64,
    max_side: u32,
) -> Result<Image> {
    let (fw, fh) = crate::playback::render::frame_size(doc, media);
    let scale = (f64::from(max_side.max(16)) / f64::from(fw.max(fh))).min(1.0);
    let w = ((f64::from(fw) * scale).round() as usize).max(2) & !1;
    let h = ((f64::from(fh) * scale).round() as usize).max(2) & !1;
    let layers = std::sync::Arc::new(fig_render::layers::LayerRasterizer::new());
    let mut sources = fig_media::MediaSources::new(media.to_vec(), layers);
    sources.mode = fig_media::DecodeMode::Playback;
    let scene = fig_render::build_scene(doc, media, t, w, h, &mut sources);
    let mut frame = fig_render::render(&scene);
    for px in frame.pixels.chunks_exact_mut(4) {
        px.swap(0, 2);
    }
    let (width, height) = (frame.width as u32, frame.height as u32);
    let image = image::RgbaImage::from_raw(width, height, frame.pixels).ok_or("the frame came out the wrong size")?;
    let mut png = Vec::new();
    image
        .write_to(&mut std::io::Cursor::new(&mut png), image::ImageFormat::Png)
        .map_err(|e| Error(format!("couldn't encode the frame: {e}")))?;
    Ok(Image { width, height, png })
}

impl FigApi for LiveApi {
    fn status(&self) -> Result<Status> {
        on_ui(|app, _| {
            let opening = app.loading.as_ref().map(|l| l.name.clone());
            let playing = app.playback.is_playing();
            let screen = if app.route == Route::Editor { "editor" } else { "home" }.to_string();
            Ok(match app.editor.as_ref() {
                Some(e) => Status {
                    screen,
                    opening,
                    project: Some(project_info(&e.project)),
                    playhead: e.playhead,
                    playing,
                    duration: e.duration(),
                    fps: e.fps(),
                    frame: crate::playback::render::frame_size(e.doc(), e.media()),
                    unsaved: e.is_dirty(),
                    can_undo: e.ed.can_undo(),
                    can_redo: e.ed.can_redo(),
                    selection: e.ed.selection.selected_clip_ids(),
                },
                None => Status {
                    screen,
                    opening,
                    project: None,
                    playhead: 0.0,
                    playing,
                    duration: 0.0,
                    fps: 0.0,
                    frame: (0, 0),
                    unsaved: false,
                    can_undo: false,
                    can_redo: false,
                    selection: Vec::new(),
                },
            })
        })
    }

    fn list_projects(&self) -> Result<Vec<ProjectInfo>> {
        on_ui(|app, _| {
            Ok(app
                .store
                .list_projects()
                .into_iter()
                .map(|p| ProjectInfo { id: p.id, name: p.name, updated: p.updated_at.to_rfc3339() })
                .collect())
        })
    }

    fn open_project(&self, id: &str) -> Result<()> {
        let want = id.to_string();
        let started = want.clone();
        let already = on_ui(move |app, tx| {
            if app.editor.as_ref().is_some_and(|e| e.id == started) {
                return Ok(true);
            }
            if app.loading.is_some() {
                return Err("another project is opening".into());
            }
            if !app.store.list_projects().iter().any(|p| p.id == started) {
                return Err(format!("no project {started}").into());
            }
            app.open_project(&started, tx);
            Ok(false)
        })?;
        if already {
            return Ok(());
        }
        // checking its files and remaking previews can take a while
        wait_for(Duration::from_secs(300), "the project to open", move |app| {
            if app.loading.is_some() {
                return None;
            }
            Some(match app.editor.as_ref() {
                Some(e) if e.id == want => Ok(()),
                _ => Err(Error::from("the project didn't open")),
            })
        })
    }

    fn create_project(&self, name: &str) -> Result<String> {
        let name = name.to_string();
        let id = on_ui(move |app, _| {
            let name = if name.trim().is_empty() { "Untitled".to_string() } else { name.trim().to_string() };
            app.store.create_project(&name).map_err(|e| Error(format!("couldn't make the project: {e}")))
        })?;
        self.open_project(&id)?;
        Ok(id)
    }

    fn save(&self) -> Result<()> {
        on_ui(|app, _| {
            editor(app)?;
            crate::editor::save_now(app);
            Ok(())
        })?;
        wait_for(Duration::from_secs(60), "the save", |app| {
            let e = app.editor.as_ref()?;
            if e.save.saving {
                return None;
            }
            Some(if e.save.failed { Err(Error::from("the save failed")) } else { Ok(()) })
        })
    }

    fn close_project(&self) -> Result<()> {
        on_ui(|app, tx| {
            editor(app)?;
            app.close_project_saving(tx);
            Ok(())
        })
    }

    fn timeline(&self) -> Result<TimelineInfo> {
        with_editor(|e| Ok(timeline_info(e)))
    }

    fn media(&self) -> Result<Vec<MediaInfo>> {
        with_editor(|e| {
            Ok(e.project
                .media
                .iter()
                .map(|m| MediaInfo {
                    id: m.id.clone(),
                    kind: lower(m.kind),
                    name: m.file_name().to_string(),
                    path: m.url.clone(),
                    duration: m.duration_sec,
                    width: m.width,
                    height: m.height,
                })
                .collect())
        })
    }

    fn clip_properties(&self, clip_id: &str) -> Result<Vec<ParamInfo>> {
        let id = clip_id.to_string();
        with_editor(move |e| {
            let c = clip(e, &id)?;
            let local = (e.playhead - c.start_sec).clamp(0.0, c.dur_sec);
            Ok(fig_timeline::properties::all()
                .iter()
                .filter(|p| p.has(c) && p.shown.is_none_or(|shown| shown(c)))
                .map(|p| {
                    let value = value_json(Some(&p.def), p.value_at(c, local));
                    param_info(&p.def, p.keyable, Some(value), p.group.to_string())
                })
                .collect())
        })
    }

    fn effect_catalog(&self) -> Result<Vec<CatalogEntry>> {
        Ok(fig_fx::effects::all()
            .iter()
            .map(|e| CatalogEntry {
                type_id: e.type_id.clone(),
                name: e.display_name.clone(),
                category: e.category.clone(),
                description: e.description.clone(),
                params: e.params.iter().map(|d| param_info(d, true, None, String::new())).collect(),
                default_duration: None,
            })
            .collect())
    }

    fn transition_catalog(&self) -> Result<Vec<CatalogEntry>> {
        Ok(fig_fx::transitions::all()
            .iter()
            .map(|t| CatalogEntry {
                type_id: t.type_id.into(),
                name: t.display_name.into(),
                category: t.category.into(),
                description: t.description.into(),
                params: t.params.iter().map(|d| param_info(d, false, None, String::new())).collect(),
                default_duration: Some(t.default_duration_sec),
            })
            .collect())
    }

    fn commands(&self) -> Result<Vec<CommandInfo>> {
        Ok(crate::commands::ALL.iter().copied().filter(|c| runnable(*c)).map(command_info).collect())
    }

    fn text_presets(&self) -> Result<Vec<String>> {
        Ok(fig_timeline::text_presets().iter().map(|p| p.name.to_string()).collect())
    }

    fn import_media(&self, paths: Vec<PathBuf>) -> Result<Vec<String>> {
        let mut files = Vec::new();
        for p in paths {
            files.push(p.canonicalize().map_err(|e| Error(format!("{}: {e}", p.display())))?);
        }
        let asked = files.clone();
        let known = on_ui(move |app, _| {
            let known: Vec<String> = editor(app)?.project.media.iter().map(|m| m.id.clone()).collect();
            crate::editor::media::import_files(app, asked, None);
            Ok(known)
        })?;
        let pending = files.clone();
        wait_for(Duration::from_secs(600), "the import", move |app| {
            (!pending.iter().any(|f| app.media.importing.contains(f))).then_some(Ok(()))
        })?;
        with_editor(move |e| {
            // new media, and for a file already in the project, the media it already was
            let mut ids: Vec<String> =
                e.project.media.iter().filter(|m| !known.contains(&m.id)).map(|m| m.id.clone()).collect();
            for f in &files {
                let name = f.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
                if let Some(m) = e.project.media.iter().find(|m| m.file_name() == name && !ids.contains(&m.id)) {
                    ids.push(m.id.clone());
                }
            }
            if ids.is_empty() {
                return Err("nothing was imported (see the window for why)".into());
            }
            Ok(ids)
        })
    }

    fn add_clip(&self, media_id: &str, track: Option<&str>, start: f64) -> Result<String> {
        let (media_id, track) = (media_id.to_string(), track.map(str::to_string));
        on_ui(move |app, _| {
            let e = editor(app)?;
            track_exists(e, track.as_deref())?;
            e.find_media(&media_id).ok_or_else(|| Error(format!("no media {media_id}")))?;
            crate::editor::media::place_media(app, &media_id, track.as_deref(), start.max(0.0))
                .ok_or_else(|| "the clip couldn't be placed".into())
        })
    }

    fn add_text(
        &self,
        text: &str,
        preset: Option<&str>,
        track: Option<&str>,
        start: f64,
        duration: Option<f64>,
    ) -> Result<String> {
        let (text, preset, track) = (text.to_string(), preset.map(str::to_string), track.map(str::to_string));
        with_editor(move |e| {
            track_exists(e, track.as_deref())?;
            let presets = fig_timeline::text_presets();
            let p = match preset.as_deref() {
                Some(name) => presets.iter().find(|p| p.name.eq_ignore_ascii_case(name)).ok_or_else(|| {
                    Error(format!(
                        "text presets are: {}",
                        presets.iter().map(|p| p.name).collect::<Vec<_>>().join(", ")
                    ))
                })?,
                None => presets.first().ok_or("there are no text presets")?,
            };
            let id = e.ed.edit(|ed| {
                let id = ed.add_text_preset(track.as_deref(), start.max(0.0), p);
                if !text.is_empty() && text != p.text {
                    ed.set_text(std::slice::from_ref(&id), &text);
                }
                if let Some(d) = duration.filter(|d| *d > 0.0) {
                    ed.trim(&id, 0.0, d);
                }
                id
            });
            e.ed.selection.select_only(&id);
            Ok(id)
        })
    }

    fn move_clip(&self, clip_id: &str, start: f64, track: Option<&str>) -> Result<()> {
        let (id, track) = (clip_id.to_string(), track.map(str::to_string));
        with_editor(move |e| {
            clip(e, &id)?;
            track_exists(e, track.as_deref())?;
            let start = start.max(0.0);
            let target = track.filter(|t| e.ed.find_clip_track_id(&id).as_deref() != Some(t.as_str()));
            match target {
                // onto another track: the clip alone, live, as a drag does
                Some(target) => {
                    let starts = indexmap::IndexMap::from([(id.clone(), start)]);
                    e.ed.begin_live();
                    if !e.ed.move_clips_to_track_live(std::slice::from_ref(&id), &target, &starts) {
                        e.ed.cancel_live();
                        return Err("there's no room for it there".into());
                    }
                    e.ed.commit_live();
                    Ok(())
                }
                // along its track, with what's linked to it: only where they all have room
                None => {
                    let delta = start - clip(e, &id)?.start_sec;
                    let starts: indexmap::IndexMap<String, f64> =
                        e.ed.link_group(&id)
                            .into_iter()
                            .filter_map(|g| e.ed.find_clip(&g).map(|c| (g, (c.start_sec + delta).max(0.0))))
                            .collect();
                    if e.ed.would_overlap_move(&starts) {
                        return Err("it would overlap another clip".into());
                    }
                    e.ed.move_clip(&id, start);
                    Ok(())
                }
            }
        })
    }

    fn trim_clip(&self, clip_id: &str, start: Option<f64>, end: Option<f64>) -> Result<()> {
        let id = clip_id.to_string();
        with_editor(move |e| {
            let c = clip(e, &id)?.clone();
            let (new_start, new_end) = (start.unwrap_or(c.start_sec).max(0.0), end.unwrap_or(c.end_sec()));
            if new_end - new_start < 1e-3 {
                return Err("the clip would have no length".into());
            }
            let track = e.ed.find_clip_track_id(&id).unwrap_or_default();
            if e.ed.would_overlap(&track, new_start, new_end - new_start, Some(&id)) {
                return Err("it would overlap another clip".into());
            }
            let (new_in, new_out) = if c.has_source_range() {
                let r = c.speed.max(1e-6);
                let source_in = c.source_in() + (new_start - c.start_sec) * r;
                let source_out = c.source_out() + (new_end - c.end_sec()) * r;
                let limit = c.media_id().and_then(|m| e.find_media(m)).map_or(f64::MAX, |m| m.duration_sec.max(0.0));
                if source_in < -1e-6 || (limit > 0.0 && source_out > limit + 1e-3) {
                    return Err("the source doesn't reach that far".into());
                }
                (source_in.max(0.0), source_out)
            } else {
                (0.0, new_end - new_start)
            };
            e.ed.edit(|ed| {
                ed.trim(&id, new_in, new_out);
                ed.set_clip_value(std::slice::from_ref(&id), "start", move |c| c.start_sec = new_start);
            });
            Ok(())
        })
    }

    fn split_clip(&self, clip_id: &str, at: f64) -> Result<Vec<String>> {
        let id = clip_id.to_string();
        with_editor(move |e| {
            clip(e, &id)?;
            let parts = e.ed.cut(&id, at);
            if parts.is_empty() {
                return Err("that time isn't inside the clip".into());
            }
            Ok(parts)
        })
    }

    fn delete_clips(&self, clip_ids: &[String], ripple: bool) -> Result<()> {
        let ids = clip_ids.to_vec();
        with_editor(move |e| {
            clips_exist(e, &ids)?;
            e.ed.edit(|ed| {
                for id in &ids {
                    if ripple {
                        ed.ripple_delete(id);
                    } else {
                        ed.lift(id);
                    }
                }
            });
            Ok(())
        })
    }

    fn set_clip_enabled(&self, clip_ids: &[String], enabled: bool) -> Result<()> {
        let ids = clip_ids.to_vec();
        with_editor(move |e| {
            clips_exist(e, &ids)?;
            e.ed.set_clip_value(&ids, "enabled", move |c| c.enabled = enabled);
            Ok(())
        })
    }

    fn set_property(&self, clip_ids: &[String], key: &str, value: Value) -> Result<()> {
        let (ids, key) = (clip_ids.to_vec(), key.to_string());
        with_editor(move |e| {
            clips_exist(e, &ids)?;
            let p = fig_timeline::properties::find(&key).ok_or_else(|| Error(format!("no property {key}")))?;
            if let Some(id) = ids.iter().find(|id| e.ed.find_clip(id).is_some_and(|c| !p.has(c))) {
                return Err(format!("clip {id} has no {key}").into());
            }
            let v = to_param(&p.def, &value)?;
            let playhead = e.playhead;
            e.ed.set_clip_property(&ids, p, v, playhead);
            Ok(())
        })
    }

    fn set_keyframe(&self, clip_id: &str, key: &str, time: f64, value: f64) -> Result<()> {
        let (id, key) = (clip_id.to_string(), key.to_string());
        with_editor(move |e| {
            let c = clip(e, &id)?;
            let p = fig_timeline::properties::find(&key).ok_or_else(|| Error(format!("no property {key}")))?;
            if !p.keyable || !p.has(c) {
                return Err(format!("{key} can't be animated on this clip").into());
            }
            let local = (time - c.start_sec).clamp(0.0, c.dur_sec);
            let value = if p.def.min < p.def.max { value.clamp(p.def.min, p.def.max) } else { value };
            e.ed.set_clip_keyframe(&id, &key, local, value);
            Ok(())
        })
    }

    fn remove_keyframes(&self, clip_id: &str, key: &str, time: Option<f64>) -> Result<()> {
        let (id, key) = (clip_id.to_string(), key.to_string());
        with_editor(move |e| {
            let start = clip(e, &id)?.start_sec;
            match time {
                Some(t) => e.ed.remove_clip_keyframe(&id, &key, t - start),
                None => e.ed.clear_clip_keyframes(&id, Some(&key)),
            }
            Ok(())
        })
    }

    fn set_text(&self, clip_ids: &[String], text: &str) -> Result<()> {
        let (ids, text) = (clip_ids.to_vec(), text.to_string());
        with_editor(move |e| {
            clips_exist(e, &ids)?;
            if let Some(id) = ids.iter().find(|id| e.ed.find_clip(id).is_some_and(|c| c.as_text().is_none())) {
                return Err(format!("clip {id} isn't a text clip").into());
            }
            e.ed.set_text(&ids, &text);
            Ok(())
        })
    }

    fn select(&self, clip_ids: &[String]) -> Result<()> {
        let ids = clip_ids.to_vec();
        with_editor(move |e| {
            if ids.is_empty() {
                e.ed.selection.clear();
                return Ok(());
            }
            clips_exist(e, &ids)?;
            e.ed.selection.set_clips(ids);
            Ok(())
        })
    }

    fn add_track(&self, kind: fig_api::TrackKind) -> Result<String> {
        with_editor(move |e| {
            Ok(e.ed.add_track(match kind {
                fig_api::TrackKind::Video => TrackKind::Video,
                fig_api::TrackKind::Audio => TrackKind::Audio,
            }))
        })
    }

    fn remove_track(&self, track_id: &str) -> Result<()> {
        let id = track_id.to_string();
        with_editor(move |e| {
            track_exists(e, Some(&id))?;
            e.ed.remove_track(&id);
            Ok(())
        })
    }

    fn change_track(&self, track_id: &str, change: TrackChange) -> Result<()> {
        let id = track_id.to_string();
        with_editor(move |e| {
            track_exists(e, Some(&id))?;
            e.ed.edit(|ed| {
                if let Some(name) = &change.name {
                    ed.rename_track(&id, Some(name.trim()).filter(|n| !n.is_empty()));
                }
                if let Some(v) = change.visible {
                    ed.set_track_visible(&id, v);
                }
                if let Some(m) = change.muted {
                    ed.set_track_muted(&id, m);
                }
                if let Some(to) = change.index
                    && let Some(from) = ed.doc().tracks.iter().position(|t| t.id == id)
                {
                    // move_track takes where it goes in before; `to` is where it ends up
                    ed.move_track(&id, if to > from { to + 1 } else { to });
                }
            });
            Ok(())
        })
    }

    fn add_marker(
        &self,
        owner: Option<&str>,
        time: f64,
        duration: f64,
        name: &str,
        color: Option<&str>,
    ) -> Result<String> {
        let (owner, name) = (owner.map(str::to_string), name.to_string());
        let color = color.unwrap_or("#eab308").to_string();
        with_editor(move |e| {
            let name = if name.is_empty() { "Marker".to_string() } else { name };
            let marker = match owner.as_deref() {
                Some(id) if e.ed.find_clip(id).is_some() => e.ed.add_clip_marker(id, time, &name, &color),
                Some(id) if e.ed.find_track(id).is_some() => e.ed.add_track_marker(id, time, &name, &color),
                Some(id) => return Err(format!("no clip or track {id}").into()),
                None if duration > 0.0 => Some(e.ed.add_range_marker(time, duration, &name, &color)),
                None => Some(e.ed.add_timeline_marker(time, &name, &color)),
            };
            marker.map(|m| m.id).ok_or_else(|| "the marker is outside the clip".into())
        })
    }

    fn delete_markers(&self, marker_ids: &[String]) -> Result<()> {
        let ids = marker_ids.to_vec();
        with_editor(move |e| {
            if let Some(id) = ids.iter().find(|id| e.ed.find_marker(id).is_none()) {
                return Err(format!("no marker {id}").into());
            }
            e.ed.delete_markers(&ids);
            Ok(())
        })
    }

    fn add_effect(&self, owner: &str, type_id: &str, range: Option<(f64, f64)>) -> Result<String> {
        let (owner, type_id) = (owner.to_string(), type_id.to_string());
        on_ui(move |app, _| {
            let e = editor(app)?;
            if e.ed.find_clip(&owner).is_none() && e.ed.find_track(&owner).is_none() {
                return Err(format!("no clip or track {owner}").into());
            }
            let mut fx =
                fig_fx::effects::find(&type_id).ok_or_else(|| Error(format!("no effect {type_id}")))?.create_instance();
            // no range: the whole clip or track
            if let Some((start, dur)) = range.map(|r| owner_range(e, &owner, r)) {
                (fx.start_sec, fx.dur_sec) = (Some(start), Some(dur));
            }
            crate::editor::library::add_effect_instance(app, &owner, fx)
                .ok_or_else(|| "the effect couldn't be added".into())
        })
    }

    fn set_effect_range(
        &self,
        owner: &str,
        effect_id: &str,
        range: Option<(f64, f64)>,
        move_keyframes: bool,
    ) -> Result<()> {
        let (owner, fx) = (owner.to_string(), effect_id.to_string());
        with_editor(move |e| {
            e.ed.find_effect(&owner, &fx).ok_or_else(|| Error(format!("no effect {fx} on {owner}")))?;
            let range = range.map(|r| owner_range(e, &owner, r));
            e.ed.set_effect_range(&owner, &fx, range, move_keyframes);
            Ok(())
        })
    }

    fn remove_effect(&self, owner: &str, effect_id: &str) -> Result<()> {
        let (owner, fx) = (owner.to_string(), effect_id.to_string());
        with_editor(move |e| {
            e.ed.find_effect(&owner, &fx).ok_or_else(|| Error(format!("no effect {fx} on {owner}")))?;
            e.ed.remove_effect(&owner, &fx);
            Ok(())
        })
    }

    fn set_effect_param(&self, owner: &str, effect_id: &str, key: &str, value: Value) -> Result<()> {
        let (owner, fx, key) = (owner.to_string(), effect_id.to_string(), key.to_string());
        with_editor(move |e| {
            let inst = e.ed.find_effect(&owner, &fx).ok_or_else(|| Error(format!("no effect {fx} on {owner}")))?;
            let entry = fig_fx::effects::find(&inst.type_id).ok_or("that effect's type is gone")?;
            let def = entry.params.iter().find(|d| d.key == key).ok_or_else(|| {
                Error(format!(
                    "its settings are: {}",
                    entry.params.iter().map(|d| d.key.as_str()).collect::<Vec<_>>().join(", ")
                ))
            })?;
            let v = to_param(def, &value)?;
            e.ed.set_effect_param(&owner, &fx, &key, v);
            Ok(())
        })
    }

    fn set_effect_keyframe(
        &self,
        owner: &str,
        effect_id: &str,
        time: f64,
        values: &[(String, f64)],
        easing: Option<&str>,
    ) -> Result<()> {
        use fig_timeline::KeyframeEasing as E;
        let easing = match easing.map(lower).as_deref() {
            None => None,
            Some("linear") => Some(E::Linear),
            Some("ease-in") => Some(E::EaseIn),
            Some("ease-out") => Some(E::EaseOut),
            Some("ease-in-out") => Some(E::EaseInOut),
            Some("hold") => Some(E::Hold),
            Some(other) => {
                return Err(format!("no easing {other}: linear, ease-in, ease-out, ease-in-out or hold").into());
            }
        };
        let (owner, fx, values) = (owner.to_string(), effect_id.to_string(), values.to_vec());
        with_editor(move |e| {
            let inst = e.ed.find_effect(&owner, &fx).ok_or_else(|| Error(format!("no effect {fx} on {owner}")))?;
            let entry = fig_fx::effects::find(&inst.type_id).ok_or("that effect's type is gone")?;
            let mut keyed = indexmap::IndexMap::new();
            for (key, value) in values {
                let def = entry.params.iter().find(|d| d.key == key).ok_or_else(|| {
                    let keys: Vec<_> = entry.params.iter().map(|d| d.key.as_str()).collect();
                    Error(format!("{key} isn't a setting of it; its settings are: {}", keys.join(", ")))
                })?;
                let value = if def.min < def.max { value.clamp(def.min, def.max) } else { value };
                keyed.insert(key, value);
            }
            // a clip's effect keys count from the clip's start; a track's are timeline times
            let local = match e.ed.find_clip(&owner) {
                Some(c) => (time - c.start_sec).clamp(0.0, c.dur_sec),
                None => time.max(0.0),
            };
            e.ed.set_effect_keyframe(&owner, &fx, local, &keyed, easing.or(Some(E::EaseInOut)));
            e.ed.break_coalescing();
            Ok(())
        })
    }

    fn add_transition(&self, left: &str, right: Option<&str>, type_id: &str, duration: Option<f64>) -> Result<()> {
        let (left, right, type_id) = (left.to_string(), right.map(str::to_string), type_id.to_string());
        with_editor(move |e| {
            let entry = fig_fx::transitions::find(&type_id).ok_or_else(|| Error(format!("no transition {type_id}")))?;
            let r = TransitionRef {
                type_id: type_id.clone(),
                duration_sec: duration.unwrap_or(entry.default_duration_sec),
                params: Default::default(),
            };
            let ok = match &right {
                Some(right) => e.ed.apply_transition_at_cut(&left, right, &r),
                None => e.ed.apply_transition_after(&left, &r),
            };
            if ok {
                Ok(())
            } else {
                let why = e.ed.transition_validation_message.clone();
                Err(why.unwrap_or_else(|| "a transition goes on a cut between two touching clips".into()).into())
            }
        })
    }

    fn remove_transition(&self, left: &str, right: &str) -> Result<()> {
        let (left, right) = (left.to_string(), right.to_string());
        with_editor(move |e| {
            e.ed.remove_transition(&left, &right);
            Ok(())
        })
    }

    fn undo(&self) -> Result<bool> {
        self.history(Command::Undo)
    }

    fn redo(&self) -> Result<bool> {
        self.history(Command::Redo)
    }

    fn seek(&self, time: f64) -> Result<()> {
        on_ui(move |app, _| {
            editor(app)?;
            crate::editor::seek(app, time, false);
            Ok(())
        })
    }

    fn play(&self) -> Result<()> {
        on_ui(|app, _| {
            editor(app)?;
            if !app.playback.is_playing() {
                crate::editor::play(app, 1.0);
            }
            Ok(())
        })
    }

    fn pause(&self) -> Result<()> {
        on_ui(|app, _| {
            editor(app)?;
            if app.playback.is_playing() {
                crate::editor::pause(app);
            }
            Ok(())
        })
    }

    fn run_command(&self, name: &str) -> Result<()> {
        let name = name.trim().to_string();
        on_ui(move |app, tx| {
            let command = crate::commands::ALL
                .iter()
                .copied()
                .filter(|c| runnable(*c))
                .find(|c| {
                    format!("{c:?}").eq_ignore_ascii_case(&name)
                        || crate::commands::entry(*c).is_some_and(|e| e.id == name)
                })
                .ok_or_else(|| Error(format!("no command {name} (see the command list)")))?;
            editor(app)?;
            crate::commands::actions::run(app, command, tx);
            Ok(())
        })
    }

    fn render_frame(&self, time: Option<f64>, max_side: u32) -> Result<Image> {
        let (doc, media, t) =
            with_editor(move |e| Ok((e.doc().clone(), e.project.media.clone(), time.unwrap_or(e.playhead))))?;
        render_png(&doc, &media, t.max(0.0), max_side)
    }

    fn export(&self, path: PathBuf, options: ExportOptions) -> Result<()> {
        if path.parent().is_some_and(|d| !d.as_os_str().is_empty() && !d.is_dir()) {
            return Err(format!("{} isn't a folder", path.parent().unwrap().display()).into());
        }
        on_ui(move |app, _| {
            let e = editor(app)?;
            if e.duration() <= 0.0 {
                return Err("the timeline is empty".into());
            }
            let e_duration = e.duration();
            let (width, height) = crate::dialogs::export::size_for(app, options.short_side.unwrap_or(0));
            let settings = fig_media::export::ExportSettings {
                width,
                height,
                quality: options.crf.unwrap_or(app.preferences.export.quality.clamp(0, 51) as u32).min(51),
                encoder: crate::dialogs::export::preferred_encoder(app),
                turn: fig_media::export::ExportTurn::None,
                range: options.range.map(|(a, b)| (a.max(0.0), b.min(e_duration))),
            };
            let name = path.file_name().map_or("export".into(), |n| n.to_string_lossy().into_owned());
            crate::dialogs::export::start_jobs(app, vec![(name, path, settings)]);
            Ok(())
        })
    }

    fn jobs(&self) -> Result<Vec<JobInfo>> {
        on_ui(|app, _| {
            Ok(app
                .jobs
                .list
                .iter()
                .map(|j| JobInfo {
                    name: j.name.clone(),
                    path: j.path.to_string_lossy().into_owned(),
                    progress: f64::from_bits(j.progress.load(std::sync::atomic::Ordering::Relaxed)),
                    done: j.done,
                    error: j.error.clone(),
                })
                .collect())
        })
    }

    fn cancel_export(&self, path: Option<&str>) -> Result<usize> {
        let path = path.map(std::path::PathBuf::from);
        on_ui(move |app, _| {
            let mut n = 0;
            for job in app.jobs.list.iter().filter(|j| !j.done && path.as_ref().is_none_or(|p| &j.path == p)) {
                job.cancel.store(true, std::sync::atomic::Ordering::Relaxed);
                n += 1;
            }
            Ok(n)
        })
    }
}

/// A timeline range in an effect owner's time: from a clip's start on a clip, as it is on a track.
fn owner_range(e: &Editor, owner: &str, (start, dur): (f64, f64)) -> (f64, f64) {
    match e.ed.find_clip(owner) {
        Some(c) => (start - c.start_sec, dur),
        None => (start, dur),
    }
}

impl LiveApi {
    /// Undo or redo as the menu does; whether anything changed.
    fn history(&self, command: Command) -> Result<bool> {
        on_ui(move |app, tx| {
            let before = editor(app)?.revision();
            crate::commands::actions::run(app, command, tx);
            Ok(editor(app)?.revision() != before)
        })
    }
}
