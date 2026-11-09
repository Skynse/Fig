# UI port plan — Rune

The decision is Rune; see `decision.md`. This is the order of work and how each stage is accepted.
Effort assumes one experienced Rust UI engineer (inventory §11 has the per-item S/M/L table);
the C# app stays buildable until parity.

## Ground rules

1. **The Avalonia build is the spec.** `docs/ui-port/inventory.md` for behaviour/metrics,
   `docs/ui-port/reference/` for pixels. No new visual design in the Rust UI.
2. **Tokens only.** Views name `crate::theme` tokens, never hex values. Metrics come from
   `timeline.rs` constants or the inventory, not guesses.
3. **The core stays UI-free.** `fig-timeline` / `fig-edit` / `fig-render` never import UI types;
   `fig-ui` owns windows, input, playback, dialogs and drawing.
4. **Every stage ships a headless snapshot** at 1200×700 (and 1100×700 for the min window), diffed
   against the Avalonia reference. A stage is not done because it compiles.
5. **No stock widgets in the editor.** Avalonia's controls are themed custom; the Rust side needs its
   own `widgets/` for 1:1. Rune Kit is a reference, not a dependency for the editor chrome.

## Phases

### P0 — Shell and layout skeleton (started)

Done in `crates/fig-ui`:

- `theme.rs` — the `FigTheme`/`FigControls`/`EditorTheme` tokens and metrics.
- `shell.rs` — undecorated title bar: brand, File/Edit/View/Help, save status, panel toggles,
  Export, window buttons, window drag.
- `editor.rs` — the 8 px inset, 0.20/·/1/·/0.22 columns, 56/44 rows, splitter gaps, status bar and
  the panel chrome for library/preview/inspector/timeline.
- `timeline.rs` — the timeline canvas: ruler/tick scale, pinned header column, lanes, clips
  (video/audio/text), marker, playhead, exact C# metrics and colours.
- `icons.rs` — Lucide glyphs generated from the C# app's own vendored SVG set.
- `app.rs` — a demo sequence built from `fig_timeline` (`Timeline`, `Track`, `Clip`).

Left for P0: menus (dropdowns + accelerators; must be app-drawn — Rune's native popup menus are
macOS/Windows only), real panel resize by dragging splitters, window persistence, `docs/ui-port/slice-*.png`
for 1100×700. Also decide the Linux edge-resize shim: Rune has no `drag_resize_window` API on Linux,
so either add the small winit-backed API upstream or drive `Tx::resize_window` from the grip drag.

**Accept:** `cargo run -p fig-ui -- --snap out.png --scene editor` produces the editor shell; visual
diff against `reference/editor-1200x700.png` has no structural differences (media thumbnails and
icons are the known gaps).

### P1 — Timeline view complete

- Track header interactions (visibility eye, mute, rack toggle, add/delete track).
- Ruler `RulerCalculator.PickScale` parity, grid only while dragging, snapping guides.
- Filmstrips + waveforms: bounded LRU of tiles from `fig-media::artifacts`, drawn as images; LOD and
  cull rules from inventory §5.2 (sliver below 6 px, no detail below 24 px).
- Effect racks, keyframe lanes, transitions, markers/ranges, fades, speed curve.
- Horizon: `fig-ui` keeps drawing from `AppState`/`EditorState`; all mutations go through
  `fig_edit::TimelineEditor`.

**Accept:** 10k-clip fixture at 60 fps on a real GPU (frame p95 ≤ 8 ms); screenshot diff; idle CPU 0.

### P2 — Timeline interaction

Port the C# gesture contract as explicit state machines (`timeline/gesture.rs`): move (with linked
clips and blocked-spill), trim/ripple/roll/slip/slide, marquee, blade, header buttons, ruler scrub,
track drag, wheel zoom/pan, magnet packing. All drags via `TimelineEditor::begin_live` /
`commit_live` so a drag is one undo step; Escape cancels. Context menus per target (app-drawn on
Linux; Rune's native popup menus are macOS/Windows only). Internal DnD and external file drop both
work; OS drag-out is Rune-macOS-only and stays out of scope for now.

**Accept:** scripted interaction suite (Rune `Runtime` pointer/key) passes the same sequences as the
C# app: select → move → trim → undo; split/ripple/lift; DnD from library; shortcuts.

### P3 — Preview and playback

- Decode demand port (`PreviewViewModel`): fast scrub pass, exact pass on settle, abort on move.
- Frame path: `fig-render` BGRA → `NativeSurface::push_bytes` → draw with `SurfaceFit::Fit` + overlays.
- Playback engine: audio-device clock, play/pause/seek, scrub audio, J/K/L shuttle, loop; `cpal`.
- Overlays: transform gizmo, swirl, fade envelope, layer gizmo, preview overlay (same handle sizes).
- Later: wgpu shader port of the C# SkSL effects, diffed against the CPU reference
  (`GpuRenderingTests` discipline).

**Accept:** 1080p30 playback with audio clock; 4K scrub; overlay screenshot diff; CPU renderer as
the fallback when the GPU is lost.

### P4 — Inspector and library

- `PropertiesViewModel` facets: 11 sections, 5 param templates, keyframe buttons, per-selection tabs.
- Widgets: FigSlider (255 LOC), NumberField (281), segmented tabs, colour picker, text field,
  keyframe diamond, disclosure, chips, tooltips.
- Library: Media grid + search, Templates, Text presets, Transitions, Effects, Markers, Captions.
- Selection/facets shared with the timeline (single selection model).

**Accept:** inspector + library screenshot diffs for no-selection, clip, track, effect, text states.

### P5 — Dialogs, preferences, home, shell polish

ExportDialog, ExportJobsWindow, CaptionsDialog, BookmarkRangesDialog, ShortcutsDialog, Preferences
(modal card over the editor), prompt/confirm, toasts, Home (empty + project grid), loading, menus
with accelerators/toggles, rebindable shortcuts (38 actions), window persistence, desktop
integration, crash log.

**Accept:** one snapshot per screen/state; shortcut rebinding round-trips the same JSON.

### P6 — Graph editor, captions, parity pass

GraphCanvas port (node graph drawing/drag), captions panel, export UI wiring, then an end-to-end
walkthrough against the C# app: every menu item, dialog, shortcut and timeline gesture. Retire
`src/Fig.App` only when a release can be built from `crates/fig-ui`.

## Verification harness

1. **Reference:** the Avalonia headless harness in `docs/ui-port/tools/headless-shots/` regenerates
   `reference/*.png` from the real view models; extend it per state as the port grows.
2. **Rust snapshot:** `cargo run -p fig-ui -- --snap out/<state>.png --scene <name>`; Rune also has
   `--tree` (element tree) and `--trace` (input trace) for structural and interaction checks.
3. **Diff:** a small Rust test crate (or `image` in `fig-ui` tests) compares snapshots. Flat colours
   exact; text AA and icons tolerant; media thumbnails masked with a fixed fixture.
4. **Interaction:** Rune's `Runtime` (`rt.pointer_down/up`, `rt.find`, `rt.frame`) for scripted
   sequences, as Rune's own tests do; assert state after each step plus a snapshot.
5. **Perf:** `fig-ui` benches for the timeline draw list; a 10k-clip scene; startup to first frame;
   idle CPU; note that this dev box is software-GPU only (measure budgets on real hardware).

## Immediate next steps (in order)

1. **State split**: move from the demo `AppState` to `EditorState` with `fig_edit::TimelineEditor`
   and project loading; keep one `view()` per screen.
2. **Timeline interactions P2 start**: hit testing + select/move/trim with live edits; this is the
   single longest item and should start before the inspector.
3. **Preview**: wire `fig-media` decode + `NativeSurface` with a real clip.
4. **Icons**: keep generating `icons.rs` from the vendored Lucide set (the script is recorded in the
   file header; move it to `tools/` when convenient).
5. **CI gate**: `cargo test` + a snapshot-diff job once the harness exists.

## Risks

| Risk | Mitigation |
| --- | --- |
| Rune API churn (pre-1.0) | pin rev; upgrade in one commit with a snapshot diff; local patch if required |
| Text/IME edge cases | Rune's `text_input`/`on_ime` proven in spike; test CJK/emoji composition early |
| Performance on a retained tree | culling + LOD + caches; measured: all-10k-visible builds in ~12 ms, so keep Fig's sliver/LOD rules and a zoom-out clamp; custom shape canvas; perf gate in P1 before building P2 |
| macOS not shipped by Fig | none: targets stay Windows/Linux; Rune's macOS-only player is irrelevant |
| Software-GPU dev box | snapshot tests must run on lavapipe; perf numbers re-measured on hardware |
| C# UI drift | freeze `src/Fig.App` to bug fixes, or accept re-sync cost per screen |
