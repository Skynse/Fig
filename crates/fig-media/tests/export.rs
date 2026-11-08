//! Exporting a real timeline: a video with sound and a title over it.

use std::path::PathBuf;
use std::sync::Arc;

use fig_media::encode::EncoderChoice;
use fig_media::export::{ExportSettings, ExportTurn, export};
use fig_render::layers::LayerRasterizer;
use fig_timeline::{Clip, FrameRate, TextBody, Timeline, Track, TrackKind, VideoBody};

fn timeline_with_clip() -> (Timeline, Vec<fig_timeline::MediaAsset>) {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../tests/assets/3 seconds timer [fxqE27gIZcc].webm");
    let mut asset = fig_media::probe::probe(&path).unwrap();
    asset.id = "m".into();
    let mut video = Track::new(TrackKind::Video);
    video.clips.push(
        Clip::new(VideoBody {
            source_id: "m".into(),
            src_in_sec: 0.0,
            src_out_sec: 1.0,
            plays_audio: true,
            ..Default::default()
        })
        .at(0.0, 1.0),
    );
    let mut titles = Track::new(TrackKind::Video);
    titles.clips.push(Clip::new(TextBody { text: "Fig".into(), ..Default::default() }).at(0.2, 0.6));
    let tl = Timeline { rate: FrameRate::new(30, 1), tracks: vec![titles, video], ..Default::default() };
    (tl, vec![asset])
}

fn probe_out(path: &std::path::Path) -> String {
    let out = std::process::Command::new("ffprobe")
        .args([
            "-v",
            "error",
            "-show_entries",
            "stream=codec_name,width,height,nb_read_frames",
            "-count_frames",
            "-of",
            "csv=p=0",
        ])
        .arg(path)
        .output()
        .unwrap();
    String::from_utf8_lossy(&out.stdout).into_owned()
}

#[test]
fn a_timeline_exports_to_h264_and_aac() {
    let (tl, media) = timeline_with_clip();
    let dir = tempfile::tempdir().unwrap();
    let out = dir.path().join("out.mp4");
    let mut last = 0.0;
    let settings = ExportSettings {
        width: 320,
        height: 180,
        quality: 23,
        encoder: EncoderChoice::Software,
        turn: ExportTurn::None,
        range: None,
    };
    let label =
        export(&tl, &media, &out, &settings, Arc::new(LayerRasterizer::new()), &mut |p| last = p, &|| false).unwrap();
    assert_eq!(label, "CPU (x264)");
    assert_eq!(last, 1.0);
    let info = probe_out(&out);
    assert!(info.contains("h264,320,180,30"), "{info}");
    assert!(info.contains("aac"), "{info}");
    assert!(fig_media::probe::is_complete_mp4(&out));
}

#[test]
fn the_automatic_encoder_falls_back_to_one_that_opens() {
    let (tl, media) = timeline_with_clip();
    let dir = tempfile::tempdir().unwrap();
    let out = dir.path().join("turned.mp4");
    let settings = ExportSettings {
        width: 320,
        height: 180,
        quality: 23,
        encoder: EncoderChoice::Automatic,
        turn: ExportTurn::Right,
        range: Some((0.0, 0.5)),
    };
    let label = export(&tl, &media, &out, &settings, Arc::new(LayerRasterizer::new()), &mut |_| {}, &|| false).unwrap();
    println!("encoder: {label}");
    let info = probe_out(&out);
    assert!(info.contains("h264,180,320,15"), "{info}");
}
