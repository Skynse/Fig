//! A project's file paths as saved: those inside its own folder relative to it, so the folder can be zipped, moved
//! or opened on another machine and still find its media; those outside it as they are.

use std::path::{Component, Path, PathBuf};

use serde_json::Value;

use super::Project;

/// `Path.GetFullPath`: absolute, with `.` and `..` worked out (without touching the disk).
pub(crate) fn full_path(path: &Path) -> PathBuf {
    let absolute =
        if path.is_absolute() { path.to_path_buf() } else { std::env::current_dir().unwrap_or_default().join(path) };
    let mut out = PathBuf::new();
    for part in absolute.components() {
        match part {
            Component::ParentDir => {
                out.pop();
            }
            Component::CurDir => {}
            other => out.push(other),
        }
    }
    out
}

fn relative(folder: &Path, path: &str) -> String {
    let p = Path::new(path);
    if path.is_empty() || !p.is_absolute() {
        return path.into();
    }
    match full_path(p).strip_prefix(full_path(folder)) {
        Ok(rel) if !rel.as_os_str().is_empty() => rel.to_string_lossy().replace('\\', "/"),
        _ => path.into(),
    }
}

/// In a project already turned into JSON, paths inside `folder` become relative to it (forward slashes, as on every
/// system). The JSON, not the project: rewriting the project in use made an export or playback that opened a file at
/// that moment get a relative path and fail.
pub(crate) fn make_relative(project: &mut Value, folder: &Path) {
    fn fix(owner: &mut Value, key: &str, folder: &Path) {
        if let Some(Value::String(path)) = owner.get_mut(key) {
            *path = relative(folder, path);
        }
    }
    fix(project, "Thumbnail", folder);
    if let Some(Value::Array(media)) = project.get_mut("Media") {
        for asset in media {
            for key in ["Url", "ProxyUrl", "Thumbnail", "Filmstrip"] {
                fix(asset, key, folder);
            }
        }
    }
}

/// Relative paths (as saved) become full paths inside `folder`.
pub(crate) fn resolve(project: &mut Project, folder: &Path) {
    let map = |path: &mut String| {
        if !path.is_empty() && !Path::new(path.as_str()).is_absolute() {
            let joined = folder.join(path.replace('/', std::path::MAIN_SEPARATOR_STR));
            *path = full_path(&joined).to_string_lossy().into_owned();
        }
    };
    if let Some(t) = project.thumbnail.as_mut() {
        map(t);
    }
    for asset in &mut project.media {
        map(&mut asset.url);
        for p in [&mut asset.proxy_url, &mut asset.thumbnail, &mut asset.filmstrip].into_iter().flatten() {
            map(p);
        }
    }
}
