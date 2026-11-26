# fig-ui

Fig's editor window, on [Rune](https://github.com/anomalyco/rune). It builds the `fig` binary.

## Run

```bash
cargo run -p fig-ui --release
cargo run -p fig-ui -- --snap out.png --scene editor   # one headless frame of a screen or state
```

The app opens on **Home**, where projects are opened or created. Scenes are snapshot states only: `editor`, `editor-selected`, `editor-context`, `home-empty`, `home-projects`, `preferences`, `export`, `captions`, `bookmarks`, `shortcuts`, `graph`, `preview-media`, `library-media`, `library-effects`, `library-text`, `export-jobs`, `toast`.

## Layout

- `main.rs`, `app.rs`: the app spec, window options, `AppState`, routing, project open, new and save.
- `home.rs`, `preferences.rs`, `settings.rs`, `desktop.rs`: the home screen, preferences, saved settings, the app-menu launcher.
- `editor/`: the dock layout, library, preview and its gizmos, inspector, graph editor, beat markers.
- `timeline/`: drawing, geometry, input gestures, context menus, effect racks, the toolbar.
- `playback/`: the transport clock, frame rendering, sound.
- `commands/`: every command, its default keys, and what it does.
- `dialogs/`: export, captions, jobs.
- `ui/`: the theme, title bar, menus, toasts, Lucide icons and the Fig logo.
- `api.rs`: the `fig-api` backend the MCP plugin drives.

Rune is pinned by git rev in this crate's `Cargo.toml`, and the workspace `Cargo.toml` patches Rune's vendored crates at the same rev. Bump the two together.
