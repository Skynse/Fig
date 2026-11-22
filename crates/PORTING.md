# Rust port

Fig is moving from C#/Avalonia to Rust with [Rune](https://github.com/anomalyco/rune) for the UI. The C# build
stays in `src/` until the Rust build replaces it. `Fig.Core` was ported first, as the crates below, then the Rune app
(`fig-ui`) on top; what the UI still lacks is in `docs/ui-port/status.md`.

Project files are shared: `fig-project` reads and writes `project.json` in the C# format, so either build opens a
project the other saved. Its tests check this against a file the C# `ProjectStore` wrote
(`crates/fig-project/tests/fixtures/csharp_project`).

## Fig.Core

| C# (`src/Fig.Core`) | Rust | State |
| --- | --- | --- |
| `Timeline/Model`, `Timeline/Effects` (data) | `fig-timeline` | done |
| `ClipTime`, `ClipAutomation`, `ClipFade`, `TextWords`, `ClipProperties`, `ClipFactory` | `fig-timeline::{clip_time, automation, fade, text_words, properties, split}` | done |
| `Media/Sources/MediaAsset` | `fig-timeline::asset` | done |
| `Project` (`Project`, `ProjectStore`, `ProjectPaths`, `Migrations`) | `fig-project::{store, paths, migrations}` | done |
| `Project/ProjectStore.Package`, `SaveService`, `ProjectManager`, `OtioImporter` | `fig-project::{package, store, otio}`; autosave in `fig-ui` | done |
| `Timeline/Editing` (`TimelineEditor`, selection, snapping, magnet, `KeyframeHost`) | `fig-edit` | done |
| `Timeline/Commands` (undo history) | `fig-edit::history` (timeline snapshots, not command classes) | done |
| `Timeline/Viewport`, `Input` | `fig-ui::timeline` | done |
| `Media/Effects`, `Media/Transitions` (catalogs, CPU processors, graph compiler) | `fig-fx` | done |
| `Media/Sources`, `Media/Frames`, `Media/Rendering` (FFmpeg decode, compositing, export) | `fig-media::{sources, video, export, encode}`, `fig-render` | done |
| `Media/Artifacts`, `Media/Analysis` (filmstrips, proxies, reverse, beats) | `fig-media::{artifacts, reverse, beats, library}` | done |
| `Audio`, `Captions` (mixer, scrub, Whisper) | `fig-media::{audio, mixer}`, `fig-captions` | done |

Differences from the C# build:

- A clip is one `Clip` struct with a `ClipBody` enum for its kind's own fields, not a class per kind.
- `clone()` keeps ids (undo snapshots); `duplicate()` gives a new one (the C# `Clone()`).
- `ParamValue` reads of the wrong kind return 0/false in release builds and assert in debug builds. The C# build
  threw an exception.
- Saving writes `project.json.partial` and then renames it over `project.json`, so a crash during a save can't
  leave a half-written project.
- Project folder names use the Windows set of reserved characters on every system, so a project folder can be
  copied to any OS.
