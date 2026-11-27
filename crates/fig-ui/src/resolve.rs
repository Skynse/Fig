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
        [cmd, id, timeline, out] if cmd == "--resolve-media" => {
            (out, videos(id, timeline.parse().unwrap_or(1)).unwrap_or_else(|e| lua_error(&e)))
        }
        [cmd, id, timeline, out, rest @ ..] if cmd == "--resolve-export" && rest.len() <= 1 => {
            let rates = rest.first().map(|r| read_rates(Path::new(r))).unwrap_or_default();
            (out, export(id, timeline.parse().unwrap_or(1), &rates).unwrap_or_else(|e| lua_error(&e)))
        }
        _ => return None,
    };
    Some(if std::fs::write(out, answer).is_ok() { 0 } else { 1 })
}

fn lua_error(e: &str) -> String {
    format!("return {{ error = {} }}\n", lua_str(e))
}

/// A project's timeline `number` (from 1), with its media.
fn load(id: &str, number: usize) -> Result<(fig_project::Project, usize), String> {
    let store = crate::settings::project_store();
    let project = store.load_project(id).map_err(|e| e.to_string())?.ok_or_else(|| format!("No Fig project {id}"))?;
    let index = number.max(1) - 1;
    if index >= project.timelines.len() {
        return Err(format!("\"{}\" has no timeline {number}", project.name));
    }
    Ok((project, index))
}

/// The video files a timeline plays, as a Lua list: the script adds them to Resolve first to learn how Resolve counts
/// their frames.
fn videos(id: &str, number: usize) -> Result<String, String> {
    let (project, index) = load(id, number)?;
    let used: std::collections::HashSet<&str> = project.timelines[index]
        .clips()
        .filter_map(|c| match &c.body {
            ClipBody::Video(v) => Some(v.source_id.as_str()),
            _ => None,
        })
        .collect();
    let paths: Vec<String> = project
        .media
        .iter()
        .filter(|m| m.kind == fig_timeline::MediaKind::Video && used.contains(m.id.as_str()))
        .map(|m| lua_str(&m.url))
        .collect();
    Ok(format!("return {{ {} }}\n", paths.join(", ")))
}

/// How Resolve counts a video's frames: its frame rate and frame count, by file path.
type Rates = std::collections::HashMap<String, (f64, f64)>;

/// The script's answer: one `path<TAB>fps<TAB>frames` line per file.
fn read_rates(path: &Path) -> Rates {
    let text = std::fs::read_to_string(path).unwrap_or_default();
    text.lines()
        .filter_map(|l| {
            let mut parts = l.split('\t');
            let (file, fps, frames) =
                (parts.next()?, parts.next()?.trim().parse().ok()?, parts.next()?.trim().parse().ok()?);
            (fps > 0.0 && frames > 0.0).then(|| (file.to_string(), (fps, frames)))
        })
        .collect()
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
fn export(id: &str, number: usize, rates: &Rates) -> Result<String, String> {
    let (project, index) = load(id, number)?;
    let timeline = &project.timelines[index];
    let name = timeline_name(timeline, index);
    let dir = crate::settings::project_store().project_directory(id).join("resolve");
    let xml = dir.join(format!("timeline-{number}.xml"));
    let (w, h, notes) = write(&xml, &name, timeline, &project.media, rates)?;
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
    rates: &Rates,
) -> Result<(u32, u32, Vec<String>), String> {
    let (w, h) = crate::playback::render::frame_size(timeline, media);
    let dir = xml_path.parent().unwrap_or(Path::new("."));
    std::fs::create_dir_all(dir).map_err(|e| e.to_string())?;
    let stills = dir.join("stills");
    let mut media = sequence_safe(media, &dir.join("images"));
    let timeline = &resolve_time(timeline, &mut media, rates);
    let media = &media;
    let raster = LayerRasterizer::new();
    let mut drawn = 0;
    let mut draw = |clip: &Clip| -> Option<PathBuf> {
        let file = match &clip.body {
            ClipBody::Image(i) => Some(media.iter().find(|m| m.id == i.source_id)?.url.clone()),
            _ => None,
        };
        // halfway in, past any way in
        let frame = raster.rasterize(clip, file.as_deref(), w as usize, h as usize, clip.dur_sec / 2.0)?;
        std::fs::create_dir_all(&stills).ok()?;
        let path = stills.join(format!("still {}.png", letters(drawn)));
        drawn += 1;
        save_png(&frame, &path).ok()?;
        Some(path)
    };
    let effect_name = |id: &str| fig_fx::effects::find(id).map_or_else(|| id.to_string(), |e| e.display_name.clone());
    let out = fig_project::resolve::export(name, timeline, media, (w, h), &mut draw, &effect_name);
    std::fs::write(xml_path, out.xml).map_err(|e| e.to_string())?;
    Ok((w, h, out.notes))
}

/// Resolve reads a picture with a number in its name (`freeze_…_8803.jpg`) as one frame of a numbered sequence, which
/// isn't there, so it shows offline. Those pictures are copied into `dir` under a name of letters.
fn sequence_safe(media: &[MediaAsset], dir: &Path) -> Vec<MediaAsset> {
    let mut media = media.to_vec();
    let mut n = 0;
    for m in media.iter_mut().filter(|m| m.kind == fig_timeline::MediaKind::Image) {
        let path = PathBuf::from(&m.url);
        let (Some(stem), ext) = (path.file_stem(), path.extension()) else { continue };
        let stem = stem.to_string_lossy();
        if !stem.contains(|c: char| c.is_ascii_digit()) {
            continue;
        }
        // the name without its digits, and letters to keep it apart from the others
        let kept: String = stem
            .chars()
            .filter(|c| !c.is_ascii_digit())
            .collect::<String>()
            .trim_matches(['_', '-', ' '])
            .chars()
            .take(40)
            .collect();
        let mut name = std::ffi::OsString::from(format!("{kept} {}", letters(n)));
        n += 1;
        if let Some(ext) = ext {
            name.push(".");
            name.push(ext);
        }
        let copy = dir.join(name);
        let fresh = std::fs::metadata(&copy).is_ok_and(|c| std::fs::metadata(&path).is_ok_and(|o| c.len() == o.len()));
        if fresh || (std::fs::create_dir_all(dir).is_ok() && std::fs::copy(&path, &copy).is_ok()) {
            m.url = copy.to_string_lossy().into_owned();
        }
    }
    media
}

/// Fig shows a video's frames by their timestamps; Resolve counts them at a frame rate of its choosing (a WebM whose
/// timestamps run at 29.974 fps it counts at 30). Over a long file the two drift apart, so every time in a file is
/// moved to where Resolve's counting puts the same frame, and each file gets the length Resolve gives it (a speed
/// ramp's time graph is scaled to that length).
fn resolve_time(timeline: &Timeline, media: &mut [MediaAsset], rates: &Rates) -> Timeline {
    let mut scale = std::collections::HashMap::new();
    let whole_rate = timeline.rate.den() == 1;
    for m in media.iter_mut().filter(|m| m.kind == fig_timeline::MediaKind::Video) {
        let Some(&(fps, frames)) = rates.get(&m.url) else {
            // without Resolve's count (run outside the script), the picture's own length is the closest guess
            if let Some(len) = fig_media::probe::video_length_sec(Path::new(&m.url)) {
                m.duration_sec = len;
            }
            continue;
        };
        let own = m
            .source_rate
            .map(|r| r.fps())
            .or_else(|| fig_media::probe::probe(Path::new(&m.url)).ok().and_then(|p| p.source_rate).map(|r| r.fps()));
        // in a whole-number timeline Resolve plays 29.97 footage as 30 and 23.976 as 24, frame for frame
        let ntsc_twin = (fps * 1.001).round();
        let fps = if whole_rate && (fps * 1.001 - ntsc_twin).abs() < 0.01 && (fps - ntsc_twin).abs() > 0.01 {
            ntsc_twin
        } else {
            fps
        };
        m.duration_sec = frames / fps;
        if let Some(own) = own.filter(|f| *f > 0.0 && (f / fps - 1.0).abs() > 1e-5) {
            scale.insert(m.id.clone(), own / fps);
        }
    }
    let mut timeline = timeline.clone();
    if scale.is_empty() {
        return timeline;
    }
    for clip in timeline.clips_mut() {
        let k = match &clip.body {
            ClipBody::Video(v) => scale.get(&v.source_id),
            _ => None,
        };
        let Some(&k) = k else { continue };
        if let ClipBody::Video(v) = &mut clip.body {
            v.src_in_sec *= k;
            v.src_out_sec *= k;
        }
        clip.speed *= k;
        if let Some(keys) = clip.keyframes.get_mut(fig_timeline::automation::keys::SPEED) {
            for key in keys {
                key.value = fig_timeline::ParamValue::Double(key.value.as_number() * k);
            }
        }
    }
    timeline
}

/// A name of letters only: Resolve reads any run of digits in a picture's name as a frame number.
fn letters(mut n: usize) -> String {
    let mut s = Vec::new();
    loop {
        s.push(b'a' + (n % 26) as u8);
        n /= 26;
        if n == 0 {
            break;
        }
        n -= 1;
    }
    s.reverse();
    String::from_utf8(s).unwrap()
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
