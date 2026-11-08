//! Every editable clip property, described once: what it is (its `ParamDef`: kind, range, default, how it reads),
//! which clips have it, where the inspector shows it, how to read and write its base value, and whether it can be
//! keyframed. The inspector builds its rows from these, rendering reads values through `number_at`, and keyframes
//! use `key`, so adding a property, or making one keyable, is one entry in [`all`].

use std::sync::LazyLock;

use super::automation::{self, keys};
use super::fade::{self, FadeOf};
use super::{
    Clip, ClipBody, CropSide, Facet, LayerTransform, ParamDef, ParamKind, ParamValue, Selectable, TextAlign, TextStyle,
};

pub struct ClipProperty {
    pub def: ParamDef,
    /// What a clip must have for this property (`Clip::facets`).
    pub facet: Facet,
    /// The inspector tab it shows in: its facet's when empty, or another's (crop sits under Picture).
    pub tab: Facet,
    /// The group it sits in within its tab ("Compositing", "Crop"...), in the order groups first appear.
    pub group: &'static str,
    /// Animatable: its keyframes (under `key`) override its base value over time. Numbers only.
    pub keyable: bool,
    /// Which clips have it, beyond `facet` (a video clip's own sound fades).
    pub applies_to: fn(&Clip) -> bool,
    /// When it is shown at all for a clip that has it (the box colour only with a box). None: always.
    pub shown: Option<fn(&Clip) -> bool>,
    /// An icon for a toggle shown as an icon button (bold, italic) rather than a labelled checkbox.
    pub icon: Option<&'static str>,
    pub get: fn(&Clip) -> ParamValue,
    pub set: fn(&mut Clip, ParamValue),
}

impl ClipProperty {
    pub fn key(&self) -> &str {
        &self.def.key
    }

    pub fn shown_in(&self) -> Facet {
        if self.tab.is_empty() { self.facet } else { self.tab }
    }

    pub fn has(&self, clip: &Clip) -> bool {
        clip.facets().contains(self.facet) && (self.applies_to)(clip)
    }

    /// Its number at a moment of the clip (seconds from its start): the keyframes there, else the base value.
    pub fn number_at(&self, clip: &Clip, local_t: f64) -> f64 {
        let base = (self.get)(clip).as_number();
        if self.keyable { automation::evaluate_clip(clip, self.key(), local_t, base) } else { base }
    }

    /// Its value at a moment: keyed when it is animated there, else the base value.
    pub fn value_at(&self, clip: &Clip, local_t: f64) -> ParamValue {
        if self.keyable && matches!(self.def.kind, ParamKind::Double | ParamKind::Int) {
            ParamValue::Double(self.number_at(clip, local_t))
        } else {
            (self.get)(clip)
        }
    }

    /// True when it has keyframes on `clip`.
    pub fn is_animated(&self, clip: &Clip) -> bool {
        self.keyable && clip.keyframes.get(self.key()).is_some_and(|k| !k.is_empty())
    }
}

fn number(key: &str, label: &str, min: f64, max: f64, default: f64, format: &str) -> ParamDef {
    ParamDef {
        key: key.into(),
        label: label.into(),
        kind: ParamKind::Double,
        min,
        max,
        default,
        format: format.into(),
        ..Default::default()
    }
}

fn toggle(key: &str, label: &str, default: bool) -> ParamDef {
    ParamDef {
        key: key.into(),
        label: label.into(),
        kind: ParamKind::Bool,
        default: default as u8 as f64,
        ..Default::default()
    }
}

fn colour(key: &str, label: &str, default: u32) -> ParamDef {
    ParamDef {
        key: key.into(),
        label: label.into(),
        kind: ParamKind::Color,
        default: default as f64,
        ..Default::default()
    }
}

fn choice(key: &str, label: &str, choices: &[&str], default: i32) -> ParamDef {
    ParamDef {
        key: key.into(),
        label: label.into(),
        kind: ParamKind::List,
        choices: choices.iter().map(|c| c.to_string()).collect(),
        default: default as f64,
        ..Default::default()
    }
}

fn n(v: f64) -> ParamValue {
    ParamValue::Double(v)
}

fn always(_: &Clip) -> bool {
    true
}

fn is_video(c: &Clip) -> bool {
    matches!(c.body, ClipBody::Video(_))
}

fn has_words(c: &Clip) -> bool {
    c.as_text().is_some_and(|t| !t.words.is_empty())
}

/// A text clip's style (the default for any other clip, which never has a text property).
fn style(c: &Clip) -> TextStyle {
    c.as_text().map(|t| t.style.clone()).unwrap_or_default()
}

fn style_mut(c: &mut Clip) -> Option<&mut TextStyle> {
    c.as_text_mut().map(|t| &mut t.style)
}

fn layout(c: &Clip) -> LayerTransform {
    c.layout().copied().unwrap_or_default()
}

macro_rules! property {
    ($name:ident = $init:expr) => {
        pub static $name: LazyLock<ClipProperty> = LazyLock::new(|| $init);
    };
}

// ---- picture --------------------------------------------------------------------------

property!(
    OPACITY = ClipProperty {
        def: number(keys::OPACITY, "Opacity", 0.0, 1.0, 1.0, "{0:P0}"),
        facet: Facet::PICTURE,
        tab: Facet::empty(),
        group: "Compositing",
        keyable: true,
        applies_to: always,
        shown: None,
        icon: None,
        get: |c| n(c.opacity),
        set: |c, v| c.opacity = v.as_number().clamp(0.0, 1.0),
    }
);

macro_rules! crop_side {
    ($name:ident, $key:expr, $label:expr, $side:expr) => {
        property!(
            $name = ClipProperty {
                def: number($key, $label, 0.0, 0.45, 0.0, "{0:P0}"),
                facet: Facet::CROP,
                tab: Facet::PICTURE,
                group: "Crop",
                keyable: true,
                applies_to: always,
                shown: None,
                icon: None,
                get: |c| n(c.crop($side).unwrap_or(0.0)),
                set: |c, v| {
                    if let Some(side) = c.crop_mut($side) {
                        *side = v.as_number().clamp(0.0, 0.45);
                    }
                },
            }
        );
    };
}

crop_side!(CROP_LEFT, keys::CROP_LEFT, "Left", CropSide::Left);
crop_side!(CROP_RIGHT, keys::CROP_RIGHT, "Right", CropSide::Right);
crop_side!(CROP_TOP, keys::CROP_TOP, "Top", CropSide::Top);
crop_side!(CROP_BOTTOM, keys::CROP_BOTTOM, "Bottom", CropSide::Bottom);

/// The crop property of one side.
pub fn crop(side: CropSide) -> &'static ClipProperty {
    match side {
        CropSide::Left => &CROP_LEFT,
        CropSide::Top => &CROP_TOP,
        CropSide::Right => &CROP_RIGHT,
        CropSide::Bottom => &CROP_BOTTOM,
    }
}

// ---- text -----------------------------------------------------------------------------

macro_rules! text_style {
    ($name:ident, $def:expr, $group:expr, |$s:ident| $get:expr, |$w:ident, $v:ident| $set:expr $(, keyable: $keyable:expr)? $(, shown: $shown:expr)? $(, icon: $icon:expr)?) => {
        property!($name = ClipProperty {
            def: $def,
            facet: Facet::TEXT,
            tab: Facet::empty(),
            group: $group,
            keyable: false $(|| $keyable)?,
            applies_to: always,
            shown: None $(.or(Some($shown)))?,
            icon: None $(.or(Some($icon)))?,
            get: |c| {
                let $s = style(c);
                $get
            },
            set: |c, $v| {
                if let Some($w) = style_mut(c) {
                    $set;
                }
            },
        });
    };
}

text_style!(TEXT_BOLD, toggle("bold", "Bold", true), "Font", |s| ParamValue::Bool(s.bold), |s, v| s.bold = v.as_bool(), icon: "bold");
text_style!(TEXT_ITALIC, toggle("italic", "Italic", false), "Font", |s| ParamValue::Bool(s.italic), |s, v| s.italic = v.as_bool(), icon: "italic");
text_style!(
    TEXT_ALIGNMENT,
    choice("align", "Alignment", &TextAlign::NAMES, TextAlign::Center as i32),
    "Font",
    |s| ParamValue::Choice(s.align as i32),
    |s, v| s.align = TextAlign::from_index(v.as_choice())
);
text_style!(TEXT_SIZE, number(keys::TEXT_SIZE, "Size", 1.0, 40.0, 8.0, "{0:0.#}%"), "Font",
    |s| n(s.size_percent), |s, v| s.size_percent = v.as_number().clamp(1.0, 40.0), keyable: true);
text_style!(TEXT_COLOUR, colour("color", "Colour", 0xFFFFFFFF), "Font", |s| ParamValue::Color(s.color), |s, v| s
    .color =
    v.as_color());
text_style!(TEXT_SHADOW, toggle("shadow", "Drop shadow", true), "Backing", |s| ParamValue::Bool(s.shadow), |s, v| s
    .shadow =
    v.as_bool());
text_style!(
    TEXT_BOX,
    toggle("background", "Background box", false),
    "Backing",
    |s| ParamValue::Bool(s.background),
    |s, v| s.background = v.as_bool()
);
text_style!(TEXT_BOX_COLOUR, colour("backgroundColor", "Box", 0xB0000000), "Backing",
    |s| ParamValue::Color(s.background_color), |s, v| s.background_color = v.as_color(), shown: |c| style(c).background);
text_style!(TEXT_OUTLINE, toggle("outline", "Outline", false), "Backing", |s| ParamValue::Bool(s.outline), |s, v| s
    .outline =
    v.as_bool());
text_style!(TEXT_OUTLINE_COLOUR, colour("outlineColor", "Outline colour", 0xFF000000), "Backing",
    |s| ParamValue::Color(s.outline_color), |s, v| s.outline_color = v.as_color(), shown: |c| style(c).outline);
text_style!(TEXT_OUTLINE_WIDTH, number("outlineWidth", "Outline width", 2.0, 40.0, 12.0, "{0:0}%"), "Backing",
    |s| n(s.outline_percent), |s, v| s.outline_percent = v.as_number().clamp(2.0, 40.0), shown: |c| style(c).outline);
text_style!(
    TEXT_WRAP,
    number("wrap", "Wrap width", 0.0, 100.0, 0.0, "{0:0}%"),
    "Layout",
    |s| n(s.wrap_percent),
    |s, v| s.wrap_percent = v.as_number().clamp(0.0, 100.0)
);
text_style!(
    TEXT_UPPERCASE,
    toggle("uppercase", "All capitals", false),
    "Layout",
    |s| ParamValue::Bool(s.uppercase),
    |s, v| s.uppercase = v.as_bool()
);

property!(
    TEXT_HIGHLIGHT = ClipProperty {
        def: toggle("highlight", "Light the spoken word", false),
        facet: Facet::TEXT,
        tab: Facet::empty(),
        group: "Captions",
        keyable: false,
        applies_to: has_words,
        shown: None,
        icon: None,
        get: |c| ParamValue::Bool(style(c).highlight),
        set: |c, v| {
            if let Some(s) = style_mut(c) {
                s.highlight = v.as_bool();
            }
        },
    }
);

property!(
    TEXT_HIGHLIGHT_COLOUR = ClipProperty {
        def: colour("highlightColor", "Spoken word", 0xFFFFD60A),
        facet: Facet::TEXT,
        tab: Facet::empty(),
        group: "Captions",
        keyable: false,
        applies_to: has_words,
        shown: Some(|c| style(c).highlight),
        icon: None,
        get: |c| ParamValue::Color(style(c).highlight_color),
        set: |c, v| {
            if let Some(s) = style_mut(c) {
                s.highlight_color = v.as_color();
            }
        },
    }
);

property!(
    IS_CAPTION = ClipProperty {
        def: toggle("isCaption", "Include in subtitle export", false),
        facet: Facet::TEXT,
        tab: Facet::empty(),
        group: "Captions",
        keyable: false,
        applies_to: always,
        shown: None,
        icon: None,
        get: |c| ParamValue::Bool(c.as_text().is_some_and(|t| t.is_caption)),
        set: |c, v| {
            if let Some(t) = c.as_text_mut() {
                t.is_caption = v.as_bool();
            }
        },
    }
);

// ---- position (text and still layers) ---------------------------------------------------

macro_rules! layout_part {
    ($name:ident, $key:expr, $label:expr, $min:expr, $max:expr, $default:expr, $format:expr, $field:ident) => {
        property!(
            $name = ClipProperty {
                def: number($key, $label, $min, $max, $default, $format),
                facet: Facet::LAYOUT,
                tab: Facet::empty(),
                group: "Position",
                keyable: true,
                applies_to: always,
                shown: None,
                icon: None,
                get: |c| n(layout(c).$field),
                set: |c, v| {
                    if let Some(l) = c.layout_mut() {
                        l.$field = v.as_number().clamp($min, $max);
                    }
                },
            }
        );
    };
}

layout_part!(LAYOUT_X, "centerX", "Across", 0.0, 1.0, 0.5, "{0:P0}", center_x);
layout_part!(LAYOUT_Y, "centerY", "Down", 0.0, 1.0, 0.5, "{0:P0}", center_y);
layout_part!(LAYOUT_SCALE, "scale", "Scale", 0.1, 4.0, 1.0, "{0:0.00}×", scale);
layout_part!(LAYOUT_ROTATION, "rotation", "Rotation", -180.0, 180.0, 0.0, "{0:0}°", rotation);

/// Reads one part of a layout.
pub type LayoutPart = fn(&LayerTransform) -> f64;

/// The position values, each with the part of a layout it is.
pub fn layout_parts() -> [(&'static ClipProperty, LayoutPart); 4] {
    [
        (&LAYOUT_X, |l| l.center_x),
        (&LAYOUT_Y, |l| l.center_y),
        (&LAYOUT_SCALE, |l| l.scale),
        (&LAYOUT_ROTATION, |l| l.rotation),
    ]
}

/// Where a layer is at a moment of it (seconds from its start): its keyframes there, else its layout.
pub fn layout_at(clip: &Clip, local_t: f64) -> LayerTransform {
    LayerTransform {
        center_x: LAYOUT_X.number_at(clip, local_t),
        center_y: LAYOUT_Y.number_at(clip, local_t),
        scale: LAYOUT_SCALE.number_at(clip, local_t),
        rotation: LAYOUT_ROTATION.number_at(clip, local_t),
    }
}

// ---- sound ----------------------------------------------------------------------------

property!(
    VOLUME = ClipProperty {
        def: number(keys::VOLUME, "Volume", 0.0, 1.0, 1.0, "{0:P0}"),
        facet: Facet::SOUND,
        tab: Facet::empty(),
        group: "Level",
        keyable: true,
        applies_to: always,
        shown: None,
        icon: None,
        get: |c| n(c.volume),
        set: |c, v| c.volume = v.as_number().clamp(0.0, 1.0),
    }
);

property!(
    SOUND_FADE_IN = ClipProperty {
        def: number("soundFadeIn", "Fade in", 0.0, 5.0, 0.0, "{0:0.0} s"),
        facet: Facet::SOUND,
        tab: Facet::empty(),
        group: "Sound fades",
        keyable: false,
        applies_to: is_video,
        shown: None,
        icon: None,
        get: |c| n(fade::get(c, FadeOf::Sound).0),
        set: |c, v| fade::apply_fade_in(c, v.as_number().max(0.0), FadeOf::Sound),
    }
);

property!(
    SOUND_FADE_OUT = ClipProperty {
        def: number("soundFadeOut", "Fade out", 0.0, 5.0, 0.0, "{0:0.0} s"),
        facet: Facet::SOUND,
        tab: Facet::empty(),
        group: "Sound fades",
        keyable: false,
        applies_to: is_video,
        shown: None,
        icon: None,
        get: |c| n(fade::get(c, FadeOf::Sound).1),
        set: |c, v| fade::apply_fade_out(c, v.as_number().max(0.0), FadeOf::Sound),
    }
);

// ---- smoothing (speed) ------------------------------------------------------------------

property!(
    BLEND_FRAMES = ClipProperty {
        def: toggle("blendFrames", "Blend frames in slow motion", false),
        facet: Facet::RETIME,
        tab: Facet::empty(),
        group: "Smoothing",
        keyable: false,
        applies_to: is_video,
        shown: None,
        icon: None,
        get: |c| ParamValue::Bool(c.as_video().is_some_and(|v| v.blend_frames)),
        set: |c, v| {
            if let Some(video) = c.as_video_mut() {
                video.blend_frames = v.as_bool();
            }
        },
    }
);

property!(
    MOTION_BLUR = ClipProperty {
        def: number("motionBlur", "Motion blur", 0.0, 1.0, 0.0, "{0:P0}"),
        facet: Facet::RETIME,
        tab: Facet::empty(),
        group: "Smoothing",
        keyable: false,
        applies_to: is_video,
        shown: None,
        icon: None,
        get: |c| n(c.as_video().map_or(0.0, |v| v.motion_blur)),
        set: |c, v| {
            if let Some(video) = c.as_video_mut() {
                video.motion_blur = v.as_number().clamp(0.0, 1.0);
            }
        },
    }
);

// ---- fades ----------------------------------------------------------------------------

property!(
    FADE_IN = ClipProperty {
        def: number("fadeIn", "Fade in", 0.0, 5.0, 0.0, "{0:0.0} s"),
        facet: Facet::FADES,
        tab: Facet::empty(),
        group: "Fades",
        keyable: false,
        applies_to: always,
        shown: None,
        icon: None,
        get: |c| n(c.fade_in_sec),
        set: |c, v| fade::apply_fade_in(c, v.as_number().max(0.0), FadeOf::Picture),
    }
);

property!(
    FADE_OUT = ClipProperty {
        def: number("fadeOut", "Fade out", 0.0, 5.0, 0.0, "{0:0.0} s"),
        facet: Facet::FADES,
        tab: Facet::empty(),
        group: "Fades",
        keyable: false,
        applies_to: always,
        shown: None,
        icon: None,
        get: |c| n(c.fade_out_sec),
        set: |c, v| fade::apply_fade_out(c, v.as_number().max(0.0), FadeOf::Picture),
    }
);

static ALL: LazyLock<Vec<&'static ClipProperty>> = LazyLock::new(|| {
    vec![
        &TEXT_BOLD,
        &TEXT_ITALIC,
        &TEXT_ALIGNMENT,
        &TEXT_SIZE,
        &TEXT_COLOUR,
        &TEXT_WRAP,
        &TEXT_UPPERCASE,
        &TEXT_SHADOW,
        &TEXT_OUTLINE,
        &TEXT_OUTLINE_COLOUR,
        &TEXT_OUTLINE_WIDTH,
        &TEXT_BOX,
        &TEXT_BOX_COLOUR,
        &TEXT_HIGHLIGHT,
        &TEXT_HIGHLIGHT_COLOUR,
        &IS_CAPTION,
        &OPACITY,
        &CROP_LEFT,
        &CROP_RIGHT,
        &CROP_TOP,
        &CROP_BOTTOM,
        &LAYOUT_X,
        &LAYOUT_Y,
        &LAYOUT_SCALE,
        &LAYOUT_ROTATION,
        &VOLUME,
        &SOUND_FADE_IN,
        &SOUND_FADE_OUT,
        &BLEND_FRAMES,
        &MOTION_BLUR,
        &FADE_IN,
        &FADE_OUT,
    ]
});

/// Every property, in inspector order.
pub fn all() -> &'static [&'static ClipProperty] {
    &ALL
}

/// The property with this key, if there is one.
pub fn find(key: &str) -> Option<&'static ClipProperty> {
    all().iter().copied().find(|p| p.key() == key)
}
