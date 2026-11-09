# UI framework decision: Rune

**Decision: Rune** (`github.com/anomalyco/rune`, MIT, pinned `dev` rev
`39301f6793266450a057f270da06b048a689be96`). This matches the direction already recorded in
`crates/PORTING.md`; the spikes confirmed it and the port has started in `crates/fig-ui`.

No further framework evaluation. GPUI 2 is not the right fit — reasons below, recorded so the choice
does not get relitigated.

## Why Rune

| Fig requirement | Rune evidence |
| --- | --- |
| Undecorated window, app-drawn title bar, drag region, window controls | Working spike: title-bar drag, app-drawn menus, window buttons, custom chrome (`Chrome`/`Titlebar`, `window_drag`), hit-tested frame on Win32; `out/shell.png` |
| Custom-drawn timeline at scale | Working spike: 10,000 clips / 8 tracks drawn and measured; `out/timeline.png`, `out/timeline_overview.png` |
| Video preview from Fig's CPU renderer | `NativeSurface::push_bytes(ByteFrame { format: Bgra })` → one wgpu texture upload, letterboxed, overlays on top; 1080p and 4K proven; `out/video.png`, `out/video_4k.png` |
| Text input / IME (captions) | `text_input`, `on_ime`, selection; spike `out/text.png`, `out/tree_*.json` |
| Menus, context menus, tooltips, modals, toasts | Native + app-drawn popup menus, modal overlays, tooltips, toasts; `out/shell.png`, `out/tooltip.png`, `out/tree_tooltip.json` |
| File dialogs, clipboard, file drag & drop | `native-file-dialog` (XDG portal on Linux), rich clipboard, `drag_out`, file drop; `out/file_dialog.png`, `out/drop.png` |
| Accessibility | AccessKit tree + platform adapters; `--tree` dump used as evidence |
| Headless verification (the 1:1 gate) | `--snap` PNG, `--tree`, `--trace`, scripted `Runtime` input — the basis of the snapshot-diff harness |
| Windows + Linux, one renderer | wgpu backend on both; Win32 custom frame; Wayland/X11 via winit |
| Widgets/behaviour to build on | `rune-kit` components, semantic tokens, springs; still hand-build editor widgets for 1:1 |
| Licence | MIT, compatible with Fig's GPLv3 |
| Ecosystem fit | The same family of apps (opencode) targets Windows/Linux first, which is Fig's release matrix |

The pin is a single commit ahead of the first spike: `39301f6` fixes a stalled-timer spin on
Wayland (a due `wake_after` frame that a covered surface never draws kept the loop at 100% CPU).
Fig's playback wakes every 16 ms, so that bug would have hit a playing window behind another app;
the fix runs the due timer covered instead of spinning. All gates re-ran green on the new pin.

Rune's own `Player` is macOS-only (AVFoundation). That is **not** a blocker: Fig already decodes with
FFmpeg (`fig-media`) and composites on the CPU (`fig-render`, tiny-skia BGRA), which is exactly the
portable byte-frame path above. Windows additionally has a D3D shared-texture path in Rune.

The spike found three bounded gaps to plan around, none of them a reason to reject Rune
(`target/research/spikes/rune/REPORT.md`):

1. **No edge-resize API on Linux.** Rune's native `Titlebar`/`Chrome` are macOS/Windows; on Linux
   Fig draws its own chrome (as it does in Avalonia) but cannot call `drag_resize_window`. Fix:
   small upstream/winit-backed API, or a `Tx::resize_window` drag shim until it lands. Windows keeps
   native resize edges.
2. **Native popup menus are macOS/Windows only.** Fig draws its own menus anyway for the custom
   look; this is already in P0.
3. **`drag_out` (dragging a file out to the OS) is macOS-only**, and external file *drop* works on
   macOS/Windows/X11 but not Wayland. Fig's in-app drag and X11/Windows file drop are enough for the
   port; OS drag-out and Wayland drop are later items (Wayland portal support to track upstream).
4. **`set_app_icon` is a no-op on Linux**; the shipped `.desktop` file already provides the icon, so
   packaging is unaffected.

Measured on the software-GPU dev box (pessimistic), release build: timeline normal case 0.28 ms p50
idle / 1.50 ms panning for a culled 10k-clip edit; all-10k-visible overview 12 ms idle (the port must
keep Fig's existing LOD/zoom-out clamp there); 1080p BGRA frame push 0.40 ms p50 including submit,
4K 0.49 ms; headless first frame 29.7 ms.

## Why not GPUI 2

GPUI 0.2.2 is mature at Zed, but against Fig's requirements it loses on the two things that matter
most and on table stakes:

- **Preview is worse.** No wgpu/blade texture interop on Linux in the published crate, `Surface` is
  macOS-only, and the documented per-frame path is CPU BGRA → RGBA → `Image` atlas upload. Fig needs
  one upload of an already-composited frame at 30–60 fps; Rune does exactly that.
- **No accessibility in the published crate** (AccessKit absent), a regression from the C# build.
- **Almost no widgets.** Zed's `ui` crate is not on crates.io; Fig's editor is all custom-drawn and
  would still need its own widgets, menus, dialogs and chrome. So the "mature ecosystem" advantage
  does not apply to a 1:1 port of this app.
- **Build friction here**: published crate has a `libc`/`xattr` incompatibility (needs pin), fragile
  Xvfb screenshots, long builds.

egui was already out of the picture for this UI (immediate-mode does not fit a 1:1 retained timeline
with per-frame custom drawing at this scale). Slint/Iced/Xilem/Dioxus were not needed once Rune passed.

## Costs accepted

1. **Pre-1.0 and fast-moving.** Pin the `dev` rev through `Cargo.toml`; upgrade deliberately. The
   repo pins the compiler it needs (`rust-toolchain.toml` = 1.99.1).
2. **Pulled from git, not crates.io.** `Cargo.lock` pins it; a clean build fetches the repo once.
3. **Upstream takes issues, not PRs.** Budget a thin adapter layer if Rune needs a local patch.
4. **No Linux/Windows audio player in Rune.** Fig's playback engine is ours anyway (audio device
   clock, scrub, shuttle); implement it with `cpal` in `fig-ui::playback`.
5. **Software-GPU dev box.** The machine has no `/dev/dri`; all snapshots run on lavapipe and all
   perf figures from the spikes are pessimistic. Frame budgets must be re-measured on a real GPU.

## Evidence

- `docs/ui-port/inventory.md` — the Avalonia spec (1,070 lines) and `reference/` (9 real Avalonia
  headless PNGs) are the 1:1 oracle.
- `target/research/spikes/rune/` — the capability spike and its PNGs/JSON trees/traces (scratch,
  gitignored).
- `docs/ui-port/slice-editor-1200x700.png` — the first Rust render of the real shell + editor layout +
  timeline drawn from `fig_timeline` data, in `crates/fig-ui`.
