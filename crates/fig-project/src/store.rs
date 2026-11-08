use std::collections::HashMap;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use chrono::{DateTime, FixedOffset};

use super::{Project, migrations, paths};
use fig_timeline::Timeline;
use fig_timeline::serde_util::{datetime, new_id, new_id_n};

#[derive(Debug, thiserror::Error)]
pub enum StoreError {
    #[error(transparent)]
    Io(#[from] io::Error),
    #[error("project file is not valid: {0}")]
    Json(#[from] serde_json::Error),
}

/// The folder of projects: one folder per project, its `project.json` and its `media.cache`.
pub struct ProjectStore {
    root: PathBuf,
}

/// What the project list shows of a project, without opening it.
#[derive(Clone, Debug, PartialEq)]
pub struct ProjectSummary {
    pub id: String,
    pub name: String,
    pub updated_at: DateTime<FixedOffset>,
    pub media_count: usize,
    pub thumbnail: Option<String>,
    pub duration_sec: f64,
    pub folder: PathBuf,
}

impl ProjectStore {
    pub fn new(root: impl Into<PathBuf>) -> ProjectStore {
        ProjectStore { root: root.into() }
    }

    pub fn root(&self) -> &Path {
        &self.root
    }

    /// A full copy of a timeline, with fresh ids for it, its tracks and its clips so nothing that caches by id mixes
    /// the two up. Clips stay linked to their copied partners.
    pub fn copy_timeline(source: &Timeline) -> Timeline {
        let mut copy = source.clone();
        copy.id = new_id();
        let mut groups: HashMap<String, String> = HashMap::new();
        for track in &mut copy.tracks {
            track.id = new_id();
            for clip in &mut track.clips {
                clip.id = new_id();
                if let Some(group) = clip.link_group_id.as_mut().filter(|g| !g.is_empty()) {
                    *group = groups.entry(group.clone()).or_insert_with(new_id_n).clone();
                }
            }
        }
        copy
    }

    /// Makes a new, empty project folder and returns its id.
    pub fn create_project(&self, name: &str) -> Result<String, StoreError> {
        let name = if name.trim().is_empty() { "Untitled" } else { name.trim() };
        let id = make_project_id(name);
        fs::create_dir_all(self.cache_directory(&id))?;
        let mut project = Project::create(name);
        project.id = id.clone();
        self.save_project(&mut project)?;
        Ok(id)
    }

    pub fn save_project(&self, project: &mut Project) -> Result<(), StoreError> {
        project.updated_at = datetime::now();
        let folder = self.project_directory(&project.id);
        fs::create_dir_all(&folder)?;
        // saved relative to the folder, so it can be zipped up and opened anywhere; in memory they stay full (the
        // JSON is rewritten, never the project, which an export or playback may be reading on another thread)
        let mut node = serde_json::to_value(&*project)?;
        paths::make_relative(&mut node, &folder);
        let text = serde_json::to_string_pretty(&node)?;
        // write beside it and swap in, so a crash mid-save never leaves half a project
        let path = self.project_path(&project.id);
        let partial = path.with_extension("json.partial");
        fs::write(&partial, text)?;
        fs::rename(partial, path)?;
        Ok(())
    }

    /// Every project in the store, most recently changed first. Folders without a readable project are skipped.
    pub fn list_projects(&self) -> Vec<ProjectSummary> {
        let Ok(entries) = fs::read_dir(&self.root) else {
            return Vec::new();
        };
        let mut summaries: Vec<ProjectSummary> = entries
            .flatten()
            .filter(|e| e.path().is_dir())
            .filter_map(|e| {
                let dir = e.path();
                let mut project = read_project(&dir.join("project.json")).ok()?;
                paths::resolve(&mut project, &dir);
                Some(ProjectSummary {
                    // the folder is the project: one unzipped under another name still opens (and saves) there
                    id: e.file_name().to_string_lossy().into_owned(),
                    name: project.name,
                    updated_at: project.updated_at,
                    media_count: project.media.len(),
                    thumbnail: project.thumbnail,
                    duration_sec: project.timelines.iter().map(Timeline::duration_sec).fold(0.0, f64::max),
                    folder: dir,
                })
            })
            .collect();
        summaries.sort_by_key(|s| std::cmp::Reverse(s.updated_at));
        summaries
    }

    /// Renames a project (the folder id is kept so media caches stay valid). False for a blank name or no project.
    pub fn rename_project(&self, id: &str, new_name: &str) -> Result<bool, StoreError> {
        let new_name = new_name.trim();
        if new_name.is_empty() {
            return Ok(false);
        }
        let Some(mut project) = self.load_project(id)? else {
            return Ok(false);
        };
        project.name = new_name.into();
        self.save_project(&mut project)?;
        Ok(true)
    }

    /// Opens a project, upgraded to the current format. None when there is no such project.
    pub fn load_project(&self, id: &str) -> Result<Option<Project>, StoreError> {
        let path = self.project_path(id);
        if !path.exists() {
            return Ok(None);
        }
        let mut project = read_project(&path)?;
        project.id = id.into();
        paths::resolve(&mut project, &self.project_directory(id));
        migrations::effects::migrate(&mut project);
        migrations::effects::move_clip_effects_to_tracks(&mut project);
        migrations::embedded_audio::migrate(&mut project);
        migrations::real_time_media::migrate(&mut project);
        Ok(Some(project))
    }

    pub fn project_directory(&self, id: &str) -> PathBuf {
        self.root.join(id)
    }

    pub fn cache_directory(&self, id: &str) -> PathBuf {
        self.project_directory(id).join("media.cache")
    }

    pub fn delete_project(&self, id: &str) -> Result<bool, StoreError> {
        let dir = self.project_directory(id);
        if !dir.is_dir() {
            return Ok(false);
        }
        fs::remove_dir_all(dir)?;
        Ok(true)
    }

    fn project_path(&self, id: &str) -> PathBuf {
        self.project_directory(id).join("project.json")
    }
}

fn read_project(path: &Path) -> Result<Project, StoreError> {
    Ok(serde_json::from_str(&fs::read_to_string(path)?)?)
}

/// Readable folder id: sanitized name + short unique suffix.
pub fn make_project_id(name: &str) -> String {
    let mut slug = sanitize_folder_name(name);
    if slug.is_empty() {
        slug = "project".into();
    }
    if slug.chars().count() > 40 {
        slug = slug.chars().take(40).collect::<String>().trim_end_matches(['-', '_']).into();
    }
    format!("{}_{}", slug, &new_id_n()[..8])
}

/// A name made safe for a folder on every system: control characters, spaces and reserved characters become `-`,
/// runs of `-` one, and no `-`, `_` or `.` at either end.
fn sanitize_folder_name(name: &str) -> String {
    const INVALID: &[char] = &['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    let mut out = String::with_capacity(name.len());
    for ch in name.trim().chars() {
        let ch = if (ch as u32) <= 32 || INVALID.contains(&ch) { '-' } else { ch };
        if !(ch == '-' && out.ends_with('-')) {
            out.push(ch);
        }
    }
    out.trim_matches(['-', '_', '.']).into()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn project_ids_are_readable() {
        let id = make_project_id("  My  reel: v2 ");
        assert!(id.starts_with("My-reel-v2_"), "{id}");
        assert_eq!(id.len(), "My-reel-v2_".len() + 8);
        assert!(make_project_id("???").starts_with("project_"));
    }
}
