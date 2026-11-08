//! Draws a title over a gradient to a PNG, to look at: `cargo run -p fig-render --example title -- out.png`.

use fig_fx::Frame;
use fig_render::layers::LayerRasterizer;
use fig_timeline::{Clip, LayerTransform, TextBody, TextStyle, TimedWord};

fn main() {
    let out = std::env::args().nth(1).unwrap_or_else(|| "title.png".into());
    let (w, h) = (540, 960);
    let style = TextStyle { background: false, ..TextStyle::captions() };
    let mut clip = Clip::new(TextBody {
        text: "Hello there 👋 world, this wraps".into(),
        style,
        layout: LayerTransform { center_y: 0.7, rotation: -4.0, ..Default::default() },
        is_caption: true,
        words: ["Hello", "there", "👋", "world,", "this", "wraps"]
            .iter()
            .enumerate()
            .map(|(i, t)| TimedWord::new(*t, i as f64 * 0.3, i as f64 * 0.3 + 0.25))
            .collect(),
    })
    .at(0.0, 3.0);
    let mut boxed = clip.clone();
    if let Some(t) = boxed.as_text_mut() {
        t.text = "Lower third\nRole".into();
        t.words.clear();
        t.style = TextStyle { size_percent: 4.5, background: true, align: fig_timeline::TextAlign::Left, ..TextStyle::default() };
        t.layout = LayerTransform { center_x: 0.3, center_y: 0.2, ..Default::default() };
    }
    clip.id = "a".into();
    let r = LayerRasterizer::new();
    let mut canvas = Frame::new(w, h);
    for (y, row) in canvas.pixels.chunks_exact_mut(w * 4).enumerate() {
        for p in row.chunks_exact_mut(4) {
            p.copy_from_slice(&[(y * 255 / h) as u8, 90, 40, 255]);
        }
    }
    for c in [&clip, &boxed] {
        let layer = r.rasterize(c, None, w, h, 0.95).unwrap();
        fig_render::blend(&mut canvas, &layer, 1.0, true);
        println!("{:?}", r.measure(c, None, w, h, 0.0));
    }
    image::save_buffer(&out, &canvas.to_rgba(), w as u32, h as u32, image::ColorType::Rgba8).unwrap();
}
