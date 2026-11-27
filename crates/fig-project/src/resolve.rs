//! A timeline for DaVinci Resolve, as Final Cut Pro 7 XML (`xmeml`), which Resolve imports with File ▸ Import ▸
//! Timeline. It is track-based like Fig: every video and audio track comes over with its cuts, speeds, opacity,
//! volume, crops, dissolves, fades and markers, and points at the original media so it can be graded there.
//!
//! What Resolve can't read from Fig is either drawn or listed: text (and stills Fig has moved or turned) become
//! transparent PNGs the caller draws, and effects and animation are named in [`ResolveExport::notes`] so nothing goes
//! missing unseen.
//!
//! Checked against Resolve Studio 21.2 by importing and exporting back: crops with their zoom and move land to the
//! pixel; only the transitions in [`resolve_transition`] become Resolve's own (any other name is a placeholder); no
//! clip filter becomes a Resolve effect, so effects can't travel this way; markers inside clip items are dropped, so
//! they go on the timeline; and a fade at an edge another clip touches would turn into a dissolve between the two,
//! so those fade by keyframes instead.

use std::collections::HashMap;
use std::fmt::Write as _;
use std::path::{Path, PathBuf};

use fig_timeline::{Clip, ClipBody, FrameRate, LayerTransform, MediaAsset, Timeline, TrackKind, TransitionRef};

/// The XML, and what didn't come over as it is in Fig.
pub struct ResolveExport {
    pub xml: String,
    pub notes: Vec<String>,
}

/// Builds the XML for `timeline` at `size` pixels. `still` draws a text clip, or a still with a layout of its own,
/// to a frame-sized PNG and returns its path (or none when it can't); `effect_name` names an effect type for the
/// notes.
pub fn export(
    name: &str,
    timeline: &Timeline,
    media: &[MediaAsset],
    size: (u32, u32),
    still: &mut dyn FnMut(&Clip) -> Option<PathBuf>,
    effect_name: &dyn Fn(&str) -> String,
) -> ResolveExport {
    let mut w = Writer::new(timeline.rate);
    let mut notes = Notes { kinds: Vec::new(), effect_name };
    let duration = timeline.tracks.iter().flat_map(|t| &t.clips).map(Clip::end_sec).fold(0.0, f64::max);

    w.raw("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE xmeml>\n");
    w.open("xmeml version=\"5\"");
    w.open("sequence id=\"sequence-1\"");
    w.leaf("name", name);
    w.leaf("duration", w.frames(duration));
    w.rate();
    w.open("timecode");
    w.rate();
    w.leaf("string", "00:00:00:00");
    w.leaf("frame", 0);
    w.leaf("displayformat", "NDF");
    w.close("timecode");
    w.open("media");

    // ---- picture: the bottom track first, as Resolve numbers them
    w.open("video");
    w.open("format");
    w.open("samplecharacteristics");
    w.rate();
    w.leaf("width", size.0);
    w.leaf("height", size.1);
    w.leaf("anamorphic", "FALSE");
    w.leaf("pixelaspectratio", "square");
    w.leaf("fielddominance", "none");
    w.close("samplecharacteristics");
    w.close("format");
    let video: Vec<_> = timeline.tracks.iter().rev().filter(|t| t.kind == TrackKind::Video).collect();
    for (n, track) in video.iter().enumerate() {
        let label = format!("V{}", n + 1);
        w.open("track");
        let clips: Vec<&Clip> = track.clips.iter().filter(|c| !matches!(c.body, ClipBody::Audio(_))).collect();
        for (i, clip) in clips.iter().enumerate() {
            let prev = i.checked_sub(1).map(|p| clips[p]);
            let next = clips.get(i + 1).copied();
            video_clip(&mut w, &mut notes, &label, clip, (prev, next), media, still);
        }
        for fx in &track.effects {
            notes.effect(&label, &fx.type_id, fx.start_sec, fx.dur_sec);
        }
        w.leaf("enabled", if track.visible { "TRUE" } else { "FALSE" });
        w.leaf("locked", "FALSE");
        w.close("track");
    }
    w.close("video");

    // ---- sound: each video track's own sound, then the audio tracks
    w.open("audio");
    w.leaf("numOutputChannels", 2);
    w.open("format");
    w.open("samplecharacteristics");
    w.leaf("depth", 16);
    w.leaf("samplerate", 48000);
    w.close("samplecharacteristics");
    w.close("format");
    for track in &video {
        let sounding: Vec<&Clip> = track
            .clips
            .iter()
            .filter(|c| match &c.body {
                ClipBody::Video(v) => v.plays_audio && media.iter().any(|m| m.id == v.source_id && m.has_audio),
                _ => false,
            })
            .collect();
        if sounding.is_empty() {
            continue;
        }
        w.open("track");
        for (i, clip) in sounding.iter().enumerate() {
            let prev = i.checked_sub(1).map(|p| sounding[p]);
            audio_clip(&mut w, clip, (prev, sounding.get(i + 1).copied()), media);
        }
        w.leaf("enabled", if track.muted { "FALSE" } else { "TRUE" });
        w.close("track");
    }
    for track in timeline.tracks.iter().filter(|t| t.kind == TrackKind::Audio) {
        w.open("track");
        for (i, clip) in track.clips.iter().enumerate() {
            let prev = i.checked_sub(1).map(|p| &track.clips[p]);
            audio_clip(&mut w, clip, (prev, track.clips.get(i + 1)), media);
        }
        w.leaf("enabled", if track.muted { "FALSE" } else { "TRUE" });
        w.close("track");
    }
    w.close("audio");
    w.close("media");

    // clip markers too: Resolve drops markers inside clip items
    for m in &timeline.markers {
        w.marker(m, 0.0);
    }
    for clip in timeline.tracks.iter().flat_map(|t| &t.clips) {
        for m in &clip.markers {
            w.marker(m, clip.start_sec);
        }
    }
    w.close("sequence");
    w.close("xmeml");
    ResolveExport { xml: w.out, notes: notes.lines() }
}

fn video_clip(
    w: &mut Writer,
    notes: &mut Notes<'_>,
    track: &str,
    clip: &Clip,
    (prev, next): (Option<&Clip>, Option<&Clip>),
    media: &[MediaAsset],
    still: &mut dyn FnMut(&Clip) -> Option<PathBuf>,
) {
    let touches = |other: Option<&Clip>, at: f64| {
        other.is_some_and(|o| (o.start_sec - at).abs() < 1e-3 || (o.end_sec() - at).abs() < 1e-3)
    };
    let (shared_in, shared_out) = (touches(prev, clip.start_sec), touches(next, clip.end_sec()));
    let at = format!("{track} {}", clock(clip.start_sec));
    // what it shows: its own file, or a picture of it
    let (file, crop) = match &clip.body {
        ClipBody::Video(v) => match media.iter().find(|m| m.id == v.source_id) {
            Some(m) => (FileRef::Media(m), [v.crop_l, v.crop_t, v.crop_r, v.crop_b]),
            None => return notes.push("Clips whose media is missing were left out".into(), at),
        },
        ClipBody::Image(i) => match media.iter().find(|m| m.id == i.source_id) {
            Some(m) if i.layout == LayerTransform::default() && !animated(clip, "layout") => {
                (FileRef::Media(m), [i.crop_l, i.crop_t, i.crop_r, i.crop_b])
            }
            Some(_) => match still(clip) {
                Some(p) => (FileRef::Still(p), [0.0; 4]),
                None => return notes.push("Stills Fig couldn't draw were left out".into(), at),
            },
            None => return notes.push("Clips whose media is missing were left out".into(), at),
        },
        ClipBody::Text(t) => match still(clip) {
            Some(p) => {
                if animated(clip, "") {
                    notes.push("Moving text comes over still".into(), format!("{at} \"{}\"", short(&t.text)));
                }
                (FileRef::Still(p), [0.0; 4])
            }
            None => {
                return notes
                    .push("Text Fig couldn't draw was left out".into(), format!("{at} \"{}\"", short(&t.text)));
            }
        },
        ClipBody::Audio(_) => return,
    };

    if clip.fade_in_sec > 0.0 && !shared_in {
        w.transition(clip.start_sec, clip.start_sec + clip.fade_in_sec, "start-black", &Transition::DISSOLVE);
    }

    let (src_in, src_out) = source_range(clip);
    let speed = if clip.dur_sec > 0.0 { (src_out - src_in) / clip.dur_sec } else { 1.0 };
    if fig_timeline::clip_time::has_curve(clip) {
        notes.push("Speed ramps come over at their average speed".into(), format!("{at} ({:.0}%)", speed * 100.0));
    }

    let id = w.next_id();
    w.open(&format!("clipitem id=\"clipitem-{id}\""));
    w.leaf("name", file.name(clip));
    w.leaf("enabled", if clip.enabled { "TRUE" } else { "FALSE" });
    w.leaf("duration", w.frames(file.duration(clip)));
    w.rate();
    w.leaf("start", w.frames(clip.start_sec));
    w.leaf("end", w.frames(clip.end_sec()));
    w.leaf("in", w.frames(src_in));
    w.leaf("out", w.frames(src_out));
    w.file(&file, clip);
    if (speed - 1.0).abs() > 1e-4 {
        w.time_remap(speed);
    }
    if crop.iter().any(|c| *c > 1e-4) {
        w.crop(crop);
        if animated(clip, "crop") {
            notes.push("Moving crops come over at their first framing".into(), at.clone());
        }
    }
    // fades at edges another clip touches go in as keyframes, from the clip's own source frames
    let fade_in = if shared_in { clip.fade_in_sec.min(clip.dur_sec) } else { 0.0 };
    let fade_out = if shared_out { clip.fade_out_sec.min(clip.dur_sec) } else { 0.0 };
    if clip.opacity < 0.999 || fade_in > 0.0 || fade_out > 0.0 {
        let base = clip.opacity.clamp(0.0, 1.0) * 100.0;
        let mut keys = Vec::new();
        let at = |local: f64| w.frames(src_in + local * speed);
        if fade_in > 0.0 {
            keys.push((at(0.0), 0.0));
            keys.push((at(fade_in), base));
        }
        if fade_out > 0.0 {
            keys.push((at(clip.dur_sec - fade_out), base));
            keys.push((at(clip.dur_sec), 0.0));
        }
        w.opacity(base, &keys);
    }
    for key in clip.keyframes.iter().filter(|(_, k)| !k.is_empty()).map(|(k, _)| k.as_str()) {
        match key {
            k if k.to_lowercase().contains("crop") || k.to_lowercase().contains("layout") => {}
            k if k == fig_timeline::automation_keys::SPEED => {}
            k => notes.push(format!("Animated {k} comes over at its starting value"), at.clone()),
        }
    }
    for fx in &clip.effects {
        notes.effect(&at, &fx.type_id, None, None);
    }
    w.close("clipitem");

    if clip.fade_out_sec > 0.0 && !shared_out {
        w.transition(clip.end_sec() - clip.fade_out_sec, clip.end_sec(), "end-black", &Transition::DISSOLVE);
    }
    if let (Some(t), Some(next)) = (&clip.transition_out, next)
        && (next.start_sec - clip.end_sec()).abs() < 1e-3
    {
        let half = t.duration_sec.max(0.0) / 2.0;
        let (kind, exact) = resolve_transition(t);
        w.transition(clip.end_sec() - half, clip.end_sec() + half, "center", &kind);
        if !exact {
            notes.push(
                format!("The \"{}\" transition comes over as a {}", t.type_id, kind.name.to_lowercase()),
                at.clone(),
            );
        }
    }
}

/// A transition as Resolve's XML import knows it: its name and category, and its settings.
struct Transition {
    name: &'static str,
    category: &'static str,
    audio: bool,
    params: Vec<(&'static str, String)>,
}

impl Transition {
    const DISSOLVE: Transition =
        Transition { name: "Cross Dissolve", category: "Dissolve", audio: false, params: Vec::new() };
    const AUDIO_FADE: Transition =
        Transition { name: "Cross Fade (+3dB)", category: "", audio: true, params: Vec::new() };
}

/// The Resolve transition closest to a Fig one, and whether it is the same thing. Only these names become Resolve's
/// own transitions on import (Fade To Color, Wipe, Center, Clock, Circle, Venetian Blind); slides, pushes, zooms,
/// spins and the rest come in as placeholders, so those go over as dissolves.
fn resolve_transition(t: &TransitionRef) -> (Transition, bool) {
    let number = |key: &str, default: f64| t.params.get(key).map_or(default, |v| (*v).as_number());
    let color = |rgb: u8| format!("<alpha>255</alpha><red>{rgb}</red><green>{rgb}</green><blue>{rgb}</blue>");
    let wipe = |name| Transition { name, category: "Wipe", audio: false, params: Vec::new() };
    match t.type_id.as_str() {
        "cross-dissolve" => (Transition::DISSOLVE, true),
        "fade-to-black" => (
            Transition {
                name: "Dip to Color Dissolve",
                category: "Dissolve",
                audio: false,
                params: vec![("dipcolor", color(0))],
            },
            true,
        ),
        "white-flash" => (
            Transition {
                name: "Dip to Color Dissolve",
                category: "Dissolve",
                audio: false,
                params: vec![("dipcolor", color(255))],
            },
            true,
        ),
        "wipe" => (wipe("Edge Wipe"), true),
        "curtain" => (wipe("Center Wipe"), true),
        "radial-wipe" => {
            let clockwise = number("direction", 0.0) < 0.5;
            (
                Transition {
                    params: vec![("wipedirection", if clockwise { "1" } else { "2" }.into())],
                    ..wipe("Clock Wipe")
                },
                true,
            )
        }
        "blinds" => (
            Transition {
                params: vec![("bands", format!("{}", number("stripes", 10.0).round()))],
                ..wipe("Venetian Blind Wipe")
            },
            true,
        ),
        "iris" => (Transition { name: "Oval Iris", category: "Iris", audio: false, params: Vec::new() }, true),
        _ => (Transition::DISSOLVE, false),
    }
}

fn audio_clip(w: &mut Writer, clip: &Clip, (prev, next): (Option<&Clip>, Option<&Clip>), media: &[MediaAsset]) {
    let touches = |other: Option<&Clip>, at: f64| {
        other.is_some_and(|o| (o.start_sec - at).abs() < 1e-3 || (o.end_sec() - at).abs() < 1e-3)
    };
    let (shared_in, shared_out) = (touches(prev, clip.start_sec), touches(next, clip.end_sec()));
    let (source_id, fade_in, fade_out) = match &clip.body {
        ClipBody::Video(v) => (&v.source_id, v.audio_fade_in_sec, v.audio_fade_out_sec),
        ClipBody::Audio(a) => (&a.source_id, clip.fade_in_sec, clip.fade_out_sec),
        _ => return,
    };
    let Some(m) = media.iter().find(|m| &m.id == source_id) else { return };
    let file = FileRef::Media(m);
    if fade_in > 0.0 && !shared_in {
        w.transition(clip.start_sec, clip.start_sec + fade_in, "start-black", &Transition::AUDIO_FADE);
    }
    let (src_in, src_out) = source_range(clip);
    let speed = if clip.dur_sec > 0.0 { (src_out - src_in) / clip.dur_sec } else { 1.0 };
    let id = w.next_id();
    w.open(&format!("clipitem id=\"clipitem-{id}\""));
    w.leaf("name", file.name(clip));
    w.leaf("enabled", if clip.enabled { "TRUE" } else { "FALSE" });
    w.leaf("duration", w.frames(file.duration(clip)));
    w.rate();
    w.leaf("start", w.frames(clip.start_sec));
    w.leaf("end", w.frames(clip.end_sec()));
    w.leaf("in", w.frames(src_in));
    w.leaf("out", w.frames(src_out));
    w.file(&file, clip);
    w.open("sourcetrack");
    w.leaf("mediatype", "audio");
    w.leaf("trackindex", 1);
    w.close("sourcetrack");
    if (speed - 1.0).abs() > 1e-4 {
        w.time_remap(speed);
    }
    let key_in = if shared_in { fade_in.min(clip.dur_sec) } else { 0.0 };
    let key_out = if shared_out { fade_out.min(clip.dur_sec) } else { 0.0 };
    if (clip.volume - 1.0).abs() > 1e-4 || key_in > 0.0 || key_out > 0.0 {
        let level = clip.volume.max(0.0);
        let at = |local: f64| w.frames(src_in + local * speed);
        let mut keys = Vec::new();
        if key_in > 0.0 {
            keys.push((at(0.0), 0.0));
            keys.push((at(key_in), level));
        }
        if key_out > 0.0 {
            keys.push((at(clip.dur_sec - key_out), level));
            keys.push((at(clip.dur_sec), 0.0));
        }
        w.audio_level(level, &keys);
    }
    w.close("clipitem");
    if fade_out > 0.0 && !shared_out {
        w.transition(clip.end_sec() - fade_out, clip.end_sec(), "end-black", &Transition::AUDIO_FADE);
    }
}

/// Where in its file a clip starts and ends, from the file's first frame.
fn source_range(clip: &Clip) -> (f64, f64) {
    match &clip.body {
        ClipBody::Video(v) => (v.src_in_sec, v.src_out_sec.max(v.src_in_sec)),
        ClipBody::Audio(a) => (a.src_in_sec, a.src_out_sec.max(a.src_in_sec)),
        _ => (0.0, clip.dur_sec),
    }
}

/// Whether any of the clip's keyframe tracks whose name contains `part` has keys ("" for any).
fn animated(clip: &Clip, part: &str) -> bool {
    clip.keyframes.iter().any(|(k, keys)| !keys.is_empty() && k.to_lowercase().contains(part))
}

fn short(text: &str) -> String {
    let line = text.lines().next().unwrap_or("").trim();
    if line.chars().count() > 30 {
        format!("{}…", line.chars().take(30).collect::<String>())
    } else {
        line.to_string()
    }
}

/// `m:ss.f` for naming where something is.
fn clock(sec: f64) -> String {
    let tenths = (sec * 10.0).round() as i64;
    format!("{}:{:02}.{}", tenths / 600, tenths / 10 % 60, tenths % 10)
}

/// What didn't carry over, one line per kind of thing with where it happens.
struct Notes<'a> {
    /// (what, where), in the order first seen.
    kinds: Vec<(String, Vec<String>)>,
    effect_name: &'a dyn Fn(&str) -> String,
}

impl Notes<'_> {
    fn push(&mut self, what: String, place: String) {
        let i = match self.kinds.iter().position(|(w, _)| *w == what) {
            Some(i) => i,
            None => {
                self.kinds.push((what, Vec::new()));
                self.kinds.len() - 1
            }
        };
        let places = &mut self.kinds[i].1;
        if !places.contains(&place) {
            places.push(place);
        }
    }

    fn effect(&mut self, at: &str, type_id: &str, start: Option<f64>, dur: Option<f64>) {
        let name = (self.effect_name)(type_id);
        let place = match (start, dur) {
            (Some(s), Some(d)) => format!("{at} {}–{}", clock(s), clock(s + d)),
            _ => at.to_string(),
        };
        self.push(format!("{name} isn't carried over"), place);
    }

    /// One line per kind: where it happens, or how often and the first few places.
    fn lines(self) -> Vec<String> {
        const SHOWN: usize = 3;
        self.kinds
            .into_iter()
            .map(|(what, mut places)| {
                // by track, then time (reels are short enough for "m:ss" to sort as text)
                places.sort();
                (what, places)
            })
            .map(|(what, places)| match places.len() {
                n if n <= SHOWN + 1 => format!("{what}: {}", places.join(", ")),
                n => format!("{what}: {n} times ({}, and {} more)", places[..SHOWN].join(", "), n - SHOWN),
            })
            .collect()
    }
}

/// The file a clip item plays.
enum FileRef<'a> {
    Media(&'a MediaAsset),
    /// A picture drawn for the clip.
    Still(PathBuf),
}

impl FileRef<'_> {
    fn path(&self) -> &Path {
        match self {
            FileRef::Media(m) => Path::new(&m.url),
            FileRef::Still(p) => p,
        }
    }

    fn name(&self, clip: &Clip) -> String {
        match (&clip.body, self) {
            (ClipBody::Text(t), _) => short(&t.text),
            _ => self.path().file_name().map_or_else(|| "clip".into(), |n| n.to_string_lossy().into_owned()),
        }
    }

    /// How long the file lasts: a still as long as its clip.
    fn duration(&self, clip: &Clip) -> f64 {
        match self {
            FileRef::Media(m) if m.duration_sec > 0.0 => m.duration_sec,
            _ => clip.dur_sec,
        }
    }

    fn key(&self) -> String {
        self.path().to_string_lossy().into_owned()
    }
}

struct Writer {
    out: String,
    depth: usize,
    rate: FrameRate,
    ids: usize,
    files: HashMap<String, usize>,
}

impl Writer {
    fn new(rate: FrameRate) -> Writer {
        Writer { out: String::new(), depth: 0, rate, ids: 0, files: HashMap::new() }
    }

    fn next_id(&mut self) -> usize {
        self.ids += 1;
        self.ids
    }

    fn frames(&self, sec: f64) -> i64 {
        (sec * self.rate.fps()).round() as i64
    }

    fn raw(&mut self, s: &str) {
        self.out.push_str(s);
    }

    fn indent(&mut self) {
        for _ in 0..self.depth {
            self.out.push_str("  ");
        }
    }

    /// Opens `tag` (which may carry attributes).
    fn open(&mut self, tag: &str) {
        self.indent();
        let _ = writeln!(self.out, "<{tag}>");
        self.depth += 1;
    }

    fn close(&mut self, tag: &str) {
        self.depth -= 1;
        self.indent();
        let _ = writeln!(self.out, "</{tag}>");
    }

    fn leaf(&mut self, tag: &str, value: impl std::fmt::Display) {
        self.indent();
        let _ = writeln!(self.out, "<{tag}>{}</{tag}>", escape(&value.to_string()));
    }

    /// The sequence's rate: NTSC rates as their whole-number timebase.
    fn rate(&mut self) {
        let ntsc = self.rate.den() == 1001;
        let timebase =
            if ntsc { (f64::from(self.rate.num()) / 1000.0).round() as i64 } else { self.rate.fps().round() as i64 };
        self.open("rate");
        self.leaf("timebase", timebase);
        self.leaf("ntsc", if ntsc { "TRUE" } else { "FALSE" });
        self.close("rate");
    }

    /// A file: in full the first time, by its id after that.
    fn file(&mut self, file: &FileRef, clip: &Clip) {
        let key = file.key();
        if let Some(&id) = self.files.get(&key) {
            self.indent();
            let _ = writeln!(self.out, "<file id=\"file-{id}\"/>");
            return;
        }
        let id = self.files.len() + 1;
        self.files.insert(key, id);
        self.open(&format!("file id=\"file-{id}\""));
        self.leaf("name", file.path().file_name().map_or_else(String::new, |n| n.to_string_lossy().into_owned()));
        self.leaf("pathurl", file_url(file.path()));
        self.rate();
        self.leaf("duration", self.frames(file.duration(clip)));
        // without a start timecode Resolve refuses the whole timeline once it takes sound from the file
        self.open("timecode");
        self.leaf("string", "00:00:00:00");
        self.leaf("displayformat", "NDF");
        self.rate();
        self.close("timecode");
        self.open("media");
        let (video, audio, size) = match file {
            FileRef::Media(m) => (m.kind != fig_timeline::MediaKind::Audio, m.has_audio, (m.width, m.height)),
            FileRef::Still(_) => (true, false, (0, 0)),
        };
        if video {
            self.open("video");
            if size.0 > 0 && size.1 > 0 {
                self.open("samplecharacteristics");
                self.leaf("width", size.0);
                self.leaf("height", size.1);
                self.close("samplecharacteristics");
            }
            self.close("video");
        }
        if audio {
            self.open("audio");
            self.leaf("channelcount", 2);
            self.close("audio");
        }
        self.close("media");
        self.close("file");
    }

    fn effect(
        &mut self,
        name: &str,
        id: &str,
        category: &str,
        kind: &str,
        mediatype: &str,
        params: &[(&str, &str, String)],
    ) {
        self.effect_keyed(name, id, category, kind, mediatype, params, &[]);
    }

    /// An effect whose first parameter also changes over time: `keys` are (source frame, value).
    #[allow(clippy::too_many_arguments)]
    fn effect_keyed(
        &mut self,
        name: &str,
        id: &str,
        category: &str,
        kind: &str,
        mediatype: &str,
        params: &[(&str, &str, String)],
        keys: &[(i64, f64)],
    ) {
        self.open("filter");
        self.open("effect");
        self.leaf("name", name);
        self.leaf("effectid", id);
        self.leaf("effectcategory", category);
        self.leaf("effecttype", kind);
        self.leaf("mediatype", mediatype);
        for (i, (pid, pname, value)) in params.iter().enumerate() {
            self.open("parameter");
            self.leaf("parameterid", pid);
            self.leaf("name", pname);
            self.raw_value(value);
            if i == 0 {
                for (when, v) in keys {
                    self.open("keyframe");
                    self.leaf("when", when);
                    self.leaf("value", format!("{v:.4}"));
                    self.close("keyframe");
                }
            }
            self.close("parameter");
        }
        self.close("effect");
        self.close("filter");
    }

    /// A parameter's value: plain, or already XML (a point).
    fn raw_value(&mut self, value: &str) {
        self.indent();
        if value.starts_with('<') {
            let _ = writeln!(self.out, "<value>{value}</value>");
        } else {
            let _ = writeln!(self.out, "<value>{}</value>", escape(value));
        }
    }

    fn time_remap(&mut self, speed: f64) {
        self.effect(
            "Time Remap",
            "timeremap",
            "motion",
            "motion",
            "video",
            &[
                ("variablespeed", "variablespeed", "0".into()),
                ("speed", "speed", format!("{:.4}", speed * 100.0)),
                ("reverse", "reverse", "FALSE".into()),
                ("frameblending", "frameblending", "FALSE".into()),
            ],
        );
    }

    /// Fig's crop keeps the kept part filling the frame; Resolve's crop only hides, so a zoom and a move go with it.
    fn crop(&mut self, [l, t, r, b]: [f64; 4]) {
        let pct = |v: f64| format!("{:.4}", (v * 100.0).clamp(0.0, 100.0));
        self.effect(
            "Crop",
            "crop",
            "motion",
            "motion",
            "video",
            &[
                ("left", "left", pct(l)),
                ("right", "right", pct(r)),
                ("top", "top", pct(t)),
                ("bottom", "bottom", pct(b)),
            ],
        );
        let (kw, kh) = ((1.0 - l - r).max(0.01), (1.0 - t - b).max(0.01));
        let zoom = (1.0 / kw).min(1.0 / kh);
        let (cx, cy) = (l + kw / 2.0, t + kh / 2.0);
        let center = format!("<horiz>{:.6}</horiz><vert>{:.6}</vert>", (0.5 - cx) * zoom, (0.5 - cy) * zoom);
        self.effect(
            "Basic Motion",
            "basic",
            "motion",
            "motion",
            "video",
            &[
                ("scale", "Scale", format!("{:.4}", zoom * 100.0)),
                ("rotation", "Rotation", "0".into()),
                ("center", "Center", center),
            ],
        );
    }

    /// Opacity in percent, with keyframes (source frame, percent) when it changes.
    fn opacity(&mut self, percent: f64, keys: &[(i64, f64)]) {
        self.effect_keyed(
            "Opacity",
            "opacity",
            "motion",
            "motion",
            "video",
            &[("opacity", "Opacity", format!("{percent:.2}"))],
            keys,
        );
    }

    /// Sound level as linear gain, with keyframes (source frame, gain) when it changes.
    fn audio_level(&mut self, gain: f64, keys: &[(i64, f64)]) {
        self.effect_keyed(
            "Audio Levels",
            "audiolevels",
            "audiolevels",
            "audiolevels",
            "audio",
            &[("level", "Level", format!("{gain:.4}"))],
            keys,
        );
    }

    /// A transition (with `alignment` "start-black"/"end-black", a fade) from `from` to `to` on the timeline.
    fn transition(&mut self, from: f64, to: f64, alignment: &str, kind: &Transition) {
        if to - from <= 0.0 {
            return;
        }
        self.open("transitionitem");
        self.leaf("start", self.frames(from.max(0.0)));
        self.leaf("end", self.frames(to));
        self.leaf("alignment", alignment);
        self.rate();
        self.open("effect");
        self.leaf("name", kind.name);
        self.leaf("effectid", if kind.audio { "KGAudioTransCrossFade3dB" } else { kind.name });
        self.leaf("effectcategory", kind.category);
        self.leaf("effecttype", "transition");
        self.leaf("mediatype", if kind.audio { "audio" } else { "video" });
        for (id, value) in &kind.params {
            self.open("parameter");
            self.leaf("parameterid", id);
            self.leaf("name", id);
            self.raw_value(value);
            self.close("parameter");
        }
        self.close("effect");
        self.close("transitionitem");
    }

    /// A marker; `origin` is added to its time (a clip's marker counts from its source in point).
    fn marker(&mut self, m: &fig_timeline::Marker, origin: f64) {
        self.open("marker");
        self.leaf("name", &m.name);
        self.leaf("comment", "");
        self.leaf("in", self.frames(origin + m.start_sec));
        self.leaf("out", if m.dur_sec > 0.0 { self.frames(origin + m.start_sec + m.dur_sec) } else { -1 });
        self.close("marker");
    }
}

fn escape(s: &str) -> String {
    s.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;").replace('"', "&quot;")
}

/// `file://localhost/...`, with what a URL can't hold percent-encoded.
fn file_url(path: &Path) -> String {
    let p = path.to_string_lossy().replace('\\', "/");
    let mut out = String::from("file://localhost");
    if !p.starts_with('/') {
        out.push('/');
    }
    for b in p.bytes() {
        if b.is_ascii_alphanumeric() || b"-._~/:".contains(&b) {
            out.push(b as char);
        } else {
            let _ = write!(out, "%{b:02X}");
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use fig_timeline::{Track, VideoBody};

    fn video(id: &str, source: &str, start: f64, dur: f64, src_in: f64, src_out: f64) -> Clip {
        let mut c = Clip::new(ClipBody::Video(VideoBody {
            source_id: source.into(),
            src_in_sec: src_in,
            src_out_sec: src_out,
            plays_audio: true,
            ..Default::default()
        }));
        c.id = id.into();
        c.start_sec = start;
        c.dur_sec = dur;
        c
    }

    #[test]
    fn cuts_speeds_and_shared_files() {
        let asset = MediaAsset {
            id: "m1".into(),
            url: "/clips/my shot.mp4".into(),
            duration_sec: 10.0,
            has_audio: true,
            ..Default::default()
        };
        let mut track = Track::new(TrackKind::Video);
        track.clips.push(video("a", "m1", 0.0, 2.0, 1.0, 3.0));
        // twice as fast: four seconds of source in two
        track.clips.push(video("b", "m1", 2.0, 2.0, 4.0, 8.0));
        let mut timeline = Timeline { rate: FrameRate::new(30, 1), ..Default::default() };
        timeline.tracks.push(track);
        let out = export("Reel & co", &timeline, &[asset], (1080, 1920), &mut |_| None, &|id| id.to_string());
        let xml = &out.xml;
        assert!(xml.contains("<name>Reel &amp; co</name>"));
        assert!(xml.contains("<pathurl>file://localhost/clips/my%20shot.mp4</pathurl>"));
        // the file is written out once and referred to after
        assert_eq!(xml.matches("<file id=\"file-1\">").count(), 1);
        assert!(xml.contains("<file id=\"file-1\"/>"));
        assert!(xml.contains("<start>60</start>") && xml.contains("<in>120</in>") && xml.contains("<out>240</out>"));
        assert!(xml.contains("<value>200.0000</value>"));
        // each video clip's sound comes along on an audio track
        assert_eq!(xml.matches("<mediatype>audio</mediatype>").count(), 2);
        assert!(out.notes.is_empty(), "{:?}", out.notes);
    }

    /// A fade where another clip touches would turn into a dissolve in Resolve, so it goes in as keyframes; a fade
    /// from black keeps its transition.
    #[test]
    fn fades_at_cuts_are_keyframes() {
        let asset = MediaAsset { id: "m1".into(), url: "/a.mp4".into(), duration_sec: 10.0, ..Default::default() };
        let mut track = Track::new(TrackKind::Video);
        let mut a = video("a", "m1", 0.0, 1.0, 0.0, 1.0);
        a.fade_in_sec = 0.5;
        let mut b = video("b", "m1", 1.0, 1.0, 1.0, 2.0);
        b.fade_in_sec = 0.5;
        track.clips = vec![a, b];
        let mut timeline = Timeline { rate: FrameRate::new(30, 1), ..Default::default() };
        timeline.tracks.push(track);
        let xml = export("f", &timeline, &[asset], (1080, 1920), &mut |_| None, &|id| id.to_string()).xml;
        assert_eq!(xml.matches("<alignment>start-black</alignment>").count(), 1);
        // b's keyframes count in source frames: from its in point at 30
        assert!(xml.contains("<when>30</when>") && xml.contains("<when>45</when>"), "{xml}");
    }
}
