# Headless reference-screenshot harness

Renders the real Fig Avalonia UI off-screen (no X server, no GPU) so the Rust port can be diffed against
byte-exact reference PNGs. This is how `docs/ui-port/reference/*.png` were produced.

```
cd docs/ui-port/tools/headless-shots
dotnet build -c Debug
HOME=/tmp/fig-shots-home ./bin/Debug/net10.0/UiShots
```

It boots `Fig.App.App` under `Avalonia.Headless` + `Avalonia.Skia` with `UseHeadlessDrawing = false`,
creates a real `MainWindow` with an `AppViewModel`, synthesises a demo project in `~/.fig/projects`
(isolated via `HOME`), then saves `MainWindow.CaptureRenderedFrame()` PNGs to
`docs/ui-port/reference/`.

Why not run the app itself: on this machine Avalonia's X11 backend cannot get an EGL surface
(no `/dev/dri`; NVIDIA EGL `drmFreeDevices` double-free under Xvfb; Mesa `ZINK: failed to choose pdev`).
Headless + Skia uses the same composition pipeline, so layout, tokens and typography are exact; the
preview stays black because no media is decoded.

Known-poor states: the export dialog capture uses a bare `ExportDialogViewModel` (no project), so it
shows "Match project"; extend `Program.cs` for more states (graph editor, captions dialog, bookmark
ranges, tooltips, hover states). Not committed to the repo's source tree; safe to delete.
