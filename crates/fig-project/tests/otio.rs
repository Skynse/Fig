//! Importing the OTIO test corpus, against the same cases as the C# `OtioImportTests`.

use fig_project::otio::{OtioImport, import_file};
use fig_timeline::Clip;

fn import(name: &str) -> OtioImport {
    import_file(&std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join(format!("tests/fixtures/otio/{name}.otio")))
        .unwrap()
}

fn close(a: f64, b: f64) -> bool {
    (a - b).abs() < 1e-6
}

fn clips(r: &OtioImport, track: usize) -> &[Clip] {
    &r.project.timelines[0].tracks[track].clips
}

#[test]
fn simple_cut_source_ranges_map_to_in_and_out() {
    let r = import("simple_cut");
    let c = clips(&r, 0);
    assert!(close(c[0].source_in(), 3.0 / 24.0) && close(c[0].source_out(), 6.0 / 24.0));
    assert!(close(c[1].source_in(), 2.0 / 24.0) && close(c[1].source_out(), 8.0 / 24.0));
}

#[test]
fn transition_test_maps_dissolves_to_clip_edges() {
    let r = import("transition_test");
    let c = clips(&r, 0);
    assert_eq!(c.len(), 3);
    assert!(c[0].transition_in.is_some());
    let a_out = c[0].transition_out.as_ref().unwrap();
    assert_eq!(a_out.type_id, "cross-dissolve");
    assert!(close(a_out.duration_sec, 20.0 / 24.0));
    assert!(close(c[1].transition_in.as_ref().unwrap().duration_sec, a_out.duration_sec));
    assert!(c[1].transition_out.is_none() && c[2].transition_in.is_none());
    assert!(c[2].transition_out.is_some());
    assert_eq!(r.transitions_imported, 4);
}

#[test]
fn multiple_track_gaps_shift_following_starts() {
    let r = import("multiple_track");
    let v1 = clips(&r, 0);
    assert!(close(v1[0].start_sec, 0.0) && close(v1[1].start_sec, 3.0 / 24.0) && close(v1[2].start_sec, 13.0 / 24.0));
    assert!(close(clips(&r, 1)[0].start_sec, 7.0 / 24.0));
    assert!(close(clips(&r, 2)[0].start_sec, 0.0));
    assert_eq!(r.gaps_skipped, 2);
}

#[test]
fn nested_and_screening_examples_flatten_and_keep_missing_references_empty() {
    let r = import("nested_example");
    assert_eq!((r.clips_imported, r.project.timelines[0].tracks.len(), clips(&r, 0).len()), (7, 1, 7));
    assert!(clips(&r, 0).iter().all(|c| c.media_id() == Some("")));
    let s = import("screening_example");
    assert_eq!(s.clips_imported, 9);
    assert!(s.project.media.is_empty());
}
