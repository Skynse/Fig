# Rust port

Fig is moving from C#/Avalonia to Rust with [Rune](https://github.com/anomalyco/rune) for the UI. The C# build
stays in `src/` until the Rust build replaces it. Order: `fig-core` first, with tests at parity, then a Rune app.

Project files are shared: `fig-core` reads and writes `project.json` in the C# format, so either build opens a
project the other saved. `tests/project_format.rs` checks this against a file the C# `ProjectStore` wrote.

## fig-core

| C# (`src/Fig.Core`) | Rust (`crates/fig-core`) | State |
| --- | --- | --- |
| `Timeline/Model`, `Timeline/Effects` (data) | `timeline` | done |
| `ClipTime`, `ClipAutomation`, `ClipFade`, `TextWords`, `ClipProperties`, `ClipFactory` | `timeline::{clip_time, automation, fade, text_words, properties, split}` | done |
| `Media/Sources/MediaAsset` | `media` | done |
| `Project` (`Project`, `ProjectStore`, `ProjectPaths`, `Migrations`) | `project` | done |
| `Project/ProjectStore.Package`, `SaveService`, `ProjectManager`, `OtioImporter` | | to do |
| `Timeline/Editing` (`TimelineEditor`, selection, snapping, magnet, `KeyframeHost`) | | to do |
| `Timeline/Commands` (undo history) | | to do |
| `Timeline/Viewport`, `Input` | | to do |
| `Media/Effects`, `Media/Transitions` (catalogs, CPU processors, graph compiler) | | to do |
| `Media/Sources`, `Media/Frames`, `Media/Rendering` (FFmpeg decode, compositing, export) | | to do |
| `Media/Artifacts`, `Media/Analysis` (filmstrips, proxies, reverse, beats) | | to do |
| `Audio`, `Captions` (mixer, scrub, Whisper) | | to do |

Differences from the C# build:

- A clip is one `Clip` struct with a `ClipBody` enum for its kind's own fields, not a class per kind.
- `clone()` keeps ids (undo snapshots); `duplicate()` gives a new one (the C# `Clone()`).
- `ParamValue` reads of the wrong kind return 0/false in release builds and assert in debug builds. The C# build
  threw an exception.
- Saving writes `project.json.partial` and then renames it over `project.json`, so a crash during a save can't
  leave a half-written project.
- Project folder names use the Windows set of reserved characters on every system, so a project folder can be
  copied to any OS.
