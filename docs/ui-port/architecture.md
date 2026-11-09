# Fig UI port — architecture

Status: draft, 2026-11-08. Companion to `inventory.md` (the Avalonia spec) and `decision.md` (framework
choice). This document covers what the Rust UI layer looks like regardless of which UI framework wins.

## 1. What "1:1" means

Parity is measured, not claimed:

1. **Look** — same colour tokens, radii, type sizes, spacing, icon set and layout metrics at the same
   window sizes; same font (Inter, already bundled by the C# app).
2. **Behaviour** — same mouse gestures, keyboard shortcuts, snapping, context menus, drag feedback,
   toasts, dialogs and empty/loading/error states.
3. **Motion** — the same short transitions (the C# app uses ~80 ms brush transitions, no large layout
   animation) rather than new animation the original does not have.
4. **Performance** — 60 fps timeline under load (10k+ clips, zoomed out), 30–60 fps preview with
   overlays, no idle redraws, startup in the same order of magnitude as the Avalonia app.

Pixels are verified with headless PNG snapshots diffed against Avalonia reference screenshots
(§9); behaviour with scripted input tests.

## 2. Layering

The UI crate is a consumer of the already-ported core. The core crates stay UI-free:

```
crates/fig-ui  ──────────────┐
  windows, state, input,      │
  theme, timeline drawing,    ├── fig-edit ── fig-timeline ── fig-project
  preview, widgets            │      └── fig-fx        └── fig-render ── fig-timeline
                              ├── fig-media (FFmpeg decode, mixer, artifacts, export)
                              ├── fig-captions (Whisper)
                              └── fig-render (CPU scene → BGRA frames)
```

`fig-ui` is responsible for everything the C# `Fig.App` does today: `AppViewModel`, `EditorViewModel`,
`PreviewViewModel`, `PropertiesViewModel`, all views/controls, `PlaybackEngine`, media service glue,
dialogs, preferences, desktop integration and crash logging.

## 3. Crate module layout

```
crates/fig-ui/
  src/main.rs              // binary: window creation, app setup, crash handler
  src/app.rs               // AppState, routing (Home | Editor), global shortcuts, lifecycle
  src/theme.rs             // tokens: surfaces, text, accent, danger, radii, type scale, spacing, motion
  src/icons.rs             // Lucide SVG path data -> stroke/fill shapes, tinted, cached
  src/shell/               // undecorated window chrome
    title_bar.rs           // brand, menus, project name, status, window buttons
    menu_bar.rs            // File/Edit/View/Help menus, menu model + rendering
    resize_grips.rs        // 8 edge grips; drag, double-click, aero snap
    toast.rs               // toasts (stack, timing, actions)
    loading.rs
  src/home/                // project browser: recent projects, create/open, templates
  src/editor/
    layout.rs              // split fractions (0.20 / 0.22 / 0.44 defaults), collapse, focus preview
    library/               // Media/Templates/Text/Transitions/Effects/Markers/Captions tabs
    inspector/             // facet sections (PropertiesViewModel), effect/keyframe bars
    preview/               // frame surface + overlays, transport bar
    status.rs              // selection summary, hints, save status
  src/timeline/            // the hard part: ~5.5k LOC of C# partials
    geometry.rs            // constants, track heights, time<->x, lane layout, culling
    draw.rs                // lanes, clips, labels, filmstrips, waveforms, transitions, markers,
                           // keyframes, ruler, playhead, selection, drag feedback
    hit.rs                 // hit testing (clip body/edges/handles, markers, headers, buttons)
    gesture.rs             // drag state machines: move, trim, ripple, roll, slip, slide, marquee,
                           // blade, magnet, track drag, rack, ruler scrub, zoom/pan
    viewport.rs            // scroll/zoom, PixelsPerSecond, ScrollTime, fit
    ruler.rs               // RulerCalculator port: tick scale, labels, snap grid
    context_menu.rs        // clip/track/marker/keyframe/effect menus
    drop.rs                // internal DnD (clips, tracks, library items), external files
  src/playback/            // PlaybackEngine port: audio-device clock, decode demand, scrub audio,
                           // shuttle, loop; frame handoff to preview
  src/preview/             // RenderScene building, frame cache, scrub two-pass decode,
                           // overlays: transform gizmo, swirl, fade envelope, layer gizmo
  src/widgets/             // FigSlider, NumberField, segmented tabs, color picker, text field,
                           // menu, tooltip, keyframe diamond, disclosure, chips
  src/dialogs/             // export, export jobs, captions, bookmark ranges, shortcuts, preferences
  src/graph/               // node graph editor canvas (GraphCanvas port)
  src/services/            // file dialogs, clipboard, DnD formats, desktop integration, crash log,
                           // app paths, preferences service, window persistence
  src/snapshot.rs          // headless screenshot/dev harness hooks
```

Keep each file small; the C# timeline is split the same way (`TimelineView.*.cs`) and that split is
worth preserving because it matches the capability boundaries.

## 4. State model

Retained, keyed tree (both candidate frameworks are retained) with explicit sub-states that mirror
the C# partial classes:

```rust
struct AppState {
    route: Route,                    // Home | Editor
    preferences: Preferences,
    projects: ProjectManager,
    home: HomeState,
    editor: Option<EditorState>,
    toasts: ToastState,
    preferences_page: Option<PreferencesState>,
    shortcuts: ShortcutMap,          // data, rebindable (port of ShortcutCatalog)
}

struct EditorState {
    project: Project,
    editor: TimelineEditor,          // fig-edit: owns undo history, selection, live edits
    timeline: TimelineState,         // viewport, gesture, hover, marquee, context menu, ruler
    transport: TransportState,       // playhead, playing, shuttle, loop, volume
    playback: PlaybackEngine,
    preview: PreviewState,           // decode state, frame cache, overlays, gizmo drag
    library: LibraryState,
    inspector: InspectorState,       // facet sections, expanded bars, param drags
    markers: MarkersState,
    captions: CaptionsState,
    graphs: Option<GraphState>,
    layout: WorkspaceLayout,         // panel fractions/collapse, persisted
    dialogs: DialogState,            // export open/jobs, bookmark ranges, shortcuts
    save: SaveState,                 // dirty/saving/failed, debounce
}
```

Rules:

- **One owner per thing.** `TimelineEditor` owns timeline mutations and undo; views never mutate the
  document directly. `PlaybackEngine` owns the clock; the preview follows it.
- **Explicit gesture state machines** instead of C# event handlers:

  ```rust
  enum Gesture {
      Idle,
      MoveClip { grab_offset: f64, seed_positions: IndexMap<String, f64>, was_playing: bool },
      TrimEdge { clip: String, edge: Edge, anchor: f64 },
      Ripple { clip: String, delta: f64 },
      Roll { left: String, right: String },
      Slip { clip: String, in_at_grab: f64 },
      Slide { clip: String, neighbours: (String, String) },
      Marquee { start: Point, additive: bool },
      Scrub { from_ruler: bool },
      ScrubTimeline { start_time: f64 },
      Splitter { panel: Panel, start: f64, start_frac: f64 },
      DragTrack { track: String, start_y: f64 },
      ExternalDrop { files: Vec<PathBuf>, at: Option<f64> },
  }
  ```

  Drags that touch clips use `TimelineEditor::begin_live` / `commit_live` (fig-edit already has the
  live-edit API), so a drag is one undo step and every intermediate frame is cheap. Escape cancels
  via `cancel_live`; a click without movement coalesces to a select.
- **Stable keys** for retained nodes: `clip:{id}`, `track:{id}`, `param:{effect}/{param}`,
  `marker:{id}`, `panel:library`, …. Stable keys let the framework diff and animate only what changed.
- **No async in the view path.** Long work (decode, save, proxy, filmstrip, Whisper, export) runs on
  workers; results arrive on a channel and wake the UI (`Tx::wake`/`cx.notify`) only when visible
  state changed.

## 5. Timeline

The timeline is the largest and most performance-sensitive piece. Plan:

- **Draw only what is visible.** Iterate tracks, skip lanes outside the viewport, cull clips with
  `x + w < header || x > width`. The C# code already does this; port the culling first and keep it.
- **LOD**: below `ClipDetailWidth` draw a flat sliver only (no corners/shadows/pictures/keyframes) —
  this is what keeps a zoomed-out 10k-clip edit cheap. Port the constant from C#.
- **Caches**: filmstrip tiles and waveform peaks are generated once by `fig-media::artifacts` and
  cached on disk; the UI keeps a bounded in-memory LRU keyed by (clip, width bucket, dpr) and
  decodes tiles lazily on a worker. Never decode media on the UI thread.
- **Text**: labels are UI text with a bounded per-frame string cache; timecode formatting is shared
  with the ruler.
- **Ruler/grid**: port `RulerCalculator::PickScale` exactly — the small ticks are the snap grid, so
  parity here is behavioural, not just visual.
- **Hit testing** is pure geometry (no framework primitives): `timeline/hit.rs` over
  `Viewport` + `TrackLayout`, unit-tested against the same cases as the C# code.
- **Frame budget**: 8 ms for the timeline at 1500 px wide, 20 tracks, 10k clips. Measure with the
  framework's frame profiler (`--profile perf` for Rune; GPUI's frame timing).

## 6. Preview and playback

- **Frame source**: `fig-render` produces a canvas-sized premultiplied BGRA frame (the CPU reference,
  tiny-skia today). The UI uploads it to a GPU texture once per new frame.
  - 1080p30 ≈ 250 MB/s upload; 4K30 ≈ 1 GB/s; fine on real GPUs, but keep a "one upload per frame,
    no copies" rule and reuse buffers.
  - The surface must letterbox against the project shape and draw overlays on top in UI space.
- **Decode demand** ports `PreviewViewModel.DecodeWorkerAsync`: fast scrub pass (nearest keyframe),
  exact pass when the playhead settles, abort on move. Workers use `fig-media::VideoSource`.
- **Playback clock**: port `PlaybackEngine`. The audio device is the clock; the UI reads the clock and
  requests frames; scrub audio and J/K/L shuttle are separate from 1× playback. Rust needs an audio
  output crate (cpal or kira) — to be decided in the plan; SoundFlow has no Rust equivalent.
- **Overlays** (transform gizmo, swirl controls, fade envelope, layer gizmo, preview overlay) are UI
  widgets drawn in the preview rect over the frame, with the same handle sizes and cursors as C#.
- **GPU shaders**: the C# app's SkSL shaders are a later phase. Start with the CPU renderer for
  correctness, then port shaders to wgpu and diff against CPU output (same discipline as
  `GpuRenderingTests`).

## 7. Services

| Service | C# | Rust plan |
| --- | --- | --- |
| File dialogs | Avalonia StorageProvider | framework dialog API or `rfd`; must be async and non-blocking |
| Clipboard | Avalonia clipboard + custom formats | `arboard` + a serialized internal format; validate paste |
| Drag & drop | Avalonia DragDrop + `DragFormats` | framework DnD; internal payloads as serialized IDs |
| App paths | `AppPaths` | `directories`/home resolution, same `~/.fig` layout |
| Preferences | `PreferenceService` JSON | serde JSON, same file/keys, migration |
| Desktop integration | `DesktopIntegration` (.desktop install) | same on Linux; no-op on Windows |
| Crash log | `CrashLog` | panic hook + log file, toast on UI-thread panic |
| Icons | `IconService` (Lucide SVG → geometry) | build-time path extraction from the same SVGs |
| Window state | `WorkspaceLayout` JSON | same fractions and file |
| Toasts | `ToastHost`/`ToastViewModel` | UI widget with same timing/copy |

## 8. Threading

- **UI thread**: state + layout + draw. No blocking I/O, no decoding, no file dialogs awaited.
- **Decode/artifact workers**: preview, filmstrips, waveforms, proxy, export, Whisper. Bounded queues;
  latest-wins for preview (drop stale frames).
- **Audio**: device callback (real-time) → ring buffer; playback clock published atomically; UI polls
  per frame; no locks on the audio path.
- **Save**: debounced on a worker; writes `project.json.partial` then renames (already in the port).
- **Wake-ups**: workers wake the UI only when something visible changes; idle app must not redraw.

## 9. Verification harness

1. **Reference screenshots** from the C# app (Avalonia headless or `xvfb-run`), fixed window sizes
   (1200×700 min, 1600×900, 1920×1080) and fixed project fixture, for each screen/state. Stored in
   `docs/ui-port/reference/`.
2. **Rust snapshots** via the framework's headless renderer at the same sizes/DPR; committed next to
   a manifest of the state used to produce each.
3. **Diff test**: region-aware comparison (exact for flat UI colours, tolerant bounding boxes for text
   AA; mask video frames/thumbnails which depend on media fixtures). Fail on >0.1% mismatched pixels
   outside masked regions.
4. **Interaction tests**: scripted pointer/keyboard events against the headless app; assert state and
   snapshots after each step (select → move → trim → undo, DnD, shortcuts).
5. **Perf gates**: timeline frame time with a 10k-clip fixture, startup time, idle CPU, memory.

## 10. Phases

| Phase | Deliverable | Exit criterion |
| --- | --- | --- |
| P0 | `fig-ui` crate, theme, shell (title bar, menus, window controls, grips), empty editor layout | static screenshot matches C# shell at 1200×700 |
| P1 | Timeline view: lanes, clips, ruler, playhead, zoom/scroll, culling, LOD | 10k clips at 60 fps; screenshot diff passes |
| P2 | Timeline interactions: select, move, trim, ripple/roll/slip/slide, marquee, blade, snap, context menus, DnD | scripted interaction suite passes; undo parity |
| P3 | Preview + playback + transport + overlays | 1080p30 playback with audio clock, scrub, overlays 1:1 |
| P4 | Inspector + library + widgets (sliders, number fields, color picker, keyframes) | inspector screenshot diff; all facets |
| P5 | Dialogs, preferences, home, toasts, shortcuts | each screen diff; rebinding works |
| P6 | Graph editor, captions, export jobs, final parity pass | full app walkthrough; C# app can be retired |

Effort follows the inventory's S/M/L table; the timeline (P1–P2) is roughly a third of the UI work.

## 11. Risks and mitigations

| Risk | Mitigation |
| --- | --- |
| Framework lacks a widget/behaviour (IME, context menus, color picker, native dialogs) | Spike proves the hard ones before committing; build missing widgets in `widgets/` (they are small and shared) |
| Timeline perf on a retained tree | Culling + LOD + per-frame cache; custom canvas/paint API if the framework's element tree is too slow; perf gate in P1 |
| Video frame upload cost / no GPU on some machines | CPU path is the reference (toasts on GPU loss); measure upload; later wgpu renderer |
| Text rendering/AA differences vs Avalonia | same font + same sizes; pixel tolerance for AA only; compare at 1x/2x DPR |
| Rune pre-release churn (if chosen) | pin a `dev` rev, vendor patches, keep the app on a thin `platform` layer to swap |
| No GPU in the dev environment | all screenshot/interaction tests must run on software rendering; perf numbers marked as pessimistic |
| C# app diverges during the port | port is screen-by-screen with reference snapshots; freeze UI work in `src/Fig.App` or accept re-sync cost |

## 12. Open questions for the user

1. Platform priority: Windows + Linux both at parity from day one, or Linux first?
2. Is the C# UI frozen now (bug fixes only), so the Rust port can track a stable target?
3. Keep the GPU shader path (wgpu) in scope for the first Rust release, or ship CPU-only preview first?
4. Any appetite for a component library layer (Rune Kit / gpui-component) versus hand-built widgets
   matching Fig exactly? Hand-built is closer to 1:1; a library is faster but will differ.
