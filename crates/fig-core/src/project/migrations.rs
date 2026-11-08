//! Upgrades for projects saved by earlier versions, run when a project loads.

/// Effects saved by earlier versions, brought to their current form.
pub mod effects {
    use crate::project::Project;
    use crate::timeline::{
        EffectInstance, KeyframeEasing, KeyframeMap, KeyframePoint, ParamMap, ParamValue, TrackKind,
    };

    /// The short-lived "Zoom region" effect, now a Transform with two keyframes.
    pub const LEGACY_ZOOM_REGION: &str = "zoomRegion";

    /// The Transform effect's type id.
    pub const TRANSFORM: &str = "transform";

    /// Migrates every clip in the project. Returns how many effects were changed.
    pub fn migrate(project: &mut Project) -> usize {
        let mut changed = 0;
        for clip in project.timelines.iter_mut().flat_map(|t| t.clips_mut()) {
            let dur = clip.dur_sec;
            for effect in clip.effects.iter_mut().filter(|e| e.type_id == LEGACY_ZOOM_REGION) {
                convert_zoom_region(dur, effect);
                changed += 1;
            }
        }
        changed
    }

    /// Moves legacy video clip stacks onto their tracks without changing their timeline positions.
    pub fn move_clip_effects_to_tracks(project: &mut Project) -> usize {
        let mut moved = 0;
        for track in
            project.timelines.iter_mut().flat_map(|t| t.tracks.iter_mut()).filter(|t| t.kind == TrackKind::Video)
        {
            for clip in &mut track.clips {
                for effect in clip.effects.drain(..) {
                    let mut copy = effect;
                    copy.start_sec = Some(clip.start_sec + copy.start_sec.unwrap_or(0.0));
                    copy.dur_sec = Some(copy.dur_sec.unwrap_or(clip.dur_sec));
                    for points in copy.keyframes.values_mut() {
                        for p in points {
                            p.time_sec += clip.start_sec;
                        }
                    }
                    copy.order = track.effects.iter().map(|fx| fx.order).max().unwrap_or(-1) + 1;
                    track.effects.push(copy);
                    moved += 1;
                }
            }
        }
        moved
    }

    fn convert_zoom_region(clip_dur: f64, fx: &mut EffectInstance) {
        let num = |key: &str, fallback: f64| fx.params.get(key).map_or(fallback, |v| v.as_number());
        let easing = match fx.params.get("ease") {
            Some(e) => match e.as_choice() {
                1 => KeyframeEasing::EaseIn,
                2 => KeyframeEasing::EaseOut,
                0 => KeyframeEasing::Linear,
                _ => KeyframeEasing::EaseInOut,
            },
            None => KeyframeEasing::EaseInOut,
        };
        let start = fx.start_sec.unwrap_or(0.0);
        let dur = fx.dur_sec.unwrap_or(clip_dur);
        let from = (num("startX", 0.5), num("startY", 0.5), num("startZoom", 1.0));
        let to = (num("endX", 0.75), num("endY", 0.75), num("endZoom", 2.5));

        let track = |a: f64, b: f64| {
            vec![
                KeyframePoint::eased(start, ParamValue::Double(a), easing),
                KeyframePoint::eased(start + dur, ParamValue::Double(b), KeyframeEasing::EaseInOut),
            ]
        };
        fx.type_id = TRANSFORM.into();
        fx.start_sec = None;
        fx.dur_sec = None;
        fx.params = ParamMap::from_iter([
            ("centerX".to_string(), ParamValue::Double(from.0)),
            ("centerY".to_string(), ParamValue::Double(from.1)),
            ("zoom".to_string(), ParamValue::Double(from.2)),
            ("rotation".to_string(), ParamValue::Double(0.0)),
        ]);
        fx.keyframes = KeyframeMap::from_iter([
            ("centerX".to_string(), track(from.0, to.0)),
            ("centerY".to_string(), track(from.1, to.1)),
            ("zoom".to_string(), track(from.2, to.2)),
            ("rotation".to_string(), track(0.0, 0.0)),
        ]);
    }
}

/// Upgrades a project saved when a video's sound was a separate audio clip linked to it. A video clip and its
/// linked audio clip that play the same part of the same file at the same place become one clip that plays its
/// audio; the audio clip's level, fades, volume keyframes and markers move onto it. A pair that has drifted apart
/// (moved or trimmed on its own) stays as it is, the audio clip playing the sound, and every other video clip stays
/// silent, so the project sounds as it did.
pub mod embedded_audio {
    use std::collections::{HashMap, HashSet};

    use crate::project::Project;
    use crate::timeline::{Clip, ClipBody, Timeline, TrackKind, automation_keys};

    pub const VERSION: i32 = 1;

    const EPS: f64 = 1e-6;

    /// Upgrades the project if it is older than [`VERSION`]. Returns how many pairs were merged.
    pub fn migrate(project: &mut Project) -> usize {
        if project.format_version >= VERSION {
            return 0;
        }
        let merged = project.timelines.iter_mut().map(migrate_timeline).sum();
        project.format_version = VERSION;
        merged
    }

    fn migrate_timeline(timeline: &mut Timeline) -> usize {
        let mut emptied: HashSet<String> = HashSet::new();
        let mut merged = 0;

        let videos: Vec<(usize, usize)> = timeline
            .tracks
            .iter()
            .enumerate()
            .filter(|(_, t)| t.kind == TrackKind::Video)
            .flat_map(|(ti, t)| {
                t.clips.iter().enumerate().filter(|(_, c)| c.as_video().is_some()).map(move |(ci, _)| (ti, ci))
            })
            .collect();

        for (vt, vc) in videos {
            let video = &timeline.tracks[vt].clips[vc];
            if video.link_group_id.as_deref().is_none_or(str::is_empty) {
                continue;
            }
            let found =
                timeline.tracks.iter().enumerate().filter(|(_, t)| t.kind == TrackKind::Audio).find_map(|(ti, t)| {
                    t.clips
                        .iter()
                        .position(|a| {
                            a.as_audio().is_some() && a.link_group_id == video.link_group_id && same_sound(video, a)
                        })
                        .map(|ci| (ti, ci))
                });
            let Some((at, ac)) = found else {
                continue;
            };
            let audio = timeline.tracks[at].clips.remove(ac);
            if timeline.tracks[at].clips.is_empty() {
                emptied.insert(timeline.tracks[at].id.clone());
            }

            let video = &mut timeline.tracks[vt].clips[vc];
            video.volume = audio.volume;
            if let ClipBody::Video(v) = &mut video.body {
                v.plays_audio = true;
                v.audio_fade_in_sec = audio.fade_in_sec;
                v.audio_fade_out_sec = audio.fade_out_sec;
            }
            if let Some(volume) = audio.keyframes.get(automation_keys::VOLUME) {
                video.keyframes.insert(automation_keys::VOLUME.into(), volume.clone());
            }
            for marker in audio.markers {
                if !video.markers.iter().any(|m| (m.start_sec - marker.start_sec).abs() < EPS && m.name == marker.name)
                {
                    video.markers.push(marker);
                }
            }
            merged += 1;
        }

        // a link group left with one clip is no group
        let mut counts: HashMap<String, usize> = HashMap::new();
        for group in timeline.clips().filter_map(|c| c.link_group_id.clone()).filter(|g| !g.is_empty()) {
            *counts.entry(group).or_default() += 1;
        }
        for clip in timeline.clips_mut() {
            if clip.link_group_id.as_ref().is_some_and(|g| counts.get(g) == Some(&1)) {
                clip.link_group_id = None;
            }
        }

        // audio tracks that only held a video's sound, now part of the video
        timeline.tracks.retain(|t| !emptied.contains(&t.id));
        merged
    }

    /// True when the audio clip plays exactly the video clip's sound: the same file, place, part and speed, on, and
    /// with no effects of its own. Its fades become the sound's fades.
    fn same_sound(video: &Clip, audio: &Clip) -> bool {
        let (Some(v), Some(a)) = (video.as_video(), audio.as_audio()) else {
            return false;
        };
        a.source_id == v.source_id
            && (audio.start_sec - video.start_sec).abs() < EPS
            && (audio.dur_sec - video.dur_sec).abs() < EPS
            && (a.src_in_sec - v.src_in_sec).abs() < EPS
            && (a.src_out_sec - v.src_out_sec).abs() < EPS
            && (audio.speed - video.speed).abs() < EPS
            && audio.enabled == video.enabled
            && audio.effects.is_empty()
            && !video.keyframes.contains_key(automation_keys::VOLUME)
    }
}

/// Upgrades a project saved when a clip whose file had another frame rate than the timeline was retimed to it: it
/// played at `speed × source rate / rate`. Clips now play in real time, so that factor moves into `Clip::speed` and
/// every clip keeps its place, length and sound.
pub mod real_time_media {
    use crate::project::Project;

    pub const VERSION: i32 = 2;

    /// Upgrades the project if it is older than [`VERSION`]. Returns how many clips changed speed.
    pub fn migrate(project: &mut Project) -> usize {
        if project.format_version >= VERSION {
            return 0;
        }
        let mut changed = 0;
        for timeline in &mut project.timelines {
            let fps = timeline.rate.fps();
            if !fps.is_finite() || fps <= 0.0 {
                continue;
            }
            for clip in timeline.clips_mut() {
                let Some(source) = clip.source_rate.filter(|r| r.den() > 0 && r.fps() > 0.0) else {
                    continue;
                };
                let ratio = source.fps() / fps;
                if (ratio - 1.0).abs() < 1e-6 {
                    continue;
                }
                clip.speed = if clip.speed <= 0.0 { 1.0 } else { clip.speed } * ratio;
                changed += 1;
            }
        }
        project.format_version = VERSION;
        changed
    }
}
