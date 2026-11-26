//! Video templates: reusable edits whose media clips point at slots until media fills them.

use std::collections::HashMap;
use std::path::PathBuf;

use chrono::{DateTime, FixedOffset};
use serde::{Deserialize, Serialize};

use fig_timeline::serde_util::{datetime, new_id_n};
use fig_timeline::{
    Clip, ClipBody, FrameRate, MediaAsset, MediaKind, Timeline, Track, TrackKind, TransitionRef, VideoBody,
};

/// A reusable edit. Media clips' source ids in `timeline` are slot ids until the template is used.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct VideoTemplate {
    pub id: String,
    pub name: String,
    pub description: String,
    pub category: String,
    pub tags: Vec<String>,
    pub canvas_width: i32,
    pub canvas_height: i32,
    pub is_built_in: bool,
    #[serde(with = "datetime")]
    pub created_at: DateTime<FixedOffset>,
    pub timeline: Timeline,
    pub slots: Vec<TemplateSlot>,
}

impl Default for VideoTemplate {
    fn default() -> Self {
        VideoTemplate {
            id: new_id_n(),
            name: "Untitled template".into(),
            description: String::new(),
            category: "My templates".into(),
            tags: Vec::new(),
            canvas_width: 1920,
            canvas_height: 1080,
            is_built_in: false,
            created_at: datetime::utc_now(),
            timeline: Timeline { rate: FrameRate::common(30.0), ..Default::default() },
            slots: Vec::new(),
        }
    }
}

impl VideoTemplate {
    pub fn duration_sec(&self) -> f64 {
        self.timeline.duration_sec()
    }

    /// "0:07", "1:20".
    pub fn duration_label(&self) -> String {
        let s = self.duration_sec().floor() as u64;
        format!("{}:{:02}", s / 60, s % 60)
    }

    pub fn slot_label(&self) -> String {
        if self.slots.len() == 1 { "1 media slot".into() } else { format!("{} media slots", self.slots.len()) }
    }

    /// "16:9", "9:16".
    pub fn canvas_label(&self) -> String {
        let (w, h) = (self.canvas_width.max(1), self.canvas_height.max(1));
        let (mut a, mut b) = (w, h);
        while b != 0 {
            (a, b) = (b, a % b);
        }
        format!("{}:{}", w / a, h / a)
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct TemplateSlot {
    pub id: String,
    pub name: String,
    pub kind: MediaKind,
    /// How much timeline time the template gives this slot. Presentation only: longer media is trimmed, shorter
    /// media still fits.
    pub target_duration_sec: f64,
    pub required: bool,
}

impl Default for TemplateSlot {
    fn default() -> Self {
        TemplateSlot {
            id: new_id_n(),
            name: "Media".into(),
            kind: MediaKind::Video,
            target_duration_sec: 0.0,
            required: true,
        }
    }
}

/// A copy of a timeline, with fresh ids (for a new edit) or the same ones (for a blueprint). Linked clips stay linked
/// to each other.
fn clone_timeline(source: &Timeline, fresh: bool) -> Timeline {
    let mut t = source.clone();
    t.revision = 0;
    t.is_autosave = false;
    if !fresh {
        return t;
    }
    t.id = new_id_n();
    let mut links: HashMap<String, String> = HashMap::new();
    for track in &mut t.tracks {
        track.id = new_id_n();
        for m in &mut track.markers {
            m.id = new_id_n();
        }
        for clip in &mut track.clips {
            *clip = Clip { id: new_id_n(), ..clip.duplicate() };
            if let Some(g) = clip.link_group_id.as_mut().filter(|g| !g.is_empty()) {
                *g = links.entry(g.clone()).or_insert_with(new_id_n).clone();
            }
        }
    }
    for m in &mut t.markers {
        m.id = new_id_n();
    }
    t
}

fn source_id_mut(clip: &mut Clip) -> Option<&mut String> {
    match &mut clip.body {
        ClipBody::Video(v) => Some(&mut v.source_id),
        ClipBody::Audio(a) => Some(&mut a.source_id),
        _ => None,
    }
}

fn compatible(expected: MediaKind, actual: MediaKind) -> bool {
    expected == actual || (expected == MediaKind::Video && actual == MediaKind::Image)
}

/// What is wrong with filling a template's slots this way (nothing: it can be used).
pub fn validate_assignments(template: &VideoTemplate, assignments: &HashMap<String, MediaAsset>) -> Vec<String> {
    let mut errors = Vec::new();
    for slot in &template.slots {
        match assignments.get(&slot.id) {
            None if slot.required => errors.push(format!("{} needs media.", slot.name)),
            None => {}
            Some(a) if !compatible(slot.kind, a.kind) => {
                errors.push(format!("{} expects {} media.", slot.name, format!("{:?}", slot.kind).to_lowercase()))
            }
            Some(_) => {}
        }
    }
    errors
}

/// A new timeline from a template, its slots filled by `assignments` (slot id → library media).
pub fn instantiate(template: &VideoTemplate, assignments: &HashMap<String, MediaAsset>) -> Result<Timeline, String> {
    let errors = validate_assignments(template, assignments);
    if !errors.is_empty() {
        return Err(errors.join(" "));
    }
    let mut timeline = clone_timeline(&template.timeline, true);
    for clip in timeline.clips_mut() {
        let rate;
        {
            let Some(id) = source_id_mut(clip) else { continue };
            let Some(asset) = assignments.get(id.as_str()) else { continue };
            *id = asset.id.clone();
            rate = asset.source_rate;
        }
        clip.source_rate = rate;
    }
    Ok(timeline)
}

/// A template from a timeline: each file its media clips play becomes a slot.
pub fn from_timeline(
    name: &str,
    category: &str,
    timeline: &Timeline,
    media: &[MediaAsset],
    description: Option<&str>,
) -> VideoTemplate {
    let mut blueprint = clone_timeline(timeline, false);
    let mut slots: Vec<TemplateSlot> = Vec::new();
    let mut by_source: HashMap<String, usize> = HashMap::new();
    for clip in blueprint.clips_mut() {
        let needed = if clip.source_out() > clip.source_in() {
            clip.source_out() - clip.source_in()
        } else {
            clip.dur_sec * clip.speed.max(0.01)
        };
        let is_audio = matches!(clip.body, ClipBody::Audio(_));
        let Some(source) = source_id_mut(clip) else { continue };
        if source.trim().is_empty() {
            continue;
        }
        let index = match by_source.get(source.as_str()) {
            Some(&i) => {
                slots[i].target_duration_sec = slots[i].target_duration_sec.max(needed);
                i
            }
            None => {
                let asset = media.iter().find(|m| &m.id == source);
                slots.push(TemplateSlot {
                    name: asset.map_or_else(
                        || format!("Media {}", slots.len() + 1),
                        |a| {
                            std::path::Path::new(a.file_name())
                                .file_stem()
                                .map_or_else(String::new, |s| s.to_string_lossy().into_owned())
                        },
                    ),
                    kind: asset.map_or(if is_audio { MediaKind::Audio } else { MediaKind::Video }, |a| a.kind),
                    target_duration_sec: needed,
                    ..Default::default()
                });
                by_source.insert(source.clone(), slots.len() - 1);
                slots.len() - 1
            }
        };
        *source = slots[index].id.clone();
    }
    VideoTemplate {
        name: if name.trim().is_empty() { "Untitled template".into() } else { name.trim().into() },
        description: description.map_or_else(|| "A reusable edit created in Fig.".into(), |d| d.trim().into()),
        category: if category.trim().is_empty() { "My templates".into() } else { category.trim().into() },
        timeline: blueprint,
        slots,
        ..Default::default()
    }
}

fn built_in(
    id: &str,
    name: &str,
    description: &str,
    category: &str,
    w: i32,
    h: i32,
    durations: &[f64],
    transitions: bool,
) -> VideoTemplate {
    let mut track = Track::new(TrackKind::Video);
    track.name = Some("Template video".into());
    let mut slots = Vec::new();
    let mut start = 0.0;
    for (i, &d) in durations.iter().enumerate() {
        let slot = TemplateSlot {
            id: format!("{id}-slot-{}", i + 1),
            name: format!("Shot {}", i + 1),
            target_duration_sec: d,
            ..Default::default()
        };
        let mut clip =
            Clip::new(VideoBody { source_id: slot.id.clone(), src_out_sec: d, ..Default::default() }).at(start, d);
        if transitions && i > 0 {
            clip.transition_in =
                Some(TransitionRef { type_id: "cross-dissolve".into(), duration_sec: 0.35, ..Default::default() });
        }
        track.clips.push(clip);
        slots.push(slot);
        start += d;
    }
    VideoTemplate {
        id: id.into(),
        name: name.into(),
        description: description.into(),
        category: category.into(),
        canvas_width: w,
        canvas_height: h,
        is_built_in: true,
        timeline: Timeline { rate: FrameRate::common(30.0), tracks: vec![track], ..Default::default() },
        slots,
        ..Default::default()
    }
}

/// The templates that ship with Fig.
pub fn built_in_templates() -> Vec<VideoTemplate> {
    vec![
        built_in(
            "clean-opener",
            "Clean opener",
            "A measured three-shot opener for channels and product stories.",
            "YouTube",
            1920,
            1080,
            &[2.5, 2.5, 3.0],
            false,
        ),
        built_in(
            "vertical-recap",
            "Vertical recap",
            "Six quick replacements paced for a short social recap.",
            "Social",
            1080,
            1920,
            &[1.2, 1.2, 1.2, 1.2, 1.2, 1.5],
            false,
        ),
        built_in(
            "product-beats",
            "Product beats",
            "Four deliberate product shots with soft cross-dissolves.",
            "Business",
            1920,
            1080,
            &[2.0, 2.0, 2.0, 2.5],
            true,
        ),
    ]
}

/// The user's templates, one JSON file each.
pub struct TemplateStore {
    root: PathBuf,
}

impl TemplateStore {
    pub fn new(root: impl Into<PathBuf>) -> Self {
        TemplateStore { root: root.into() }
    }

    fn path_for(&self, id: &str) -> PathBuf {
        let safe: String = id.chars().filter(|c| c.is_alphanumeric()).collect();
        self.root.join(format!("{safe}.figtemplate.json"))
    }

    /// Newest first; unreadable files are skipped.
    pub fn list(&self) -> Vec<VideoTemplate> {
        let Ok(entries) = std::fs::read_dir(&self.root) else { return Vec::new() };
        let mut all: Vec<VideoTemplate> = entries
            .flatten()
            .map(|e| e.path())
            .filter(|p| p.to_string_lossy().ends_with(".figtemplate.json"))
            .filter_map(|p| serde_json::from_str(&std::fs::read_to_string(p).ok()?).ok())
            .collect();
        all.sort_by_key(|t| std::cmp::Reverse(t.created_at));
        all
    }

    pub fn load(&self, id: &str) -> Option<VideoTemplate> {
        serde_json::from_str(&std::fs::read_to_string(self.path_for(id)).ok()?).ok()
    }

    pub fn save(&self, template: &VideoTemplate) -> std::io::Result<()> {
        std::fs::create_dir_all(&self.root)?;
        std::fs::write(
            self.path_for(&template.id),
            serde_json::to_string_pretty(template).map_err(std::io::Error::other)?,
        )
    }
}
