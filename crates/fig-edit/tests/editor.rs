//! Editing behaviour, against the same cases as the C# `EditOpsTests`, `ClipFadeTests` and `MainTrackMagnetTests`.

use fig_edit::TimelineEditor;
use fig_edit::magnet::in_time_order;
use fig_timeline::fade::{self, FadeOf};
use fig_timeline::{AudioBody, Clip, FrameRate, MediaAsset, MediaKind, Timeline, TrackKind, VideoBody};

fn video(id: &str, start: f64, dur: f64) -> Clip {
    Clip::new(VideoBody { source_id: "a".into(), src_out_sec: dur, ..Default::default() }).with_id(id).at(start, dur)
}

fn one_track() -> (TimelineEditor, String) {
    let mut ed = TimelineEditor::new(Timeline { rate: FrameRate::common(30.0), ..Default::default() });
    let t = ed.add_track(TrackKind::Video);
    (ed, t)
}

fn close(a: f64, b: f64) -> bool {
    (a - b).abs() < 1e-6
}

#[test]
fn cut_splits_into_two_with_source_ranges_and_undo_restores() {
    let (mut ed, t) = one_track();
    ed.add_clip(&t, video("a", 0.0, 10.0));
    let produced = ed.cut("a", 4.0);
    assert_eq!(produced.len(), 2);
    let track = ed.find_track(&t).unwrap();
    assert_eq!((track.clips[0].dur_sec, track.clips[1].dur_sec), (4.0, 6.0));
    assert_eq!((track.clips[0].source_out(), track.clips[1].source_in()), (4.0, 4.0));
    ed.cut(&produced[1], 7.0);
    ed.undo();
    ed.undo();
    let track = ed.find_track(&t).unwrap();
    assert_eq!(track.clips.len(), 1);
    assert_eq!((track.clips[0].id.as_str(), track.clips[0].dur_sec, track.clips[0].source_out()), ("a", 10.0, 10.0));
    assert!(ed.redo());
    assert_eq!(ed.find_track(&t).unwrap().clips.len(), 2);
}

#[test]
fn a_videos_picture_and_own_sound_fade_independently_and_split_apart() {
    let (mut ed, t) = one_track();
    let mut v = video("v", 0.0, 10.0);
    v.as_video_mut().unwrap().plays_audio = true;
    ed.add_clip(&t, v);
    ed.set_fade_in("v", 2.0, FadeOf::Sound);
    ed.set_fade_out("v", 3.0, FadeOf::Picture);
    let c = ed.find_clip("v").unwrap();
    assert_eq!((c.fade_in_sec, c.fade_out_sec), (0.0, 3.0));
    assert_eq!(fade::get(c, FadeOf::Sound), (2.0, 0.0));
    assert!(close(fade::effective_opacity(c, 0.0), 1.0));
    assert!(close(fade::effective_volume(c, 0.0), 0.0));
    ed.cut("v", 5.0);
    let track = ed.find_track(&t).unwrap();
    assert_eq!(fade::get(&track.clips[0], FadeOf::Sound), (2.0, 0.0));
    assert_eq!((track.clips[1].fade_in_sec, track.clips[1].fade_out_sec), (0.0, 3.0));
    ed.undo();
    let c = ed.find_clip("v").unwrap();
    assert_eq!((fade::get(c, FadeOf::Sound).0, c.fade_in_sec, c.fade_out_sec), (2.0, 0.0, 3.0));
}

#[test]
fn fade_slider_drags_are_one_step_and_clamp() {
    let (mut ed, t) = one_track();
    let mut v = video("v", 0.0, 4.0);
    v.fade_out_sec = 1.0;
    ed.add_clip(&t, v);
    ed.set_fade_in("v", 1.0, FadeOf::Picture);
    ed.set_fade_in("v", 2.0, FadeOf::Picture);
    ed.set_fade_in("v", 5.0, FadeOf::Picture); // capped to dur - fade out = 3
    assert_eq!(ed.find_clip("v").unwrap().fade_in_sec, 3.0);
    assert!(ed.undo());
    assert_eq!(ed.find_clip("v").unwrap().fade_in_sec, 0.0);
    assert!(ed.undo()); // adding the clip
    assert!(ed.undo()); // adding the track
    assert!(!ed.undo());
}

/// An overlay track above the main track, the main track holding three 2 s clips end to end.
fn magnet_setup() -> (TimelineEditor, String) {
    let mut ed = TimelineEditor::new(Timeline { rate: FrameRate::common(30.0), ..Default::default() });
    let main = ed.add_track(TrackKind::Video);
    ed.add_track(TrackKind::Video);
    for i in 0..3 {
        ed.add_clip(&main, video(&format!("c{i}"), i as f64 * 2.0, 2.0));
    }
    (ed, main)
}

fn starts(ed: &TimelineEditor, track: &str) -> Vec<f64> {
    let t = ed.find_track(track).unwrap();
    in_time_order(t).iter().map(|id| (ed.find_clip(id).unwrap().start_sec * 1e6).round() / 1e6).collect()
}

#[test]
fn deleting_on_the_main_track_closes_the_gap_in_one_undo_step() {
    let (mut ed, main) = magnet_setup();
    ed.selection.select_only("c1");
    ed.delete_selection();
    assert_eq!(starts(&ed, &main), [0.0, 2.0]);
    ed.undo();
    assert_eq!(starts(&ed, &main), [0.0, 2.0, 4.0]);
}

#[test]
fn packing_moves_a_main_clips_linked_sound_with_it() {
    let (mut ed, _) = magnet_setup();
    let audio = ed.add_track(TrackKind::Audio);
    ed.add_clip(
        &audio,
        Clip::new(AudioBody { source_id: "a".into(), src_out_sec: 2.0, ..Default::default() })
            .with_id("s")
            .at(4.0, 2.0),
    );
    ed.link_clips(["c2", "s"]);
    ed.selection.select_only("c0");
    ed.delete_selection();
    assert!(close(ed.find_clip("c2").unwrap().start_sec, 2.0));
    assert!(close(ed.find_clip("s").unwrap().start_sec, 2.0));
}

#[test]
fn media_dropped_on_the_main_track_goes_in_at_the_nearest_cut_and_undoes() {
    let (mut ed, main) = magnet_setup();
    let asset = MediaAsset {
        id: "b".into(),
        kind: MediaKind::Video,
        duration_sec: 1.0,
        width: 1920,
        height: 1080,
        ..Default::default()
    };
    let placed = ed.place_media(&asset, Some(&main), 2.7);
    assert!(close(ed.find_clip(&placed).unwrap().start_sec, 2.0));
    assert!(close(ed.find_clip("c1").unwrap().start_sec, 3.0));
    assert!(close(ed.find_clip("c2").unwrap().start_sec, 5.0));
    ed.undo();
    assert_eq!(starts(&ed, &main), [0.0, 2.0, 4.0]);
}

#[test]
fn q_deletes_before_the_playhead_and_the_main_track_closes_up() {
    let (mut ed, main) = magnet_setup();
    let playhead = ed.trim_to_playhead(3.0, true, None);
    assert!(close(playhead.unwrap(), 2.0));
    assert_eq!(starts(&ed, &main), [0.0, 2.0, 3.0]);
    let second = &in_time_order(ed.find_track(&main).unwrap())[1];
    assert!(close(ed.find_clip(second).unwrap().source_in(), 1.0));
    ed.undo();
    assert_eq!(starts(&ed, &main), [0.0, 2.0, 4.0]);
    assert_eq!(ed.find_clip("c1").unwrap().dur_sec, 2.0);
}

#[test]
fn a_freeze_frame_splits_the_clip_and_holds_between_the_halves() {
    let (mut ed, main) = magnet_setup();
    let still =
        MediaAsset { id: "still".into(), kind: MediaKind::Image, width: 1920, height: 1080, ..Default::default() };
    let hold = ed.freeze_frame("c1", 3.0, &still).unwrap();
    let h = ed.find_clip(&hold).unwrap();
    assert!(close(h.start_sec, 3.0) && close(h.dur_sec, TimelineEditor::FREEZE_FRAME_SEC));
    assert_eq!(starts(&ed, &main), [0.0, 2.0, 3.0, 6.0, 7.0]);
    ed.undo();
    assert_eq!(starts(&ed, &main), [0.0, 2.0, 4.0]);
}

#[test]
fn copy_paste_puts_clips_back_and_selects_them() {
    let (mut ed, t) = one_track();
    ed.set_main_track_magnet(false);
    ed.add_clip(&t, video("a", 0.0, 2.0));
    ed.selection.select_only("a");
    assert!(ed.copy_selected());
    let pasted = ed.paste(5.0, None);
    assert_eq!(pasted.len(), 1);
    assert!(close(ed.find_clip(&pasted[0]).unwrap().start_sec, 5.0));
    assert!(ed.selection.is_selected(&pasted[0]));
    ed.undo();
    assert!(ed.find_clip(&pasted[0]).is_none());
    assert!(ed.selection.is_empty()); // the pasted clip went with the undo
}

#[test]
fn speed_pushes_later_clips_and_a_slider_drag_is_one_step() {
    let (mut ed, t) = one_track();
    ed.add_clip(&t, video("a", 0.0, 4.0));
    ed.add_clip(&t, video("b", 4.0, 2.0));
    ed.set_speed("a", 1.5);
    ed.set_speed("a", 2.0);
    assert!(close(ed.find_clip("a").unwrap().dur_sec, 2.0));
    assert!(close(ed.find_clip("b").unwrap().start_sec, 2.0));
    ed.undo();
    assert!(close(ed.find_clip("a").unwrap().dur_sec, 4.0));
    assert!(close(ed.find_clip("b").unwrap().start_sec, 4.0));
}

#[test]
fn pasting_onto_the_magnetic_main_track_goes_in_at_the_nearest_cut() {
    let (mut ed, main) = magnet_setup();
    ed.selection.select_only("c0");
    ed.copy_selected();
    ed.paste(4.5, None);
    assert_eq!(starts(&ed, &main), [0.0, 2.0, 4.0, 6.0]);
}
