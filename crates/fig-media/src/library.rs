//! A project's media library: importing files, making what the timeline shows of them (thumbnails, filmstrips,
//! waveforms, proxies), checking them when a project opens, relinking, and collecting them into the project folder.

use std::path::{Path, PathBuf};

use fig_project::Project;
use fig_timeline::{MediaAsset, MediaKind, ProxyStatus, TrackKind, clip_time};

use crate::{MediaError, artifacts, probe};

/// Proxies are all-keyframe at the source frame rate; older ones (other names) are made again.
const PROXY_SUFFIX: &str = "_proxy_v3.mp4";

/// What checking a project on open found and did.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct ValidationReport {
    pub assets_checked: usize,
    pub artifacts_repaired: usize,
    pub offline_assets: usize,
    pub failed_artifacts: usize,
    pub notes: Vec<String>,
}

impl ValidationReport {
    pub fn had_issues(&self) -> bool {
        self.artifacts_repaired > 0 || self.offline_assets > 0 || self.failed_artifacts > 0
    }
}

/// What collecting media did.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct CollectResult {
    pub copied: usize,
    /// Files the project uses that aren't where it last saw them, so couldn't be copied.
    pub missing: Vec<String>,
}

/// Where a project keeps what it makes from its media.
pub struct Library {
    pub cache_dir: PathBuf,
}

/// Nothing is encoding when a project opens: a proxy saved mid-encode was interrupted, not in progress.
pub fn reset_pending_proxies(project: &mut Project) {
    for a in project.media.iter_mut().filter(|m| m.proxy_status == ProxyStatus::Pending) {
        a.proxy_status = ProxyStatus::None;
    }
}

fn exists(p: &str) -> bool {
    !p.is_empty() && Path::new(p).exists()
}

fn label(a: &MediaAsset) -> String {
    if a.file_name().is_empty() { a.id.clone() } else { a.file_name().to_string() }
}

impl Library {
    pub fn new(cache_dir: impl Into<PathBuf>) -> Library {
        Library { cache_dir: cache_dir.into() }
    }

    /// Reads a file into the library (or finds it there already, by its contents), with a thumbnail. Returns the
    /// asset's id.
    pub fn import(&self, project: &mut Project, path: &Path) -> Result<String, MediaError> {
        let mut asset = probe::probe(path).map_err(|e| {
            MediaError::Other(format!(
                "Cannot import '{}': {e}",
                path.file_name().map_or_else(String::new, |n| n.to_string_lossy().into_owned())
            ))
        })?;
        if let Some(existing) = project.media.iter().find(|m| m.hash == asset.hash) {
            return Ok(existing.id.clone());
        }
        if matches!(asset.kind, MediaKind::Video | MediaKind::Image) {
            let thumb = self.cache_dir.join(format!("{}.jpg", asset.hash));
            if !thumb.exists() {
                std::fs::create_dir_all(&self.cache_dir)?;
                let _ = artifacts::thumbnail(path, &thumb, 320);
            }
            asset.thumbnail = thumb.exists().then(|| thumb.to_string_lossy().into_owned());
        }
        let id = asset.id.clone();
        project.media.push(asset);
        Ok(id)
    }

    /// Makes the slow things the timeline shows of a file, each on its own so one failing doesn't stop the others:
    /// its filmstrip (made again when it is incomplete) and its waveform peaks. On the calling thread.
    pub fn finalize(&self, asset: &mut MediaAsset) {
        if !exists(&asset.url) {
            asset.offline = true;
            return;
        }
        if asset.kind == MediaKind::Video && needs_filmstrip(asset) {
            let strip = self.cache_dir.join(format!("{}_strip.jpg", asset.hash));
            let _ = std::fs::create_dir_all(&self.cache_dir);
            let _ = std::fs::remove_file(&strip);
            match artifacts::filmstrip(Path::new(&asset.url), &strip, 40) {
                Ok(info) => {
                    asset.filmstrip = Some(strip.to_string_lossy().into_owned());
                    asset.filmstrip_frame_width = info.frame_width as i32;
                    asset.filmstrip_frame_height = info.frame_height as i32;
                    asset.filmstrip_frame_count = info.frame_count as i32;
                    asset.filmstrip_frame_interval_sec = info.frame_interval_sec;
                }
                Err(_) => {
                    asset.filmstrip = None;
                    asset.filmstrip_frame_width = 0;
                    asset.filmstrip_frame_height = 0;
                    asset.filmstrip_frame_count = 0;
                    asset.filmstrip_frame_interval_sec = 0.0;
                }
            }
        }
        if asset.has_audio && asset.waveform_peaks.is_none() {
            // about 30 buckets a second is enough at any timeline zoom
            let buckets = ((asset.duration_sec * 30.0) as usize).clamp(256, 16384);
            asset.waveform_peaks = artifacts::peaks(Path::new(&asset.url), buckets).ok().map(Into::into);
        }
    }

    /// Makes (or, with `force`, remakes) a video's playback proxy, on the calling thread.
    pub fn request_proxy(&self, asset: &mut MediaAsset, force: bool, cancel: &dyn Fn() -> bool) {
        if asset.kind != MediaKind::Video {
            return;
        }
        if !exists(&asset.url) {
            asset.offline = true;
            return;
        }
        if !artifacts::should_generate_proxy(asset.width as u32, asset.height as u32, 720) {
            asset.proxy_url = None;
            asset.proxy_status = ProxyStatus::None;
            return;
        }
        let path = self.cache_dir.join(format!("{}{PROXY_SUFFIX}", asset.hash));
        let usable = |p: &Path| probe::is_complete_mp4(p);
        if !force {
            let current = asset.proxy_url.as_deref().filter(|p| p.ends_with(PROXY_SUFFIX));
            if asset.proxy_status == ProxyStatus::Ready && current.is_some_and(|p| usable(Path::new(p))) {
                return;
            }
            if usable(&path) {
                asset.proxy_url = Some(path.to_string_lossy().into_owned());
                asset.proxy_status = ProxyStatus::Ready;
                return;
            }
        }
        // a stale or partial one (a crash mid-encode) goes first
        let _ = std::fs::remove_file(&path);
        if force {
            asset.proxy_url = None;
        }
        asset.proxy_status = ProxyStatus::Pending;
        let _ = std::fs::create_dir_all(&self.cache_dir);
        match artifacts::proxy(Path::new(&asset.url), &path, 720, cancel) {
            Ok(p) if p.skipped => {
                asset.proxy_url = None;
                asset.proxy_status = ProxyStatus::None;
            }
            Ok(_) if usable(&path) => {
                asset.proxy_url = Some(path.to_string_lossy().into_owned());
                asset.proxy_status = ProxyStatus::Ready;
            }
            _ => {
                asset.proxy_url = None;
                asset.proxy_status = ProxyStatus::Failed;
                let _ = std::fs::remove_file(&path);
            }
        }
    }

    /// Points an asset at another file (and drops the proxy made from the old one).
    pub fn relink(project: &mut Project, asset_id: &str, new_path: &str) -> bool {
        let Some(a) = project.media.iter_mut().find(|m| m.id == asset_id) else { return false };
        a.url = new_path.into();
        a.offline = false;
        a.proxy_url = None;
        a.proxy_status = ProxyStatus::None;
        true
    }

    pub fn remove(project: &mut Project, asset_id: &str) -> bool {
        let before = project.media.len();
        project.media.retain(|m| m.id != asset_id);
        project.media.len() != before
    }

    /// The project card's picture: the timeline's first composited frame (the topmost visible video at the earliest
    /// time a video covers), saved and set as the project's thumbnail. False when there is nothing to show.
    pub fn update_project_thumbnail(&self, project: &mut Project, width: u32) -> bool {
        let Some(timeline) = project.timelines.first() else { return false };
        let earliest = timeline
            .tracks
            .iter()
            .filter(|t| t.kind == TrackKind::Video && t.visible)
            .flat_map(|t| &t.clips)
            .filter(|c| c.enabled && c.as_video().is_some())
            .map(|c| c.start_sec)
            .fold(f64::INFINITY, f64::min);
        if !earliest.is_finite() {
            return false;
        }
        for (_, track) in timeline.top_down() {
            let Some(top) = track
                .clips
                .iter()
                .rev()
                .find(|c| c.enabled && c.as_video().is_some() && earliest >= c.start_sec && earliest < c.end_sec())
            else {
                continue;
            };
            let Some(asset) =
                project.find_media(&top.as_video().unwrap().source_id).filter(|a| !a.url.is_empty() && !a.offline)
            else {
                continue;
            };
            let src = clip_time::source_at(top, earliest - top.start_sec);
            let thumb = self.cache_dir.join("project-thumb.jpg");
            let _ = std::fs::create_dir_all(&self.cache_dir);
            if artifacts::save_frame_as_jpeg(Path::new(&asset.url), src, &thumb, width).is_ok() {
                project.thumbnail = Some(thumb.to_string_lossy().into_owned());
                return true;
            }
            return false;
        }
        false
    }

    /// Checks every file on open: is it there, is its stream information current, is its thumbnail there (made again
    /// if not). Filmstrips and waveforms are left for the background, so opening never hangs on a stubborn file.
    pub fn validate_and_repair(&self, project: &mut Project, progress: &mut dyn FnMut(&str)) -> ValidationReport {
        let mut report = ValidationReport::default();
        let _ = std::fs::create_dir_all(&self.cache_dir);
        for asset in &mut project.media {
            report.assets_checked += 1;
            let name = label(asset);
            if !exists(&asset.url) {
                asset.offline = true;
                report.offline_assets += 1;
                report.notes.push(format!("\"{name}\": source file missing (offline)."));
                continue;
            }
            asset.offline = false;
            match probe::probe(Path::new(&asset.url)) {
                Ok(fresh) => {
                    asset.has_audio = fresh.has_audio;
                    asset.kind = fresh.kind;
                    if fresh.duration_sec > 0.0 {
                        asset.duration_sec = fresh.duration_sec;
                    }
                    if fresh.width > 0 {
                        asset.width = fresh.width;
                    }
                    if fresh.height > 0 {
                        asset.height = fresh.height;
                    }
                }
                Err(_) => {
                    report.failed_artifacts += 1;
                    report.notes.push(format!("\"{name}\": re-probe failed; treating as offline."));
                    asset.offline = true;
                    continue;
                }
            }
            if asset.thumbnail.as_deref().is_none_or(|t| !exists(t)) {
                progress(&format!("Regenerating thumbnail for \"{name}\"..."));
                let thumb = self.cache_dir.join(format!("{}.jpg", asset.hash));
                match artifacts::thumbnail(Path::new(&asset.url), &thumb, 320) {
                    Ok(()) => {
                        asset.thumbnail = Some(thumb.to_string_lossy().into_owned());
                        report.artifacts_repaired += 1;
                    }
                    Err(e) => {
                        asset.thumbnail = None;
                        report.failed_artifacts += 1;
                        report.notes.push(format!("\"{name}\": thumbnail failed ({e})."));
                    }
                }
            }
            if asset.kind == MediaKind::Video && needs_filmstrip(asset) {
                report.notes.push(format!("\"{name}\": filmstrip pending (will generate in background)."));
            }
            if asset.has_audio && asset.waveform_peaks.is_none() {
                report.notes.push(format!("\"{name}\": waveform pending (will generate in background)."));
            }
        }
        report
    }

    /// Copies every media file the project uses into a `media` folder beside the project file, and points the
    /// project at the copies, so the folder holds everything. Files already inside it, and missing ones (listed),
    /// are left alone; names that clash get a number. `progress` runs 0..1 by bytes.
    pub fn collect_sources(
        &self,
        project: &mut Project,
        project_dir: &Path,
        progress: &mut dyn FnMut(f64),
    ) -> Result<CollectResult, MediaError> {
        let mut result = CollectResult::default();
        let folder = std::fs::canonicalize(project_dir).unwrap_or_else(|_| project_dir.to_path_buf());
        let media_dir = folder.join("media");
        let mut to_copy = Vec::new();
        for (i, asset) in project.media.iter().enumerate() {
            if asset.url.is_empty() || std::fs::canonicalize(&asset.url).is_ok_and(|p| p.starts_with(&folder)) {
                continue;
            }
            if exists(&asset.url) {
                to_copy.push(i);
            } else {
                result.missing.push(asset.file_name().to_string());
            }
        }
        let total = to_copy
            .iter()
            .map(|&i| std::fs::metadata(&project.media[i].url).map_or(0, |m| m.len()))
            .sum::<u64>()
            .max(1);
        let mut done = 0;
        for i in to_copy {
            std::fs::create_dir_all(&media_dir)?;
            let asset = &mut project.media[i];
            let target = free_name(&media_dir, asset.file_name());
            let partial = PathBuf::from(format!("{}.partial", target.display()));
            let size = std::fs::copy(&asset.url, &partial)?;
            std::fs::rename(&partial, &target)?;
            asset.url = target.to_string_lossy().into_owned();
            asset.offline = false;
            done += size;
            result.copied += 1;
            progress(done as f64 / total as f64);
        }
        Ok(result)
    }
}

/// `name` in `folder`, numbered ("clip (2).mp4") if taken.
fn free_name(folder: &Path, name: &str) -> PathBuf {
    let p = Path::new(name);
    let stem = p.file_stem().map_or_else(String::new, |s| s.to_string_lossy().into_owned());
    let ext = p.extension().map_or_else(String::new, |e| format!(".{}", e.to_string_lossy()));
    let mut path = folder.join(name);
    let mut n = 2;
    while path.exists() {
        path = folder.join(format!("{stem} ({n}){ext}"));
        n += 1;
    }
    path
}

fn needs_filmstrip(a: &MediaAsset) -> bool {
    a.filmstrip.as_deref().is_none_or(|f| !exists(f))
        || a.filmstrip_frame_width <= 0
        || a.filmstrip_frame_height <= 0
        || a.filmstrip_frame_count <= 0
        || a.filmstrip_frame_interval_sec <= 0.0
}

/// True when the asset still needs a filmstrip or waveform peaks.
pub fn needs_preview_backfill(a: &MediaAsset) -> bool {
    if a.offline || a.url.is_empty() {
        return false;
    }
    (a.kind == MediaKind::Video && needs_filmstrip(a)) || (a.has_audio && a.waveform_peaks.is_none())
}
