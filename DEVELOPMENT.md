## Architecture

Fig is a Cargo workspace. The editing, media and rendering crates have no UI dependency, and the window (`fig-ui`, on [Rune](https://github.com/anomalyco/rune)) sits on top of them.

| Crate | What it holds |
| --- | --- |
| `fig-timeline` | The model: timelines, tracks, clips, effects, keyframes, markers, library media, and the time maths over them. Pure data, saved in the same project format the C# build used. |
| `fig-edit` | The editor that makes every change: undo history, selection, snapping, the main-track magnet, keyframes, effects, transitions, markers, the clipboard. |
| `fig-project` | What is saved and where: the project store, `.figpck` packages, templates, bringing older saves up to date, the OTIO importer. |
| `fig-fx` | Frames and what is done to them: pixel kernels, the effect and transition catalogs, node-graph effects, presets. |
| `fig-render` | Turning a timeline moment into a picture: the scene builder, the compositor, text and stills. |
| `fig-media` | FFmpeg: probing, decoding, filmstrips, proxies, waveforms, reversed copies, the mixer, export. |
| `fig-captions` | Captions from speech with Whisper (whisper.cpp on the CPU): model downloads, listening, timed words. |
| `fig-api` | The control API: what a running editor lets other code read and do, with no transport attached. |
| `fig-mcp` | An MCP server over `fig-api`. The window serves it on a Unix socket; `fig mcp` is the stdio side a client starts. It stays an optional feature (`mcp`). |
| `fig-ui` | The `fig` binary: home screen, editor, timeline, inspector, preview, graph editor, preferences. |

Every change to a timeline goes through `fig-edit`'s `Editor::edit`, which makes it one undo step. The UI never mutates the document directly.

## Building and testing

```bash
cargo run -p fig-ui --release
cargo test --workspace
```

`rust-toolchain.toml` pins the toolchain Rune needs. Linux builds need the FFmpeg development libraries (avutil, avcodec, avformat, swscale, swresample), clang for the FFmpeg bindings, and the ALSA headers for sound (`alsa-lib-devel`). Building without the `audio` feature plays on a wall clock, silently.

Tests live inline, in a `#[cfg(test)] mod tests` at the bottom of the file they test. Test behaviour through the core crates, not through the UI.

## Creating a new effect

Built-in effects are in `crates/fig-fx/src/effects/` (`basic.rs`, `creative.rs`). Each one is an `EffectEntry::builtin(type_id, name, category, icon, description, params, kernel)` in that file's `entries()`, and its kernel is a `fn(&Frame, &ParamSet, f64) -> Option<Frame>` that returns `None` when it leaves the frame as it is. Every parameter declared in `params` is keyframable and shows up in the inspector without UI work. Transitions follow the same pattern in `crates/fig-fx/src/transitions/`.

The `type_id` is saved in project files, so never rename one that has shipped.

## Environment variables

| Variable | Effect |
| --- | --- |
| `FIG_DATA_DIR` | Use another folder for projects, settings and caches (handy for testing on a copy). |
| `FIG_X11=1` | Run through XWayland instead of native Wayland. |
| `FIG_DECORATED=1` | Use the system's window frame instead of Fig's own title bar. |
| `FIG_MCP_SOCKET` | Where the window serves MCP. |
| `FIG_PROFILE`, `FIG_AUDIO_TRACE` | Print frame timings and audio diagnostics. |

## CI and releases

`.github/workflows/ci.yml` builds and tests every push to master and every pull request, and reports clippy's lints without failing on them. Both workflows build against FFmpeg 8.1 from [BtbN's GPL shared builds](https://github.com/BtbN/FFmpeg-Builds) (`ci/ffmpeg.sh`), so what is tested is what ships. `.github/actions/setup` holds the system packages a build needs on Ubuntu.

To release a major version, tag it:

```bash
git tag v2.0.0 && git push origin v2.0.0
```

`.github/workflows/release.yml` stamps the version into `fig-ui` and `fig-mcp`, builds `fig` in release mode and packs it with `packaging/linux/appimage.sh` into `Fig-<version>-linux-x64.AppImage`, which carries the FFmpeg libraries and finds them through its rpath. It then publishes the AppImage and its `SHA256SUMS` as a GitHub release. Run the workflow by hand from the Actions tab to build any other version; leave **publish** off to get the AppImage as a workflow artifact without making a release.
