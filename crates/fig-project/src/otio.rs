//! Importing OpenTimelineIO (`.otio`) timelines: tracks, clip ranges, gaps, transitions (as cross dissolves),
//! media references, markers, nesting, and the metadata other tools left on them.

use serde_json::Value;

use crate::Project;
use fig_timeline::{
    AudioBody, Clip, EffectInstance, FrameRate, Marker, MediaAsset, MediaKind, Metadata, ParamValue, Timeline, Track,
    TrackKind, TransitionRef, VideoBody,
};

#[derive(Debug, Default)]
pub struct OtioImport {
    pub project: Project,
    pub warnings: Vec<String>,
    pub sources: Vec<String>,
    pub clips_imported: usize,
    pub transitions_imported: usize,
    pub effects_imported: usize,
    pub markers_imported: usize,
    pub gaps_skipped: usize,
}

pub fn import_file(path: &std::path::Path) -> Result<OtioImport, String> {
    let text = std::fs::read_to_string(path).map_err(|e| e.to_string())?;
    let mut result = import(&text)?;
    if result.project.name.is_empty() {
        result.project.name = path.file_stem().map_or_else(String::new, |s| s.to_string_lossy().into_owned());
    }
    Ok(result)
}

pub fn import(json: &str) -> Result<OtioImport, String> {
    let root: Value = serde_json::from_str(json).map_err(|e| e.to_string())?;
    if !root.is_object() {
        return Err("OTIO root must be an object.".into());
    }
    let mut r = OtioImport { project: Project::create(""), ..Default::default() };
    match schema(&root) {
        Some("Timeline.1") => {
            r.project.name = root.get("name").and_then(Value::as_str).unwrap_or("").into();
            r.project.metadata = metadata_of(&root);
            let stack = root.get("tracks").filter(|t| t.is_object());
            let mut timeline = Timeline { rate: infer_rate(&root, stack), ..Default::default() };
            if let Some(stack) = stack {
                timeline.global_start_sec =
                    root.get("global_start_time").filter(|g| g.is_object()).map_or(0.0, seconds);
                timeline.metadata = metadata_of(stack);
                import_markers(stack, true, 0.0, &mut timeline.markers, &mut r);
                import_stack(stack, &mut timeline, &mut r);
            }
            r.project.timelines.push(timeline);
            Ok(r)
        }
        Some("Stack.1" | "Track.1" | "Sequence.1") => {
            r.project.name = "OTIO Import".into();
            r.project.metadata = metadata_of(&root);
            let mut timeline = Timeline { rate: infer_rate(&root, None), ..Default::default() };
            import_markers(&root, true, 0.0, &mut timeline.markers, &mut r);
            import_stack(&root, &mut timeline, &mut r);
            r.project.timelines.push(timeline);
            Ok(r)
        }
        other => Err(format!(
            "Unsupported OTIO root schema '{}'. Expected Timeline.1, Stack.1 or Track.1.",
            other.unwrap_or("")
        )),
    }
}

fn schema(v: &Value) -> Option<&str> {
    v.get("OTIO_SCHEMA").and_then(Value::as_str)
}

fn name(v: &Value) -> String {
    v.get("name").and_then(Value::as_str).unwrap_or("").into()
}

fn children(v: &Value) -> &[Value] {
    v.get("children").and_then(Value::as_array).map_or(&[], |a| a.as_slice())
}

fn seconds(rt: &Value) -> f64 {
    let value = rt.get("value").and_then(Value::as_f64).unwrap_or(0.0);
    let rate = rt.get("rate").and_then(Value::as_f64).unwrap_or(0.0);
    if rate != 0.0 { value / rate } else { value }
}

fn range_seconds(range: Option<&Value>) -> Option<(f64, f64)> {
    let r = range.filter(|r| r.is_object())?;
    let start = r.get("start_time").filter(|s| s.is_object()).map_or(0.0, seconds);
    let dur = r.get("duration").filter(|d| d.is_object()).map_or(0.0, seconds);
    Some((start, dur))
}

fn metadata_of(v: &Value) -> Metadata {
    v.get("metadata")
        .and_then(Value::as_object)
        .map(|m| m.iter().map(|(k, v)| (k.clone(), v.clone())).collect())
        .unwrap_or_default()
}

fn import_stack(stack: &Value, timeline: &mut Timeline, r: &mut OtioImport) {
    for child in children(stack) {
        if matches!(schema(child), Some("Track.1" | "Track.2" | "Sequence.1")) {
            import_track(child, timeline, r);
        }
    }
}

fn import_track(otio: &Value, timeline: &mut Timeline, r: &mut OtioImport) {
    let audio = otio.get("kind").and_then(Value::as_str).is_some_and(|k| k.eq_ignore_ascii_case("audio"));
    let mut track = Track::new(if audio { TrackKind::Audio } else { TrackKind::Video });
    track.index = timeline.tracks.len() as i32;
    track.metadata = metadata_of(otio);
    import_markers(otio, true, 0.0, &mut track.markers, r);
    populate(&mut track, children(otio), r);
    timeline.tracks.push(track);
}

fn populate(track: &mut Track, items: &[Value], r: &mut OtioImport) {
    let mut pos = 0.0;
    let mut last: Option<usize> = None;
    let mut pending: Option<&Value> = None;
    for child in items {
        match schema(child) {
            Some("Clip.1" | "Clip.2") => {
                if let Some(clip) = import_clip(child, track.kind, pos, r) {
                    let index = track.clips.len();
                    pos += clip.dur_sec;
                    track.clips.push(clip);
                    flush_transition(track, pending.take(), last, Some(index), r);
                    last = Some(index);
                }
            }
            Some("Gap.1") => {
                if let Some((_, d)) = range_seconds(child.get("source_range"))
                    && d > 0.0
                {
                    pos += d;
                    r.gaps_skipped += 1;
                }
            }
            Some("Transition.1") => pending = Some(child),
            Some("Stack.1" | "Track.1" | "Track.2" | "Sequence.1") => {
                let nested = children(child);
                if !nested.is_empty() {
                    populate(track, nested, r);
                    pos = track.clips.iter().map(Clip::end_sec).fold(pos, f64::max);
                }
            }
            other => r.warnings.push(format!("Skipping unsupported track item '{}'.", other.unwrap_or(""))),
        }
    }
    flush_transition(track, pending, last, None, r);
}

fn flush_transition(
    track: &mut Track,
    pending: Option<&Value>,
    prev: Option<usize>,
    next: Option<usize>,
    r: &mut OtioImport,
) {
    let Some(t) = pending else { return };
    let offset = |k: &str| t.get(k).filter(|v| v.is_object()).map_or(0.0, seconds);
    let duration = (offset("in_offset") + offset("out_offset")).max(0.0);
    let tx = || Some(TransitionRef { type_id: "cross-dissolve".into(), duration_sec: duration, ..Default::default() });
    if let Some(p) = prev {
        track.clips[p].transition_out = tx();
        r.transitions_imported += 1;
    }
    if let Some(n) = next {
        track.clips[n].transition_in = tx();
        r.transitions_imported += 1;
    }
    if prev.is_none() && next.is_none() {
        r.warnings.push(format!("Transition '{}' has no adjacent clip and was skipped.", name(t)));
    }
}

fn active_media_reference(clip: &Value) -> Option<&Value> {
    if let Some(single) = clip.get("media_reference").filter(|m| m.is_object()) {
        return Some(single);
    }
    let key = clip.get("active_media_reference_key").and_then(Value::as_str)?;
    clip.get("media_references")?.get(key).filter(|m| m.is_object())
}

fn media_url(media: Option<&Value>) -> Option<String> {
    let m = media?;
    if schema(m) != Some("ExternalReference.1") {
        return None;
    }
    let url = m.get("target_url").and_then(Value::as_str).filter(|u| !u.is_empty())?;
    if url.len() >= 7 && url[..7].eq_ignore_ascii_case("file://") {
        let rest = url[7..].trim_start_matches('/');
        let b = rest.as_bytes();
        // a Windows "C:/..." path
        if rest.len() > 2 && b[1] == b':' {
            return Some(rest.into());
        }
        return Some(format!("/{rest}"));
    }
    Some(url.into())
}

fn import_clip(clip: &Value, kind: TrackKind, start: f64, r: &mut OtioImport) -> Option<Clip> {
    let range = range_seconds(clip.get("source_range"));
    let media = active_media_reference(clip);
    let available = media.and_then(|m| range_seconds(m.get("available_range")));
    // a clip without a source range plays the whole of what its media has
    let src_in = range.map(|r| r.0).or(available.map(|a| a.0)).unwrap_or(0.0);
    let dur = range.map(|r| r.1).or(available.map(|a| a.1)).unwrap_or(0.0);
    if dur <= 0.0 {
        r.warnings.push(format!("Clip '{}' has no usable duration and was skipped.", name(clip)));
        return None;
    }
    let mut source_id = String::new();
    if let Some(url) = media_url(media) {
        let asset = ensure_asset(&mut r.project, &url, kind, available.map_or(0.0, |a| a.0));
        asset.metadata = media.map(metadata_of).unwrap_or_default();
        source_id = asset.id.clone();
        if !r.sources.contains(&url) {
            r.sources.push(url);
        }
    } else if let Some(m) = media {
        r.warnings.push(format!(
            "Clip '{}' references media without a resolvable URL ({}).",
            name(clip),
            schema(m).unwrap_or("?")
        ));
    }
    let mut c = match kind {
        TrackKind::Audio => Clip::new(AudioBody { source_id, src_in_sec: src_in, src_out_sec: src_in + dur }),
        TrackKind::Video => {
            Clip::new(VideoBody { source_id, src_in_sec: src_in, src_out_sec: src_in + dur, ..Default::default() })
        }
    }
    .at(start, dur);
    c.enabled = clip.get("enabled").and_then(Value::as_bool).unwrap_or(true);
    c.metadata = metadata_of(clip);
    for (order, effect) in clip.get("effects").and_then(Value::as_array).into_iter().flatten().enumerate() {
        let effect_name = effect.get("effect_name").and_then(Value::as_str).unwrap_or("");
        // the effects Fig has an equivalent of, at their defaults
        let (type_id, amount) = match effect_name.to_lowercase().as_str() {
            "brightness" => ("brightness", 0.15),
            "grayscale" | "desaturate" => ("grayscale", 1.0),
            _ => {
                r.warnings.push(format!(
                    "Effect '{effect_name}' on clip '{}' has no fig equivalent and was skipped.",
                    name(clip)
                ));
                continue;
            }
        };
        c.effects.push(EffectInstance {
            type_id: type_id.into(),
            order: order as i32,
            params: [("amount".to_string(), ParamValue::Double(amount))].into_iter().collect(),
            ..Default::default()
        });
        r.effects_imported += 1;
    }
    import_markers(clip, false, src_in, &mut c.markers, r);
    r.clips_imported += 1;
    Some(c)
}

fn ensure_asset<'a>(project: &'a mut Project, url: &str, kind: TrackKind, source_start: f64) -> &'a mut MediaAsset {
    if let Some(i) = project.media.iter().position(|a| a.url.eq_ignore_ascii_case(url)) {
        let a = &mut project.media[i];
        if kind == TrackKind::Audio {
            a.has_audio = true;
        }
        if a.source_start_sec == 0.0 {
            a.source_start_sec = source_start;
        }
        return a;
    }
    project.media.push(MediaAsset {
        url: url.into(),
        kind: if kind == TrackKind::Audio { MediaKind::Audio } else { MediaKind::Video },
        has_audio: kind == TrackKind::Audio,
        source_start_sec: source_start,
        ..Default::default()
    });
    project.media.last_mut().unwrap()
}

fn import_markers(parent: &Value, absolute: bool, clip_src_start: f64, target: &mut Vec<Marker>, r: &mut OtioImport) {
    for m in parent.get("markers").and_then(Value::as_array).into_iter().flatten() {
        let marked = m.get("marked_range").filter(|v| v.is_object());
        let start = marked
            .map_or(0.0, |mr| mr.get("start_time").filter(|s| s.is_object()).map_or_else(|| seconds(mr), seconds));
        let dur =
            marked.map_or(0.0, |mr| mr.get("duration").filter(|d| d.is_object()).map_or_else(|| seconds(mr), seconds));
        target.push(Marker {
            name: name(m),
            start_sec: if absolute { start } else { start - clip_src_start },
            dur_sec: dur,
            color: marker_color(m),
            metadata: Some(metadata_of(m)),
            ..Default::default()
        });
        r.markers_imported += 1;
    }
}

fn marker_color(m: &Value) -> String {
    let named = match m.get("color") {
        Some(Value::Object(c)) => c.get("name").and_then(Value::as_str).filter(|s| !s.is_empty()).map(str::to_string),
        Some(Value::String(s)) if !s.is_empty() => Some(s.clone()),
        _ => None,
    };
    let named = named.or_else(|| {
        metadata_of(m).get("cmx_3600").and_then(|c| c.get("color")).and_then(Value::as_str).map(str::to_string)
    });
    match named.map(|n| n.to_uppercase()).as_deref() {
        Some("RED") => "#ff3b30",
        Some("ORANGE") => "#ff9500",
        Some("YELLOW") => "#ffcc00",
        Some("GREEN") => "#34c759",
        Some("TEAL") => "#5ac8fa",
        Some("BLUE") => "#0a84ff",
        Some("PURPLE") => "#af52de",
        Some("PINK") => "#ff2d55",
        Some("WHITE") => "#f5f5f7",
        Some("BLACK") => "#1c1c1e",
        _ => "#ffd60a",
    }
    .into()
}

fn first_rate(v: &Value) -> Option<f64> {
    if let Some(rate) =
        v.get("source_range").and_then(|s| s.get("start_time")).and_then(|t| t.get("rate")).and_then(Value::as_f64)
        && rate > 0.0
    {
        return Some(rate);
    }
    children(v).iter().find_map(first_rate)
}

fn infer_rate(root: &Value, stack: Option<&Value>) -> FrameRate {
    if let Some(rate) = root.get("global_start_time").and_then(|g| g.get("rate")).and_then(Value::as_f64)
        && rate > 0.0
    {
        return FrameRate::common(rate);
    }
    FrameRate::common(stack.and_then(first_rate).unwrap_or(30.0))
}
