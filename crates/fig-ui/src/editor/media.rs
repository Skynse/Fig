//! Media in the editor: importing files (picked or dropped), the slow previews made for them in the background
//! (filmstrip, waveform, proxy), and the decoded pictures the library and timeline draw.

use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};

use fig_media::library::Library;
use fig_timeline::{MediaAsset, MediaKind, TrackKind};
use rune::{Pixels, Tx};

use crate::app::AppState;

pub const EXTENSIONS: &[&str] = &[
    "mp4", "webm", "mov", "mkv", "avi", "m4v", "mp3", "wav", "flac", "ogg", "m4a", "aac", "wma", "png", "jpg", "jpeg",
    "bmp", "gif", "webp",
];

/// Pictures decoded for drawing, keyed by file path.
#[derive(Default)]
pub struct MediaCache {
    thumbs: HashMap<String, Pixels>,
    strips: HashMap<String, Vec<Pixels>>,
    loading: HashSet<String>,
    /// Files being imported right now (so a double drop doesn't import twice).
    pub importing: HashSet<PathBuf>,
}

impl MediaCache {
    pub fn thumbnail(&self, path: &str) -> Option<&Pixels> {
        self.thumbs.get(path)
    }

    /// A filmstrip's tiles, left to right, one every `filmstrip_frame_interval_sec` of the file.
    pub fn strip(&self, path: &str) -> Option<&[Pixels]> {
        self.strips.get(path).map(Vec::as_slice)
    }
}

fn library(app: &AppState) -> Option<Library> {
    let e = app.editor.as_ref()?;
    Some(Library::new(app.store.cache_directory(&e.id)))
}

/// After a project opens: previews for media that has none yet, then the pictures for drawing.
pub fn prepare_project(app: &mut AppState) {
    let Some(lib) = library(app) else { return };
    let Some(e) = app.editor.as_ref() else { return };
    let pending: Vec<MediaAsset> =
        e.project.media.iter().filter(|a| fig_media::library::needs_preview_backfill(a)).cloned().collect();
    for asset in pending {
        finalize(app, &lib, asset);
    }
    load_pictures(app);
}

fn finalize(app: &mut AppState, lib: &Library, asset: MediaAsset) {
    let lib = Library::new(lib.cache_dir.clone());
    let project_id = app.editor.as_ref().map(|e| e.id.clone());
    crate::app::spawn(
        move || {
            let mut asset = asset;
            lib.finalize(&mut asset);
            asset
        },
        move |app, asset| {
            let Some(e) = app.editor.as_mut() else { return };
            if Some(&e.id) != project_id.as_ref() {
                return;
            }
            let wants_proxy = asset.kind == MediaKind::Video
                && !asset.offline
                && asset.proxy_status != fig_timeline::ProxyStatus::Ready
                && fig_media::artifacts::should_generate_proxy(asset.width as u32, asset.height as u32, 720);
            e.update_media(asset.clone());
            load_pictures(app);
            if wants_proxy {
                request_proxy(app, &asset.id, false);
            }
        },
    );
}

/// Makes (or, forced, remakes) a video's playback proxy in the background.
pub fn request_proxy(app: &mut AppState, asset_id: &str, force: bool) {
    let Some(lib) = library(app) else { return };
    let Some(e) = app.editor.as_mut() else { return };
    let Some(asset) = e.project.media.iter_mut().find(|m| m.id == asset_id) else { return };
    asset.proxy_status = fig_timeline::ProxyStatus::Pending;
    let mut copy = asset.clone();
    e.media_rev += 1;
    crate::app::spawn(
        move || {
            lib.request_proxy(&mut copy, force, &|| false);
            copy
        },
        |app, asset| {
            if let Some(e) = app.editor.as_mut() {
                e.update_media(asset);
            }
            app.playback.reset_sources();
        },
    );
}

/// Loads (in the background) the thumbnails and filmstrips not decoded yet.
pub fn load_pictures(app: &mut AppState) {
    let Some(e) = app.editor.as_ref() else { return };
    let mut wanted: Vec<(String, bool, (i32, i32))> = Vec::new();
    for a in &e.project.media {
        if let Some(t) = &a.thumbnail
            && !app.media.thumbs.contains_key(t)
            && !app.media.loading.contains(t)
        {
            wanted.push((t.clone(), false, (0, 0)));
        }
        if let Some(s) = &a.filmstrip
            && !app.media.strips.contains_key(s)
            && !app.media.loading.contains(s)
            && a.filmstrip_frame_count > 0
        {
            wanted.push((s.clone(), true, (a.filmstrip_frame_width, a.filmstrip_frame_count)));
        }
        // an image is its own thumbnail
        if a.kind == MediaKind::Image && a.thumbnail.is_none() && !app.media.thumbs.contains_key(&a.url) {
            wanted.push((a.url.clone(), false, (0, 0)));
        }
    }
    for (path, strip, (tile_w, count)) in wanted {
        app.media.loading.insert(path.clone());
        crate::app::spawn(
            move || {
                if strip {
                    (path.clone(), split_strip(Path::new(&path), tile_w.max(1) as u32, count.max(1) as u32))
                } else {
                    (path.clone(), thumbnail(Path::new(&path), 256).map(|p| vec![p]))
                }
            },
            move |app, (path, pictures)| {
                app.media.loading.remove(&path);
                let Some(pictures) = pictures else { return };
                if strip {
                    app.media.strips.insert(path, pictures);
                } else if let Some(p) = pictures.into_iter().next() {
                    app.media.thumbs.insert(path, p);
                }
            },
        );
    }
}

fn thumbnail(path: &Path, max_w: u32) -> Option<Pixels> {
    let img = image::open(path).ok()?;
    let img = if img.width() > max_w { img.thumbnail(max_w, max_w * 4) } else { img };
    let rgba = img.to_rgba8();
    let pixels = Pixels::new();
    pixels.set(rgba.width(), rgba.height(), rgba.into_raw());
    Some(pixels)
}

fn split_strip(path: &Path, tile_w: u32, count: u32) -> Option<Vec<Pixels>> {
    let img = image::open(path).ok()?.to_rgba8();
    let (w, h) = img.dimensions();
    let tile_w = tile_w.min(w.max(1));
    let mut tiles = Vec::new();
    for i in 0..count {
        let x = i * tile_w;
        if x + tile_w > w {
            break;
        }
        let tile = image::imageops::crop_imm(&img, x, 0, tile_w, h).to_image();
        let p = Pixels::new();
        p.set(tile_w, h, tile.into_raw());
        tiles.push(p);
    }
    (!tiles.is_empty()).then_some(tiles)
}

/// File ▸ Import: pick a file, import it into the library.
pub fn import_dialog(tx: &mut Tx) {
    let options =
        rune::file_dialog::FileDialogOptions { title: "Import Media".into(), ..Default::default() }.filter("Media", EXTENSIONS);
    tx.open_file(options, |app: &mut AppState, result, _tx| match result {
        Ok(Some(path)) => import_files(app, vec![path], None),
        Ok(None) => {}
        Err(e) => crate::ui::toasts::show(app, format!("Couldn't open the file picker: {}", e.0)),
    });
}

/// Where dropped files go: onto the timeline at a moment and track, or just into the library.
#[derive(Clone, Debug)]
pub struct Placement {
    pub track_id: Option<String>,
    pub at_sec: f64,
}

/// Files dropped on the window (or the timeline, with a placement).
pub fn drop_files(app: &mut AppState, files: &[PathBuf], place: Option<Placement>, _tx: &mut Tx) {
    if app.editor.is_none() {
        // on Home, a dropped package or project file is opened
        for f in files {
            if f.extension().is_some_and(|x| x.eq_ignore_ascii_case("figpck")) {
                crate::home::import_package(app, f.clone());
            }
        }
        return;
    }
    let media: Vec<PathBuf> = files
        .iter()
        .filter(|f| f.extension().and_then(|x| x.to_str()).is_some_and(|x| EXTENSIONS.contains(&x.to_ascii_lowercase().as_str())))
        .cloned()
        .collect();
    if media.is_empty() {
        crate::ui::toasts::show(app, "Fig can't import that kind of file");
        return;
    }
    import_files(app, media, place);
}

/// Imports files in the background; each card appears as its probe lands, previews follow. With a placement, the
/// clips are put on the timeline one after another from there.
pub fn import_files(app: &mut AppState, files: Vec<PathBuf>, place: Option<Placement>) {
    let Some(lib) = library(app) else { return };
    let Some(e) = app.editor.as_ref() else { return };
    let known = e.project.media.clone();
    let files: Vec<PathBuf> = files.into_iter().filter(|f| app.media.importing.insert(f.clone())).collect();
    if files.is_empty() {
        return;
    }
    let project_id = e.id.clone();
    crate::app::spawn(
        move || {
            let mut results = Vec::new();
            let mut scratch = fig_project::Project { media: known, ..Default::default() };
            for f in &files {
                let r = lib.import(&mut scratch, f).map(|id| scratch.media.iter().find(|m| m.id == id).cloned());
                results.push((f.clone(), r.map_err(|e| e.to_string())));
            }
            results
        },
        move |app, results| {
            let mut placed_at = place.as_ref().map(|p| p.at_sec);
            let mut added = Vec::new();
            for (file, result) in results {
                app.media.importing.remove(&file);
                let asset = match result {
                    Ok(Some(asset)) => asset,
                    Ok(None) => continue,
                    Err(error) => {
                        crate::ui::toasts::show(app, error);
                        continue;
                    }
                };
                let Some(e) = app.editor.as_mut() else { return };
                if e.id != project_id {
                    return;
                }
                if !e.project.media.iter().any(|m| m.id == asset.id) {
                    let mut asset = asset.clone();
                    asset.in_project = Path::new(&asset.url).starts_with(&e.folder);
                    e.project.media.push(asset);
                    e.media_rev += 1;
                }
                if let (Some(p), Some(at)) = (place.as_ref(), placed_at) {
                    let id = place_media(app, &asset.id, p.track_id.as_deref(), at);
                    if let Some(end) = id.and_then(|id| app.editor.as_ref()?.ed.find_clip(&id).map(|c| c.end_sec())) {
                        placed_at = Some(end);
                    }
                }
                added.push(asset);
            }
            if let Some(lib) = library(app) {
                for asset in added.iter().cloned() {
                    finalize(app, &lib, asset);
                }
            }
            load_pictures(app);
            if added.len() == 1 && place.is_none() {
                crate::ui::toasts::show(app, format!("Imported {}", added[0].file_name()));
            } else if added.len() > 1 && place.is_none() {
                crate::ui::toasts::show(app, format!("Imported {} files", added.len()));
            }
        },
    );
}

/// Puts a library item on the timeline (`TimelineEditor::place_media`) and selects it. Returns the clip's id.
pub fn place_media(app: &mut AppState, asset_id: &str, track_id: Option<&str>, at_sec: f64) -> Option<String> {
    let e = app.editor.as_mut()?;
    let asset = e.find_media(asset_id)?.clone();
    let id = e.ed.edit(|ed| ed.place_media(&asset, track_id, at_sec));
    let group = e.ed.link_group(&id);
    e.ed.selection.set_clips(group);
    Some(id)
}

/// The library card's + (or a double-click): at the playhead, on the main track (audio on the first audio track).
pub fn add_to_timeline(app: &mut AppState, asset_id: &str) {
    let Some(e) = app.editor.as_ref() else { return };
    let Some(asset) = e.find_media(asset_id) else { return };
    let track = if asset.kind == MediaKind::Audio {
        e.doc().tracks.iter().find(|t| t.kind == TrackKind::Audio).map(|t| t.id.clone())
    } else {
        e.ed.main_track_id().map(str::to_string)
    };
    let name = asset.file_name().to_string();
    let at = e.playhead;
    if let Some(id) = place_media(app, asset_id, track.as_deref(), at) {
        let end = app.editor.as_ref().and_then(|e| e.ed.find_clip(&id)).map(|c| c.end_sec());
        if let Some(end) = end {
            crate::editor::seek(app, end, false);
        }
        crate::ui::toasts::show(app, format!("Added {name}"));
    }
}

/// Opens the folder a file is in, in the system's file manager.
pub fn reveal(path: &str) -> Result<(), String> {
    let folder = Path::new(path).parent().filter(|p| p.is_dir()).ok_or("The file's folder can't be found")?;
    let opener = if cfg!(windows) { "explorer" } else if cfg!(target_os = "macos") { "open" } else { "xdg-open" };
    std::process::Command::new(opener).arg(folder).spawn().map(|_| ()).map_err(|e| e.to_string())
}
