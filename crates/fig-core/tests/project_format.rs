//! Projects saved by the C# build open here, and what is saved here opens there: the fixture was written by the C#
//! `ProjectStore`.

use std::fs;
use std::path::Path;

use fig_core::project::{Project, ProjectStore, migrations};
use fig_core::timeline::{
    AudioBody, Clip, ClipBody, ClipKind, FrameRate, FrameShape, KeyframeEasing, ParamValue, TextStyle, Timeline, Track,
    TrackKind, VideoBody,
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
fn saving_writes_the_csharp_shape() {
    let (_root, store) = fixture_store();
    let mut p = store.load_project("fixture").unwrap().unwrap();
    store.save_project(&mut p).unwrap();

    let saved: serde_json::Value =
        serde_json::from_str(&fs::read_to_string(store.project_directory("fixture").join("project.json")).unwrap())
            .unwrap();
    let original: serde_json::Value =
        serde_json::from_str(include_str!("fixtures/csharp_project/project.json")).unwrap();

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
    let mut project =
        Project { timelines: vec![Timeline { tracks: vec![v1, a1, a2], ..Default::default() }], ..Default::default() };

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
