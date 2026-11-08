//! Decoding real files: the clips in the repository's `tests/` folder.

use std::path::PathBuf;

use fig_media::{AudioSource, DecodeMode, VideoSource};

fn asset(name: &str) -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../tests").join(name)
}

fn mean(f: &fig_fx::Frame) -> f64 {
    f.pixels.iter().map(|&b| b as f64).sum::<f64>() / f.pixels.len() as f64
}

#[test]
fn video_decodes_forward_seeks_back_and_holds_the_last_frame() {
    let path = asset("segments/segment_0.mp4");
    let mut v = VideoSource::open(&path, 320, 320).unwrap();
    let a = v.decode_forward(0.5, DecodeMode::Playback).unwrap();
    assert_eq!((a.width, a.height), (320, 320));
    assert!((v.last_presented_sec() - 0.5).abs() < 0.05, "{}", v.last_presented_sec());
    let b = v.decode_forward(1.0, DecodeMode::Playback).unwrap();
    assert!(mean(&b) > 1.0);
    v.seek(0.2);
    v.decode_forward(0.2, DecodeMode::Playback).unwrap();
    assert!((v.last_presented_sec() - 0.2).abs() < 0.05);
    // asking past the end holds the final frame
    assert!(v.decode_forward(10_000.0, DecodeMode::Playback).is_some());
    // a scrub shows a keyframe at once
    v.seek(0.0);
    assert!(v.decode_forward(0.9, DecodeMode::Scrub).is_some());
}

#[test]
fn a_webm_with_rounded_frame_times_decodes_every_frame_once() {
    let path = asset("assets/3 seconds timer [fxqE27gIZcc].webm");
    let mut v = VideoSource::open(&path, 160, 90).unwrap();
    let mut times = Vec::new();
    for i in 0..30 {
        v.decode_forward(i as f64 / 30.0, DecodeMode::Playback).unwrap();
        times.push(v.last_presented_sec());
    }
    times.dedup();
    assert!(times.len() >= 28, "{times:?}");
}

#[test]
fn audio_reads_contiguous_chunks_at_full_length() {
    let path = asset("assets/3 seconds timer [fxqE27gIZcc].webm");
    let mut a = AudioSource::open(&path, 48_000).unwrap();
    let first = a.read(0.0, 0.5);
    assert_eq!(first.len(), 48_000);
    assert!((a.next_sec() - 0.5).abs() < 1e-3);
    let second = a.read(0.5, 0.5);
    assert_eq!(second.len(), 48_000);
    assert!(first.iter().chain(&second).any(|s| s.abs() > 1e-4), "the sound is silent");
    let past = a.read(1_000.0, 0.1);
    assert!(past.iter().all(|s| *s == 0.0));
}

#[test]
fn importing_makes_a_thumbnail_and_finalizing_a_filmstrip_and_peaks() {
    let dir = tempfile::tempdir().unwrap();
    let lib = fig_media::library::Library::new(dir.path());
    let mut project = fig_project::Project::default();
    let path = asset("assets/3 seconds timer [fxqE27gIZcc].webm");
    let id = lib.import(&mut project, &path).unwrap();
    assert_eq!(lib.import(&mut project, &path).unwrap(), id); // the same file is found again
    let a = project.media.iter_mut().find(|m| m.id == id).unwrap();
    assert!(a.thumbnail.as_deref().is_some_and(|t| std::path::Path::new(t).exists()));
    lib.finalize(a);
    assert!(a.filmstrip_frame_count >= 8 && a.filmstrip.is_some());
    assert!(a.waveform_peaks.as_ref().is_some_and(|p| p.iter().any(|v| *v > 0.01)));
}
