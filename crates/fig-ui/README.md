# fig-ui

Fig's Rust UI, ported 1:1 from the Avalonia app in `src/Fig.App` using
[Rune](https://github.com/anomalyco/rune). The framework decision and roadmap are in
`docs/ui-port/decision.md` and `docs/ui-port/plan.md`; what works today is
`docs/ui-port/status.md`, against the Avalonia spec in `docs/ui-port/inventory.md` and the
reference renders in `docs/ui-port/reference/`.

## Run

The repo pins Rust 1.99.1 (`rust-toolchain.toml`) because the pinned Rune revision needs it.

```bash
source tools/fig-env.sh
cargo run -p fig-ui                                   # windowed (no system frame; app draws it)
cargo run -p fig-ui -- --snap out.png --scene editor  # one headless frame per screen/state
```

The app itself opens on **Home** with no project; a project is opened/created from there. Scenes
are snapshot states only (used by the harness and the windowed gate): `editor`, `editor-selected`,
`editor-context`, `home-empty`, `home-projects`, `preferences`, `export`, `captions`, `bookmarks`,
`shortcuts`, `graph`, `preview-media`, `library-media`, `library-effects`, `library-text`,
`export-jobs`, `toast`.

## Verification

```bash
tools/gen-fixtures.sh      # demo media for preview/filmstrip snapshots (ffmpeg)
tools/render-scenes.sh     # all scenes -> out/scenes/
tools/ui-snapshots.py      # pixel diff vs docs/ui-port/reference/ (media regions masked)
cargo test -p fig-ui       # unit + real-event interaction tests (Rune Runtime)
tools/window-smoke.sh      # launches the real window on Xvfb, clicks/drags/plays, asserts it reacted
tools/real-data-check.sh   # loads the real ~/.fig projects/preferences/workspace/gestures (read-only)
tools/config-write-check.sh # rewrites preferences.json/workspace.json on a copy, asserts C# shape
```

`tools/window-smoke.sh` is the functional gate: it clicks a clip with the system pointer, presses
Space and fails unless the inspector, selection outline, playhead and preview frame changed.

## Layout

- `main.rs` — app spec, window options (borderless), setup/poll/on_key wiring.
- `app.rs` — `AppState`, routing, scenes, project open/new/save, preferences.
- `shell.rs` / `chrome.rs` — title bar, menus, window buttons, edge grips.
- `editor.rs` / `editor_state.rs` — dock layout, splitters, `EditorState` (owns `TimelineEditor`).
- `timeline*.rs` — canvas, geometry, hit testing, gestures, context menus.
- `preview.rs` / `playback.rs` — FFmpeg decode → NativeSurface, transport clock.
- `inspector.rs` / `library.rs` / `widgets.rs` — panels and hand-drawn widgets.
- `home.rs`, `preferences.rs`, `dialogs.rs`, `shortcuts.rs`, `captions.rs`, `graph.rs`, `toasts.rs`.
- `services.rs` — project paths/store.
- `icons.rs` — all 1756 Lucide icons generated from the C# app's vendored SVGs.

Rune is pinned to `anomalyco/rune` dev rev `39301f6793266450a057f270da06b048a689be96` in
`Cargo.toml`; bumping it is a deliberate change, checked against the reference snapshots. This pin
includes the Wayland covered-window stalled-timer fix (no 100% CPU while playing behind another
window).
