//! A project as one file to share: a `.figpck` is a zip of `project.json` and every file the project uses, its media
//! under `media/` wherever it came from. What Fig makes from media (previews, proxies) is left out and remade on open;
//! a reversed copy, which is footage, goes in.

use std::collections::{HashMap, HashSet};
use std::fs::File;
use std::io::{self, Read, Write};
use std::path::{Path, PathBuf};

use zip::write::SimpleFileOptions;

use super::paths::{self, full_path};
use super::{Project, ProjectStore, StoreError};
use fig_timeline::ProxyStatus;

pub const PACKAGE_EXTENSION: &str = ".figpck";

/// What packing did.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct PackResult {
    pub files: usize,
    /// Media the project uses that wasn't where it last saw it, so isn't in the package.
    pub missing: Vec<String>,
}

fn file_name(p: &str) -> String {
    Path::new(p).file_name().map_or_else(String::new, |n| n.to_string_lossy().into_owned())
}

/// `entry`, numbered ("media/clip (2).mp4") if taken.
fn free_entry(taken: &HashSet<String>, entry: &str) -> String {
    let p = Path::new(entry);
    let stem = p.file_stem().map_or_else(String::new, |s| s.to_string_lossy().into_owned());
    let ext = p.extension().map_or_else(String::new, |e| format!(".{}", e.to_string_lossy()));
    let mut name = entry.to_string();
    let mut n = 2;
    while taken.contains(&name.to_lowercase()) {
        name = format!("media/{stem} ({n}){ext}");
        n += 1;
    }
    name
}

impl ProjectStore {
    /// Writes the project and its media to `package`. Missing media is left out (named in the result). `progress`
    /// runs 0..1 by bytes.
    pub fn pack(
        &self,
        project: &Project,
        package: &Path,
        progress: &mut dyn FnMut(f64),
    ) -> Result<PackResult, StoreError> {
        let mut result = PackResult::default();
        let folder = full_path(&self.project_directory(&project.id));
        // a copy to write: its media named by where it sits in the package
        let mut copy = project.clone();
        copy.thumbnail = None;
        let mut files: Vec<(PathBuf, String)> = Vec::new();
        let mut taken: HashSet<String> = HashSet::new();
        let mut by_source: HashMap<PathBuf, String> = HashMap::new();
        for asset in &mut copy.media {
            asset.proxy_url = None;
            asset.proxy_status = ProxyStatus::None;
            asset.thumbnail = None;
            asset.filmstrip = None;
            if asset.url.is_empty() || !Path::new(&asset.url).exists() {
                if !asset.url.is_empty() {
                    result.missing.push(file_name(&asset.url));
                }
                continue;
            }
            let full = full_path(Path::new(&asset.url));
            // one entry per file, however many media items use it
            let entry = match by_source.get(&full) {
                Some(e) => e.clone(),
                None => {
                    let e = match full.strip_prefix(&folder) {
                        Ok(rel) => rel.to_string_lossy().replace('\\', "/"),
                        Err(_) => free_entry(&taken, &format!("media/{}", file_name(&asset.url))),
                    };
                    taken.insert(e.to_lowercase());
                    by_source.insert(full.clone(), e.clone());
                    files.push((full, e.clone()));
                    e
                }
            };
            asset.url = entry;
        }
        if let Some(dir) = package.parent() {
            std::fs::create_dir_all(dir)?;
        }
        let partial = PathBuf::from(format!("{}.partial", package.display()));
        let total = files.iter().map(|(f, _)| std::fs::metadata(f).map_or(0, |m| m.len())).sum::<u64>().max(1);
        let written = (|| -> Result<(), StoreError> {
            let mut zip = zip::ZipWriter::new(File::create(&partial)?);
            zip.start_file(
                "project.json",
                SimpleFileOptions::default().compression_method(zip::CompressionMethod::Deflated),
            )
            .map_err(io::Error::other)?;
            zip.write_all(serde_json::to_string_pretty(&copy)?.as_bytes())?;
            let mut done = 0u64;
            let mut buf = vec![0u8; 1 << 20];
            for (source, entry) in &files {
                // footage is compressed already: stored, not squeezed again
                zip.start_file(
                    entry,
                    SimpleFileOptions::default().compression_method(zip::CompressionMethod::Stored).large_file(true),
                )
                .map_err(io::Error::other)?;
                let mut from = File::open(source)?;
                loop {
                    let n = from.read(&mut buf)?;
                    if n == 0 {
                        break;
                    }
                    zip.write_all(&buf[..n])?;
                    done += n as u64;
                    progress(done as f64 / total as f64);
                }
                result.files += 1;
            }
            zip.finish().map_err(io::Error::other)?;
            Ok(())
        })();
        match written {
            Ok(()) => {
                std::fs::rename(&partial, package)?;
                Ok(result)
            }
            Err(e) => {
                let _ = std::fs::remove_file(&partial);
                Err(e)
            }
        }
    }

    /// Unpacks a package into a new project (the package is left as it is) and returns its id, ready to load.
    pub fn unpack(&self, package: &Path, progress: &mut dyn FnMut(f64)) -> Result<String, StoreError> {
        let mut zip =
            zip::ZipArchive::new(File::open(package)?).map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e))?;
        let mut project: Project = {
            let manifest = zip.by_name("project.json").map_err(|_| {
                io::Error::new(io::ErrorKind::InvalidData, "Not a Fig project package: it has no project.json")
            })?;
            serde_json::from_reader(manifest)?
        };
        let name = if project.name.trim().is_empty() {
            package.file_stem().map_or_else(String::new, |s| s.to_string_lossy().into_owned())
        } else {
            project.name.clone()
        };
        let id = self.create_project(&name)?;
        let folder = full_path(&self.project_directory(&id));
        let total: u64 = (0..zip.len()).filter_map(|i| zip.by_index(i).ok().map(|e| e.size())).sum::<u64>().max(1);
        let mut done = 0u64;
        for i in 0..zip.len() {
            let mut entry = zip.by_index(i).map_err(io::Error::other)?;
            if entry.is_dir() || entry.name() == "project.json" {
                continue;
            }
            // nothing may land outside the project's folder ("../" in a name)
            let Some(rel) = entry.enclosed_name() else { continue };
            let target = full_path(&folder.join(rel));
            if !target.starts_with(&folder) {
                continue;
            }
            if let Some(dir) = target.parent() {
                std::fs::create_dir_all(dir)?;
            }
            io::copy(&mut entry, &mut File::create(&target)?)?;
            done += entry.size();
            progress(done as f64 / total as f64);
        }
        project.id = id.clone();
        paths::resolve(&mut project, &self.project_directory(&id));
        self.save_project(&mut project)?;
        Ok(id)
    }
}

/// Writes a project to a file of the user's choosing (Save As), whole: written beside it and swapped in.
pub fn save_project_file(project: &mut Project, path: &Path) -> Result<(), StoreError> {
    project.updated_at = fig_timeline::serde_util::datetime::now();
    let tmp = path.with_extension("tmp");
    std::fs::write(&tmp, serde_json::to_string_pretty(project)?)?;
    std::fs::rename(tmp, path)?;
    Ok(())
}

/// Reads a project file written by [`save_project_file`].
pub fn load_project_file(path: &Path) -> Result<Project, StoreError> {
    Ok(serde_json::from_str(&std::fs::read_to_string(path)?)?)
}
