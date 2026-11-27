//! Fig in DaVinci Resolve. Resolve's Workspace ▸ Scripts ▸ Fig (the Lua script in `packaging/resolve`, which Fig
//! installs and keeps pointed at itself) runs Fig without a window to list projects and to write a timeline the way
//! Resolve reads it: the XML from `fig_project::resolve`, plus the PNGs its text and moved stills need. Those live in
//! the project's own `resolve` folder, since Resolve links to them for good.

use std::path::{Path, PathBuf};

use fig_render::layers::LayerRasterizer;
use fig_timeline::{Clip, ClipBody, MediaAsset, Timeline};

const SCRIPT: &str = include_str!("../../../packaging/resolve/Fig.lua");

/// What the script runs: `--resolve-projects <out>` and `--resolve-export <project> <timeline, from 1> <out>`. Each
/// answers with a Lua file at `<out>` the script reads back. `--install-resolve-script` puts the script in Resolve's
/// menu. None when the arguments are something else.
pub fn run_cli(args: &[String]) -> Option<i32> {
    if let [cmd] = args
        && cmd == "--install-resolve-script"
    {
        return Some(match install_script() {
            Ok(path) => {
                println!("Installed {}. In Resolve: Workspace > Scripts > Fig", path.display());
                0
            }
            Err(e) => {
                eprintln!("Couldn't install the Resolve importer: {e}");
                1
            }
        });
    }
    let (out, answer) = match args {
        [cmd, out] if cmd == "--resolve-projects" => (out, projects()),
        [cmd, id, timeline, out] if cmd == "--resolve-export" => {
            let answer = export(id, timeline.parse().unwrap_or(1))
                .unwrap_or_else(|e| format!("return {{ error = {} }}\n", lua_str(&e)));
            (out, answer)
        }
        _ => return None,
    };
    Some(if std::fs::write(out, answer).is_ok() { 0 } else { 1 })
}

/// Every project with its timelines' names, most recent first, as a Lua table.
fn projects() -> String {
    let store = crate::settings::project_store();
    let mut lua = String::from("return {\n");
    for summary in store.list_projects() {
        let Ok(Some(project)) = store.load_project(&summary.id) else { continue };
        let timelines: Vec<String> =
            project.timelines.iter().enumerate().map(|(i, t)| lua_str(&timeline_name(t, i))).collect();
        lua += &format!(
            "  {{ id = {}, name = {}, timelines = {{ {} }} }},\n",
            lua_str(&summary.id),
            lua_str(&project.name),
            timelines.join(", ")
        );
    }
    lua + "}\n"
}

/// Writes timeline `number` (from 1) of a project for Resolve and describes it as a Lua table.
fn export(id: &str, number: usize) -> Result<String, String> {
    let store = crate::settings::project_store();
    let project = store.load_project(id).map_err(|e| e.to_string())?.ok_or_else(|| format!("No Fig project {id}"))?;
    let index = number.max(1) - 1;
    let timeline =
        project.timelines.get(index).ok_or_else(|| format!("\"{}\" has no timeline {number}", project.name))?;
    let name = timeline_name(timeline, index);
    let dir = store.project_directory(id).join("resolve");
    let xml = dir.join(format!("timeline-{number}.xml"));
    let (w, h, notes) = write(&xml, &name, timeline, &project.media)?;
    let rate = timeline.rate;
    let fps = if rate.den() == 1 {
        rate.num().to_string()
    } else {
        format!("{:.3}", rate.fps()).trim_end_matches('0').trim_end_matches('.').to_string()
    };
    let notes: Vec<String> = notes.iter().map(|n| lua_str(n)).collect();
    Ok(format!(
        "return {{\n  name = {},\n  timeline = {},\n  xml = {},\n  width = {w},\n  height = {h},\n  fps = {},\n  notes = {{ {} }},\n}}\n",
        lua_str(&project.name),
        lua_str(&name),
        lua_str(&xml.to_string_lossy()),
        lua_str(&fps),
        notes.join(", ")
    ))
}

fn timeline_name(t: &Timeline, index: usize) -> String {
    t.name.clone().filter(|n| !n.trim().is_empty()).unwrap_or_else(|| format!("Timeline {}", index + 1))
}

/// Writes the XML at `xml_path` and the stills beside it. Returns the frame size and what didn't carry over.
fn write(
    xml_path: &Path,
    name: &str,
    timeline: &Timeline,
    media: &[MediaAsset],
) -> Result<(u32, u32, Vec<String>), String> {
    let (w, h) = crate::playback::render::frame_size(timeline, media);
    let dir = xml_path.parent().unwrap_or(Path::new("."));
    std::fs::create_dir_all(dir).map_err(|e| e.to_string())?;
    let stills = dir.join("stills");
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
    Ok((w, h, out.notes))
}

/// A Lua string literal.
fn lua_str(s: &str) -> String {
    let mut out = String::with_capacity(s.len() + 2);
    out.push('"');
    for c in s.chars() {
        match c {
            '\\' => out.push_str("\\\\"),
            '"' => out.push_str("\\\""),
            '\n' => out.push_str("\\n"),
            c if (c as u32) < 32 => out.push_str(&format!("\\{}", c as u32)),
            c => out.push(c),
        }
    }
    out.push('"');
    out
}

/// A premultiplied BGRA frame as a straight-alpha RGBA PNG.
fn save_png(frame: &fig_fx::Frame, path: &Path) -> Result<(), String> {
    let mut rgba = Vec::with_capacity(frame.pixels.len());
    for p in frame.pixels.as_chunks::<4>().0 {
        let a = p[3];
        let un =
            |c: u8| if a == 0 { 0 } else { ((u32::from(c) * 255 + u32::from(a) / 2) / u32::from(a)).min(255) as u8 };
        rgba.extend_from_slice(&[un(p[2]), un(p[1]), un(p[0]), a]);
    }
    image::save_buffer(path, &rgba, frame.width as u32, frame.height as u32, image::ColorType::Rgba8)
        .map_err(|e| e.to_string())
}

// ---------------- the script in Resolve's menu ----------------

/// Where Resolve looks for scripts listed on every page (Workspace ▸ Scripts), for this user.
fn scripts_dir() -> Option<PathBuf> {
    let home = || std::env::var_os("HOME").map(PathBuf::from);
    let dir = if cfg!(target_os = "windows") {
        PathBuf::from(std::env::var_os("APPDATA")?).join(r"Blackmagic Design\DaVinci Resolve\Support\Fusion\Scripts")
    } else if cfg!(target_os = "macos") {
        home()?.join("Library/Application Support/Blackmagic Design/DaVinci Resolve/Fusion/Scripts")
    } else {
        home()?.join(".local/share/DaVinciResolve/Fusion/Scripts")
    };
    Some(dir.join("Utility"))
}

/// This Fig: the AppImage file rather than its temporary mount.
fn fig_path() -> Option<PathBuf> {
    std::env::var_os("APPIMAGE").map(PathBuf::from).or_else(|| std::env::current_exe().ok())
}

fn script_text(fig: &Path) -> String {
    let fig = fig.to_string_lossy();
    // inside a Lua string literal
    SCRIPT.replace("\"@FIG@\"", &lua_str(&fig))
}

/// Help ▸ Install DaVinci Resolve importer. Returns where the script went.
pub fn install_script() -> Result<PathBuf, String> {
    let dir = scripts_dir().ok_or("Couldn't find your home folder")?;
    let fig = fig_path().ok_or("Couldn't tell where Fig is")?;
    std::fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    let path = dir.join("Fig.lua");
    std::fs::write(&path, script_text(&fig)).map_err(|e| e.to_string())?;
    Ok(path)
}

/// At startup: an installed script is brought up to date and pointed at this Fig (an update may have moved it).
/// Resolve being installed is enough to install it the first time. Development builds leave it alone.
pub fn refresh_script() {
    let (Some(dir), Some(fig)) = (scripts_dir(), fig_path()) else { return };
    if !crate::desktop::is_release(&fig) {
        return;
    }
    let path = dir.join("Fig.lua");
    // Resolve makes its Scripts folder on first launch
    if !dir.parent().is_some_and(Path::exists) {
        return;
    }
    let text = script_text(&fig);
    if std::fs::read_to_string(&path).is_ok_and(|now| now == text) {
        return;
    }
    let _ = std::fs::create_dir_all(&dir).and_then(|_| std::fs::write(&path, text));
}
