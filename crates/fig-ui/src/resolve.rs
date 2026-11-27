//! Writing a Resolve export to disk: the XML from `fig_project::resolve`, the PNGs its text and moved stills need
//! (in a "<name> stills" folder beside it), and a notes file when something didn't carry over.

use std::path::{Path, PathBuf};

use fig_render::layers::LayerRasterizer;
use fig_timeline::{Clip, ClipBody, MediaAsset, Timeline};

/// Writes the export at `xml_path`. Returns how many notes there were and where they went.
pub fn write(xml_path: &Path, name: &str, timeline: &Timeline, media: &[MediaAsset]) -> Result<(usize, PathBuf), String> {
    let (w, h) = crate::playback::render::frame_size(timeline, media);
    let stem = xml_path.file_stem().map_or_else(|| name.to_string(), |s| s.to_string_lossy().into_owned());
    let dir = xml_path.parent().unwrap_or(Path::new("."));
    let stills = dir.join(format!("{stem} stills"));
    let raster = LayerRasterizer::new();
    let mut draw = |clip: &Clip| -> Option<PathBuf> {
        let file = match &clip.body {
            ClipBody::Image(i) => Some(media.iter().find(|m| m.id == i.source_id)?.url.clone()),
            _ => None,
        };
        // halfway in, past any way in
        let frame = raster.rasterize(clip, file.as_deref(), w as usize, h as usize, clip.dur_sec / 2.0)?;
        std::fs::create_dir_all(&stills).ok()?;
        let path = stills.join(format!("{}.png", clip.id));
        save_png(&frame, &path).ok()?;
        Some(path)
    };
    let effect_name = |id: &str| fig_fx::effects::find(id).map_or_else(|| id.to_string(), |e| e.display_name.clone());
    let out = fig_project::resolve::export(name, timeline, media, (w, h), &mut draw, &effect_name);
    std::fs::write(xml_path, out.xml).map_err(|e| e.to_string())?;
    let notes = dir.join(format!("{stem} - not carried over.txt"));
    if !out.notes.is_empty() {
        let body = format!(
            "What Fig couldn't hand to Resolve in \"{name}\":\n\n{}\n",
            out.notes.iter().map(|n| format!("- {n}")).collect::<Vec<_>>().join("\n")
        );
        std::fs::write(&notes, body).map_err(|e| e.to_string())?;
    }
    Ok((out.notes.len(), notes))
}

/// A premultiplied BGRA frame as a straight-alpha RGBA PNG.
fn save_png(frame: &fig_fx::Frame, path: &Path) -> Result<(), String> {
    let mut rgba = Vec::with_capacity(frame.pixels.len());
    for p in frame.pixels.as_chunks::<4>().0 {
        let a = p[3];
        let un = |c: u8| if a == 0 { 0 } else { ((u32::from(c) * 255 + u32::from(a) / 2) / u32::from(a)).min(255) as u8 };
        rgba.extend_from_slice(&[un(p[2]), un(p[1]), un(p[0]), a]);
    }
    image::save_buffer(path, &rgba, frame.width as u32, frame.height as u32, image::ColorType::Rgba8).map_err(|e| e.to_string())
}
