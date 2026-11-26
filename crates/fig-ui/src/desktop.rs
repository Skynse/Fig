//! What Fig does for itself on the desktop (`CrashLog.cs`, `DesktopIntegration.cs`): a panic is written to
//! `~/.fig/logs/fig.log` with its place in the code, and a release binary puts itself in the app menu (a launcher
//! pointing at the binary wherever it is, and its icons, for this user only), rewriting the launcher when the binary
//! has moved. `fig --uninstall` takes it out again. A build run from `target/` does neither app-menu step.

use std::path::{Path, PathBuf};

pub const APP_ID: &str = "io.github.skynse.Fig";

const LAUNCHER: &str = include_str!("../../../packaging/linux/share/applications/io.github.skynse.Fig.desktop");
const ICONS: [(&str, &[u8]); 8] = [
    ("16x16", include_bytes!("../../../packaging/linux/share/icons/hicolor/16x16/apps/io.github.skynse.Fig.png")),
    ("24x24", include_bytes!("../../../packaging/linux/share/icons/hicolor/24x24/apps/io.github.skynse.Fig.png")),
    ("32x32", include_bytes!("../../../packaging/linux/share/icons/hicolor/32x32/apps/io.github.skynse.Fig.png")),
    ("48x48", include_bytes!("../../../packaging/linux/share/icons/hicolor/48x48/apps/io.github.skynse.Fig.png")),
    ("64x64", include_bytes!("../../../packaging/linux/share/icons/hicolor/64x64/apps/io.github.skynse.Fig.png")),
    ("128x128", include_bytes!("../../../packaging/linux/share/icons/hicolor/128x128/apps/io.github.skynse.Fig.png")),
    ("256x256", include_bytes!("../../../packaging/linux/share/icons/hicolor/256x256/apps/io.github.skynse.Fig.png")),
    ("512x512", include_bytes!("../../../packaging/linux/share/icons/hicolor/512x512/apps/io.github.skynse.Fig.png")),
];

/// Writes every panic to the log, with the thread, the place in the code and a backtrace, then lets the default
/// report run too. Logging never makes things worse: a failure to write is ignored.
pub fn install_crash_log() {
    let previous = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |info| {
        let thread = std::thread::current().name().unwrap_or("unnamed").to_string();
        let place = info.location().map_or_else(String::new, |l| format!(" at {}:{}", l.file(), l.line()));
        let message = info
            .payload()
            .downcast_ref::<&str>()
            .map(|s| s.to_string())
            .or_else(|| info.payload().downcast_ref::<String>().cloned())
            .unwrap_or_else(|| "panic".into());
        let trace = std::backtrace::Backtrace::force_capture();
        let entry = format!("{} [{thread}] {message}{place}\n{trace}\n\n", chrono::Local::now().to_rfc3339());
        let dir = crate::settings::log_dir();
        let _ = std::fs::create_dir_all(&dir);
        if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(dir.join("fig.log")) {
            use std::io::Write;
            let _ = f.write_all(entry.as_bytes());
        }
        previous(info);
    }));
}

fn data_home() -> PathBuf {
    match std::env::var_os("XDG_DATA_HOME").filter(|d| !d.is_empty()) {
        Some(d) => PathBuf::from(d),
        None => std::env::home_dir().unwrap_or_default().join(".local/share"),
    }
}

fn launcher_path() -> PathBuf {
    data_home().join("applications").join(format!("{APP_ID}.desktop"))
}

fn icons_root() -> PathBuf {
    data_home().join("icons/hicolor")
}

/// A build run from cargo's `target/` folder is a development build: it doesn't touch the app menu.
fn is_release(exe: &Path) -> bool {
    !exe.components().any(|c| c.as_os_str() == "target") && !exe.starts_with("/tmp")
}

/// At startup, off the UI thread: adds Fig to the app menu (Linux only). Nothing here may stop Fig from starting.
pub fn on_startup() {
    if !cfg!(target_os = "linux") {
        return;
    }
    // an AppImage runs from a temporary mount: the menu entry must point at the AppImage file itself
    let exe = match std::env::var_os("APPIMAGE") {
        Some(image) => PathBuf::from(image),
        None => match std::env::current_exe() {
            Ok(exe) => exe,
            Err(_) => return,
        },
    };
    if !is_release(&exe) {
        return;
    }
    std::thread::spawn(move || {
        if let Err(e) = add_to_app_menu(&exe) {
            eprintln!("Desktop integration skipped: {e}");
        }
    });
}

fn add_to_app_menu(exe: &Path) -> std::io::Result<()> {
    let launcher = LAUNCHER.replace("@EXEC@", &escape_exec(&exe.to_string_lossy()));
    let path = launcher_path();
    if std::fs::read_to_string(&path).is_ok_and(|now| now == launcher) {
        return Ok(());
    }
    for (size, png) in ICONS {
        let dir = icons_root().join(size).join("apps");
        std::fs::create_dir_all(&dir)?;
        std::fs::write(dir.join(format!("{APP_ID}.png")), png)?;
    }
    if let Some(dir) = path.parent() {
        std::fs::create_dir_all(dir)?;
    }
    std::fs::write(&path, launcher)?;
    refresh();
    Ok(())
}

/// `fig --uninstall`: takes Fig out of the app menu (the binary itself is the user's to delete).
pub fn remove_from_app_menu() {
    let _ = std::fs::remove_file(launcher_path());
    for (size, _) in ICONS {
        let _ = std::fs::remove_file(icons_root().join(size).join("apps").join(format!("{APP_ID}.png")));
    }
    refresh();
    println!("Removed Fig from the app menu. Delete the Fig file itself to finish.");
}

/// A path as the desktop entry spec wants it inside a quoted Exec argument.
fn escape_exec(path: &str) -> String {
    path.replace('\\', "\\\\").replace('"', "\\\"").replace('`', "\\`").replace('$', "\\$").replace('%', "%%")
}

/// Tells the desktop the menu changed (each tool may be missing; the menu catches up on its own).
fn refresh() {
    let apps = launcher_path().parent().map(|p| p.to_string_lossy().into_owned()).unwrap_or_default();
    let icons = icons_root().to_string_lossy().into_owned();
    for (tool, args) in [
        ("update-desktop-database", vec!["-q", apps.as_str()]),
        ("gtk-update-icon-cache", vec!["-q", "-t", icons.as_str()]),
        ("kbuildsycoca6", vec![]),
    ] {
        let _ = std::process::Command::new(tool)
            .args(args)
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null())
            .status();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn exec_paths_are_escaped_for_desktop_entries() {
        assert_eq!(escape_exec("/home/a b/100%/$x\"y"), "/home/a b/100%%/\\$x\\\"y");
    }
}
