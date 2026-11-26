use crate::{LayerTransform, TextAlign, TextStyle};

/// A ready-made title in the library's Text tab: what it says, how it looks and where it sits.
#[derive(Clone, Debug, PartialEq)]
pub struct TextPreset {
    pub name: &'static str,
    pub text: &'static str,
    pub style: TextStyle,
    pub layout: LayerTransform,
    pub dur_sec: f64,
}

pub fn text_presets() -> Vec<TextPreset> {
    let d = TextStyle::default();
    let at = |x: f64, y: f64| LayerTransform { center_x: x, center_y: y, ..Default::default() };
    let p = |name, text, style, layout| TextPreset { name, text, style, layout, dur_sec: 3.0 };
    vec![
        p("Title", "Title", d.clone(), LayerTransform::default()),
        p("Subtitle", "Subtitle", TextStyle { bold: false, size_percent: 5.0, ..d.clone() }, at(0.5, 0.62)),
        p(
            "Lower third",
            "Name\nRole",
            TextStyle { size_percent: 4.5, align: TextAlign::Left, background: true, ..d.clone() },
            at(0.24, 0.82),
        ),
        p(
            "Caption",
            "Caption",
            TextStyle { bold: false, size_percent: 4.0, shadow: false, background: true, ..d.clone() },
            at(0.5, 0.9),
        ),
        p("Big statement", "BIG", TextStyle { size_percent: 18.0, ..d.clone() }, LayerTransform::default()),
        p(
            "Credits",
            "Directed by\nYour Name",
            TextStyle { bold: false, italic: true, size_percent: 5.0, ..d },
            LayerTransform::default(),
        ),
    ]
}
