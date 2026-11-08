//! The speed-curve maths, against the same cases as the C# `SpeedCurveTests`.

use fig_timeline::{Clip, KeyframeEasing, KeyframePoint, ParamValue, VideoBody, automation_keys, clip_time, split};

fn key(t: f64, speed: f64, easing: KeyframeEasing) -> KeyframePoint {
    KeyframePoint::eased(t, ParamValue::Double(speed), easing)
}

fn ramped(easing: KeyframeEasing) -> Clip {
    let mut clip = Clip::new(VideoBody { src_in_sec: 10.0, src_out_sec: 100.0, ..Default::default() }).at(0.0, 6.0);
    clip.keyframes.insert(
        automation_keys::SPEED.into(),
        vec![key(1.0, 0.5, easing), key(3.0, 4.0, easing), key(5.0, 1.0, KeyframeEasing::Linear)],
    );
    clip
}

#[test]
fn source_at_is_the_area_under_the_speed_curve_and_local_at_inverts_it() {
    use KeyframeEasing::*;
    for easing in [Linear, EaseIn, EaseOut, EaseInOut, Hold] {
        let clip = ramped(easing);
        let (step, mut area) = (1e-4, 0.0);
        let mut t = 0.0;
        while t < 6.0 - step / 2.0 {
            area += clip_time::speed_at(&clip, t + step / 2.0) * step;
            let next = t + step;
            if (next - next.round()).abs() < step / 2.0 {
                let whole = next.round();
                let at = clip_time::source_at(&clip, whole);
                assert!((10.0 + area - at).abs() < 5e-4, "{easing:?} at {whole}: {} vs {at}", 10.0 + area);
                assert!((clip_time::local_at(&clip, at) - whole).abs() < 5e-5, "{easing:?} inverse at {whole}");
            }
            t = next;
        }
    }
}

#[test]
fn a_cut_through_a_ramp_plays_both_halves_as_the_whole_did() {
    let clip = ramped(KeyframeEasing::Linear);
    let times = [0.5, 1.7, 2.4, 3.9, 5.5];
    let expected: Vec<f64> = times.iter().map(|&t| clip_time::source_at(&clip, t)).collect();

    let (left, right) = (split::before(&clip, 2.4), split::after(&clip, 2.4));

    assert!((left.source_out() - right.source_in()).abs() < 1e-9);
    for (i, &t) in times.iter().enumerate() {
        let actual = if t < 2.4 { clip_time::source_at(&left, t) } else { clip_time::source_at(&right, t - 2.4) };
        assert!((expected[i] - actual).abs() < 1e-6, "at {t}: {} vs {actual}", expected[i]);
    }
    assert_ne!(left.id, clip.id);
    assert_eq!((right.start_sec, right.dur_sec), (2.4, 6.0 - 2.4));
}
