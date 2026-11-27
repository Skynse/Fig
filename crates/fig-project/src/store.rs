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

#[cfg(test)]
mod format_tests {
    use std::fs;
    use std::path::Path;

    use crate::{Project, ProjectStore, migrations};
    use fig_timeline::{
        AudioBody, Clip, ClipBody, ClipKind, FrameRate, FrameShape, KeyframeEasing, ParamValue, TextStyle, Timeline,
        Track, TrackKind, VideoBody,
    };

    fn fixture_store() -> (tempfile::TempDir, ProjectStore) {
        let root = tempfile::tempdir().unwrap();
        let dir = root.path().join("fixture");
        fs::create_dir_all(&dir).unwrap();
        let src = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/csharp_project/project.json");
        fs::copy(src, dir.join("project.json")).unwrap();
        let store = ProjectStore::new(root.path());
        (root, store)
    }

    #[test]
    #[cfg_attr(windows, ignore = "the fixture's outside paths are Unix paths, which Windows reads as drive-relative")]
    fn a_csharp_project_opens_with_everything_in_it() {
        let (_root, store) = fixture_store();
        let p = store.load_project("fixture").unwrap().unwrap();

        assert_eq!(p.id, "fixture");
        assert_eq!(p.name, "Fixture");
        assert_eq!(p.format_version, Project::CURRENT_FORMAT_VERSION);
        let media = p.find_media("m1").unwrap();
        assert_eq!(Path::new(&media.url), store.project_directory("fixture").join("media/a.mp4"));
        assert_eq!(media.source_rate, Some(FrameRate::new(30000, 1001)));
        assert_eq!(p.find_media("m2").unwrap().url, "/elsewhere/b.png");

        let tl = &p.timelines[0];
        assert_eq!(tl.rate, FrameRate::new(30, 1));
        assert_eq!(tl.shape, FrameShape::Tall);
        assert_eq!(tl.song_bpm, Some(120.0));
        assert!(tl.main_track_magnet);
        assert_eq!(tl.markers[0].output_name.as_deref(), Some("out"));
        assert_eq!(tl.tracks.len(), 3);

        let fx = &tl.tracks[0].effects[0];
        assert_eq!(fx.params["radius"], ParamValue::Int(4));
        assert_eq!(fx.params["on"], ParamValue::Bool(true));
        assert_eq!(fx.params["tint"], ParamValue::Color(0xFF112233));
        assert_eq!(fx.params["mode"], ParamValue::Choice(2));
        assert_eq!((fx.start_sec, fx.dur_sec), (Some(0.0), Some(2.0)));

        let v = tl.find_clip("v1").unwrap();
        let ClipBody::Video(body) = &v.body else { panic!("not video") };
        assert_eq!((body.src_in_sec, body.src_out_sec, body.crop_l, body.motion_blur), (1.0, 5.0, 0.1, 0.5));
        assert!(body.plays_audio);
        assert_eq!(v.transition_out.as_ref().unwrap().params["softness"], ParamValue::Double(0.3));
        assert_eq!(v.keyframes["opacity"][1].easing, KeyframeEasing::EaseInOut);
        assert_eq!(v.markers[0].name, "hit");

        let t = tl.find_clip("t1").unwrap().as_text().unwrap();
        assert_eq!(t.style, TextStyle::captions());
        assert_eq!(t.layout.center_y, 0.8);
        assert!(t.is_caption);
        assert_eq!(t.words[1].text, "world");

        assert_eq!(tl.find_clip("i1").unwrap().kind(), ClipKind::Image);
        let a = tl.find_clip("a1").unwrap();
        assert_eq!((a.kind(), a.volume, a.link_group_id.as_deref()), (ClipKind::Audio, 0.5, Some("g")));
        assert!(tl.tracks[2].muted);

        let g = &p.effect_graphs[0];
        assert_eq!((g.nodes[0].x, g.edges[0].to_port.as_str(), g.promoted[0].targets[0].scale), (3.0, "in", 1.0));
    }

    #[test]
    #[cfg_attr(windows, ignore = "the fixture's outside paths are Unix paths, which Windows reads as drive-relative")]
    fn saving_writes_the_csharp_shape() {
        let (_root, store) = fixture_store();
        let mut p = store.load_project("fixture").unwrap().unwrap();
        store.save_project(&mut p).unwrap();

        let saved: serde_json::Value =
            serde_json::from_str(&fs::read_to_string(store.project_directory("fixture").join("project.json")).unwrap())
                .unwrap();
        let original: serde_json::Value =
            serde_json::from_str(include_str!("../tests/fixtures/csharp_project/project.json")).unwrap();

        // a clip's type is the first thing in it: System.Text.Json reads it only there
        let clip = &saved["Timelines"][0]["Tracks"][0]["Clips"][0];
        assert_eq!(clip.as_object().unwrap().keys().next().unwrap(), "kind");
        assert_eq!(clip["kind"], "video");
        // paths inside the folder stay relative
        assert_eq!(saved["Media"][0]["Url"], "media/a.mp4");

        // everything the C# build reads back is there, the same; it also wrote some read-only values it ignores
        let read_only = ["Kind", "HasCrop", "Fps", "FramesPerSecond"];
        fn same(a: &serde_json::Value, b: &serde_json::Value, path: &str, skip: &[&str]) {
            match (a, b) {
                (serde_json::Value::Object(a), serde_json::Value::Object(b)) => {
                    for (k, av) in a {
                        if skip.contains(&k.as_str()) || k == "UpdatedAt" || k == "Id" && path.is_empty() {
                            continue;
                        }
                        let bv = b.get(k).unwrap_or_else(|| panic!("{path}.{k} missing"));
                        same(av, bv, &format!("{path}.{k}"), skip);
                    }
                }
                (serde_json::Value::Array(a), serde_json::Value::Array(b)) => {
                    assert_eq!(a.len(), b.len(), "{path}");
                    for (i, (av, bv)) in a.iter().zip(b).enumerate() {
                        same(av, bv, &format!("{path}[{i}]"), skip);
                    }
                }
                (serde_json::Value::Number(a), serde_json::Value::Number(b)) => {
                    assert_eq!(a.as_f64(), b.as_f64(), "{path}")
                }
                _ => assert_eq!(a, b, "{path}"),
            }
        }
        same(&original, &saved, "", &read_only);
    }

    #[test]
    fn a_new_project_round_trips() {
        let root = tempfile::tempdir().unwrap();
        let store = ProjectStore::new(root.path());
        let id = store.create_project("Round").unwrap();
        assert!(store.cache_directory(&id).is_dir());
        let loaded = store.load_project(&id).unwrap().unwrap();
        assert_eq!(loaded.name, "Round");
        assert!(store.rename_project(&id, "  Renamed ").unwrap());
        let list = store.list_projects();
        assert_eq!((list.len(), list[0].name.as_str()), (1, "Renamed"));
        assert!(store.delete_project(&id).unwrap());
        assert!(store.load_project(&id).unwrap().is_none());
    }

    fn video(id: &str, group: &str, start: f64) -> Clip {
        let mut c = Clip::new(VideoBody { source_id: "m".into(), src_out_sec: 4.0, ..Default::default() })
            .with_id(id)
            .at(start, 4.0);
        c.link_group_id = Some(group.into());
        c
    }

    fn audio(id: &str, group: &str, start: f64) -> Clip {
        let mut c = Clip::new(AudioBody { source_id: "m".into(), src_out_sec: 4.0, ..Default::default() })
            .with_id(id)
            .at(start, 4.0);
        c.link_group_id = Some(group.into());
        c.volume = 0.5;
        c
    }

    #[test]
    fn an_older_project_folds_each_videos_linked_sound_into_the_video_clip() {
        let mut v1 = Track::new(TrackKind::Video);
        v1.clips = vec![video("together", "g1", 0.0), video("apart", "g2", 10.0)];
        let mut a1 = Track::new(TrackKind::Audio);
        a1.clips = vec![audio("together-sound", "g1", 0.0)];
        let mut a2 = Track::new(TrackKind::Audio);
        a2.clips = vec![audio("moved-sound", "g2", 11.0)];
        let (a1_id, a2_id) = (a1.id.clone(), a2.id.clone());
        let mut project = Project {
            timelines: vec![Timeline { tracks: vec![v1, a1, a2], ..Default::default() }],
            ..Default::default()
        };

        assert_eq!(migrations::embedded_audio::migrate(&mut project), 1);

        let tl = &project.timelines[0];
        let together = tl.find_clip("together").unwrap();
        let apart = tl.find_clip("apart").unwrap();
        assert!(together.as_video().unwrap().plays_audio);
        assert_eq!(together.volume, 0.5);
        assert_eq!(together.link_group_id, None);
        assert!(!apart.as_video().unwrap().plays_audio); // its sound was moved: the audio clip keeps playing it
        assert_eq!(apart.link_group_id.as_deref(), Some("g2"));
        assert!(tl.find_track(&a1_id).is_none()); // only held the merged sound
        assert!(tl.find_track(&a2_id).is_some());
        assert_eq!(project.format_version, migrations::embedded_audio::VERSION);
        assert_eq!(migrations::embedded_audio::migrate(&mut project), 0);
    }

    #[test]
    fn an_older_project_keeps_retimed_clips_playing_as_they_did() {
        let mut fast = Clip::new(VideoBody { source_id: "m".into(), src_out_sec: 8.0, ..Default::default() })
            .with_id("fast")
            .at(0.0, 4.0);
        fast.source_rate = Some(FrameRate::new(60, 1));
        let mut same = Clip::new(VideoBody { source_id: "m".into(), src_out_sec: 4.0, ..Default::default() })
            .with_id("same")
            .at(0.0, 4.0);
        same.source_rate = Some(FrameRate::new(30, 1));
        let mut track = Track::new(TrackKind::Video);
        track.clips = vec![fast, same];
        let mut project = Project {
            format_version: migrations::embedded_audio::VERSION,
            timelines: vec![Timeline { rate: FrameRate::new(30, 1), tracks: vec![track], ..Default::default() }],
            ..Default::default()
        };

        assert_eq!(migrations::real_time_media::migrate(&mut project), 1);

        let tl = &project.timelines[0];
        assert!((tl.find_clip("fast").unwrap().speed - 2.0).abs() < 1e-6); // 60fps on 30fps played twice as fast; now it says so
        assert_eq!(tl.find_clip("fast").unwrap().dur_sec, 4.0);
        assert_eq!(tl.find_clip("same").unwrap().speed, 1.0);
        assert_eq!(project.format_version, Project::CURRENT_FORMAT_VERSION);
        assert_eq!(migrations::real_time_media::migrate(&mut project), 0);
    }

    #[test]
    fn a_package_carries_the_project_and_its_media() {
        let root = tempfile::tempdir().unwrap();
        let store = ProjectStore::new(root.path().join("projects"));
        let id = store.create_project("Packed").unwrap();
        let mut p = store.load_project(&id).unwrap().unwrap();
        let outside = root.path().join("clip.mp4");
        fs::write(&outside, b"footage").unwrap();
        p.media.push(fig_timeline::MediaAsset {
            id: "m".into(),
            url: outside.to_string_lossy().into(),
            ..Default::default()
        });
        p.media.push(fig_timeline::MediaAsset {
            id: "gone".into(),
            url: "/nowhere/lost.mp4".into(),
            ..Default::default()
        });
        let pkg = root.path().join("out.figpck");
        let result = store.pack(&p, &pkg, &mut |_| {}).unwrap();
        assert_eq!((result.files, result.missing.clone()), (1, vec!["lost.mp4".to_string()]));
        let new_id = store.unpack(&pkg, &mut |_| {}).unwrap();
        let opened = store.load_project(&new_id).unwrap().unwrap();
        let m = opened.find_media("m").unwrap();
        assert!(Path::new(&m.url).ends_with("media/clip.mp4"), "{}", m.url);
        assert_eq!(fs::read(&m.url).unwrap(), b"footage");
    }
}
