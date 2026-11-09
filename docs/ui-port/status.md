# UI port — status

Updated at the end of the first implementation round. The framework call is Rune
([decision.md](decision.md)); the plan is [plan.md](plan.md). This page is what is working today in
`crates/fig-ui`, how it is verified, and what remains.

## Existing C# data loads and saves 1:1

The port reads the same `~/.fig` tree the C# build uses, and writes it back in the same shape:

| File | Behaviour |
| --- | --- |
| `~/.fig/projects/*/project.json` | listed on Home (most recently edited first), opened with real media, relative media paths resolved against the project folder, saved back into the same timeline index (other timelines preserved) |
| project thumbnails | rendered from `Thumbnail` (absolute or project-relative) as real images on the cards |
| `~/.fig/preferences.json` | full `Preferences.cs` model: General/Editing/Playback/Export/Rendering/Keyboard/Captions, PascalCase, defaults, clamping, unknown keys preserved, `.bad` file kept on parse errors, `.tmp` + rename writes |
| `~/.fig/config/workspace.json` | panel fractions/collapses load into the editor and persist on splitter/collapse changes |
| `~/.fig/config/gestures.json` | wheel/drag bindings load with `GestureRegistry` defaults, the user's overlay and its navigation migration; the timeline's wheel/zoom follows them |
| `Keyboard.Shortcuts` | user rebindings override the built-ins (e.g. `edit.split` = `B`, `edit.redo` = `Ctrl+Shift+Z`) |
| `~/.fig/templates`, `presets`, `logs`, `models/whisper` | paths provided and created (`AppPaths`); presets/templates browsing is still on the list |

Verified with the real user directory: **`tools/real-data-check.sh`** copies `preferences.json` /
`workspace.json` / `gestures.json`, symlinks the real projects directory read-only, lists Home (real
thumbnails), opens the first project, and asserts the timeline, a decoded frame from the project's
own media, the media library and the saved panel widths (`workspace.json` left = 0.32):

```text
home: first card thumbnail varied pixels = 13805
project: timeline/playhead present
project: decoded preview varied pixels = 76720
project: media library varied pixels = 5548
project: library/preview panel gap at x=388
PASS: existing projects, preferences, workspace and media load 1:1
```

**`tools/config-write-check.sh`** proves the write direction on a copy: toggling Snapping in
Preferences and collapsing a panel rewrites only those values, keeping `SchemaVersion`, the user's
shortcut overrides, Captions and Export settings intact, and writes `workspace.json` in the C#
record shape:

```text
PASS: preferences.json and workspace.json keep the C# shape and values
```

Screenshots: [home-real.png](screens/home-real.png) (the user's projects with their thumbnails) and
[real-project.png](screens/real-project.png) (their project open, with its text clips, media,
decoded monitor and saved panel layout). Nothing under `~/.fig` is written by either check.

## Framework pin

Rune is pinned to `39301f6793266450a057f270da06b048a689be96` (one commit ahead of the spike pin). It
fixes a Wayland stalled-timer spin: a `wake_after` frame a fully covered surface never draws kept
the loop at 100% CPU — exactly what Fig's 16 ms playback wake would trigger with the window behind
another app. The commit touches only `window/*` internals; no API change, and every gate
(tests, snapshots, window input, real data, config write) re-ran green on the new rev.

## Launch behaviour

The app opens on **Home**, exactly like the C# build (`AppViewModel.CurrentView` starts at Home; a
project is opened explicitly). There is **no default project**: with no projects on disk you get
Home's empty state, with projects you get the grid; Home's New Project creates a real project in
`~/.fig/projects` and opens it. The demo sequence the screenshots below use exists only as a
headless snapshot scene (`--scene editor`) and in tests — it is never created by launching the app.

## Working now

**Shell & chrome** — undecorated window (Rune `WindowOptions { borderless: true }`), the 46 px
app-drawn title bar (brand, File/Edit/View/Help, save status, panel toggles, Export, window
buttons), working minimize/maximize/close, window drag, keyboard shortcuts, panel splitters,
collapsed side panels (46 px rail / 44 px strip), timed toasts, and app-drawn menus with
click-away closing. Linux edge grips drive `Tx::resize_window` (the one non-1:1 Linux behaviour;
Rune has no `drag_resize_window` API yet — see the decision doc).

**Editor** — the exact dock layout: 8 px inset, 0.20/·/1/·/0.22 columns, 56/44 rows, 4–8 px
splitters, status bar. Library rail + Media grid (real thumbnails, duration chips, click adds to
the timeline), inspector sections with live sliders (opacity/volume/fades) and facet-driven
sections for clip/track/marker/transition/effect/media, preview panel with transport, and the
timeline canvas with ruler, pinned header column, lanes, clips, filmstrips, markers, playhead,
selection and culling.

**Timeline interaction** — click/shift select, linked-group move with frame snapping, live edits
(one undo step per drag, Escape cancels), trim on either edge, ruler scrub, marquee select, track
visibility/mute toggles, wheel pan and Ctrl-wheel zoom-about-pointer, right-click context menus
with the C# command set.

**Media** — real FFmpeg decode through `fig-media::VideoSource`, the frame uploaded to Rune's
`NativeSurface` (`push_bytes`, BGRA, letterboxed) and drawn in the well; filmstrip thumbnails shared
with the timeline; demo fixtures generated by `tools/gen-fixtures.sh`.

**Projects** — `fig-project::ProjectStore` is wired: Home lists real projects, cards open them, New
Project creates one, Ctrl+S/`Save` commits the live document and writes `project.json` (the same
format the C# build reads).

**Screens** — Home (empty + populated grid, matching the reference layout), Preferences (7-page
modal card with grouped settings and shortcuts page), Export dialog (560×696 reference layout),
Export jobs, Auto captions dialog with the sequence's real caption rows, Bookmark ranges, Keyboard
shortcuts, and the error/shortcut dialogs.

**Graph editor** — node cards with sockets, bezier cables, parameter rows, selection outline,
drag-to-move nodes, node menu and close; maths unit-tested.

## Functional verification (real input, real window)

The first round proved pixels; this one proves behaviour. `tools/window-smoke.sh` launches the app
on an isolated X server, clicks the first video clip with the system pointer, presses Space, and
screenshots the window; `tools/window-smoke.py` then fails unless the inspector changed (selection),
the clip area changed (selection outline), the playhead moved, and the preview frame changed:

```text
inspector changed by 8.3% on click
timeline clip area changed by 0.6% on click
playhead x before=160 after=276
preview changed by 5.1% while playing
PASS: selection, playback and preview all react to real window input
```

`cargo test -p fig-ui` has 14 interaction tests driving Rune's headless `Runtime` with the same
pointer/key events: click-select, drag-move + one-step undo, Space/K transport, tick advancing the
playhead and decoding a frame, File menu + Escape, library card adds a clip, inspector slider edits
opacity, Ctrl+, opens Preferences, Export opens its dialog.

### Bugs this round found and fixed

1. **Handled shortcuts didn't repaint.** `AppSpec::on_key` mutated the state but Rune scheduled no
   frame, so Space/Ctrl+Z/menus-by-key looked dead in the window. `on_key` now calls `tx.instant()`
   and `tx.wake_after` when it handles a key.
2. **A fabricated demo project was opened on launch.** That was a snapshot convenience leaking into
   the product: the app now starts on Home with no project, and the demo sequence only exists in
   headless scenes/tests. Projects opened from the store get a media-backed editor whose preview
   decodes its first frame and follows the playhead while playing.
3. **Clicking a library card silently failed** when the playhead was over an existing clip (the
   insert was rejected); it now falls back to a new track.
4. **Import was a label, not a command.** It opens the native file dialog and imports/probes the
   picked file through `fig-media` into the project media library.
5. **Library tabs were placeholders.** Effects add to the selection, Text presets add text clips,
   Markers and Captions list and select their items.
6. **New projects had no tracks**, so a new project opened blank; they now start with V1 + A1.

Windows for reference: [window-selection.png](screens/window-selection.png),
[window-playback.png](screens/window-playback.png).

## Verification

| Gate | Result |
| --- | --- |
| `cargo test` (timeline, edit, project, fx, fig-ui) | 35 tests, all passing (14 of them interaction tests) |
| `tools/window-smoke.sh` real-window input gate | PASS: launches on Home (no project), then click/selection/playback/preview react |
| `tools/real-data-check.sh` (real `~/.fig`) | PASS: projects, thumbnails, preferences, workspace, media, decoded frame |
| `tools/config-write-check.sh` | PASS: preferences.json/workspace.json written in the C# shape, unknown data preserved |
| `tools/ui-snapshots.py` mean diff vs Avalonia 1:1 renders | editor **4.07** / editor-selected **4.86** / home-empty **4.48** / home-projects **7.86** / preferences **6.72** (out of 255; media regions masked where fixtures differ) |
| Headless snapshots | 17 scenes: `tools/render-scenes.sh` |
| Build | `cargo build -p fig-ui` green; no GPU needed for tests (software Vulkan) |
| Workspace | core crates untouched apart from `Cargo.lock`; no commits made |

Reference renders are in [reference/](reference/); the port's own screenshots are in
[screens/](screens/) (editor, editor-context, editor-selected, home-empty, home-projects,
preferences, preview-media, library-media, graph, captions).

## Remaining (next rounds)

1. **Audio playback** — the transport clock is timer-driven; add the cpal sink behind the `audio`
   feature (needs ALSA headers), audio-device as clock, scrub audio, shuttle sound. The video
   monitor already follows playback frame by frame.
2. **More timeline gestures** — ripple/roll/slip/slide, main-track magnet packing, ghost/blocked
   spill feedback, auto-scroll while dragging, depth keyboard parity, DnD from the library and
   external files.
3. **Filmstrip/waveform caches** — bounded LRU over `fig-media::artifacts` tiles and waveform peaks
   instead of the current per-source thumbnails; waveform drawing on audio clips.
4. **GPU path** — port the SkSL effect/transition shaders to wgpu and diff against the CPU renderer
   (`GpuRenderingTests` discipline), then composite multi-layer scenes in the preview instead of a
   single source.
5. **Export pipeline wiring** — the dialog drives `fig-media::export` and the jobs list gets real
   progress.
6. **Captions end-to-end** — Whisper model download/transcribe from the dialog, inline caption text
   editing in the list, SRT export.
7. **Graph model** — map the UI graph to `fig-fx`'s compiled `GraphPlan`, wire effect bars to the
   graph editor.
8. **Polish** — menu hover/accelerator rendering and keyboard navigation, tooltips, window state
   persistence, colour picker popup, number-field text entry, platform drag-out.
9. **Theming/persistence** — Preferences JSON read/write (`preferences::save` exists), accent theme.
10. **Parity pass** — extend the reference harness with per-screen states and drive the diff gate
    in CI; retire `src/Fig.App` at the end.
