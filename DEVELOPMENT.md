## Architecture

- `Fig.Core` has no UI dependency. How the timeline behaves, how media is decoded and how audio is mixed all live there, covered by unit tests; `Fig.App` is the Avalonia UI on top.
- Playback treats the audio device as the clock. The preview and the playhead follow it, which keeps picture and sound in sync.
- Media artifacts (thumbnails, filmstrips, waveforms) are generated once, cached on disk per project, and repaired when a project is opened.

## Creating a new effect

Write one class. Declare the effect and each parameter (its default and range are written only here), derive from `EffectProcessor`, and read parameters from the `ParamSet` you are given. A value the clip doesn't have yet reads as the declared default, and numbers are held to their range.

```cs
[Effect("crop-with-padding", "Crop with Padding", Icon = "crop",
    Description = "Crop the edges and pad the freed space with a solid color.")]
[EffectParam("left", "Crop left", Default = 0, Min = 0, Max = 0.5)]
[EffectParam("color", "Padding color", Kind = ParamKind.Color, Default = 0xFF000000)]
internal sealed class CropWithPaddingEffect : EffectProcessor
{
    protected override DecodedFrame Render(DecodedFrame frame, ParamSet p, double localT)
    {
        var left = p.Number("left");
        var color = p.Color("color");
        // ... return a new frame (FramePool.Rent(w * h * 4)), or `frame` itself when there is nothing to do
    }
}
```

The catalog finds it on its own. `PixelOps` has the common shapes: `MapColor` for a per-pixel colour change, `Remap` for moving pixels around (flip, mosaic), `Rows` for parallel scanlines. Then port it to a shader (see [GPU rendering](#gpu-rendering)): the CPU class is the reference, and the preview and export draw the shader. In `GpuEffects`, read the parameters the same way, with `EffectCatalog.ParamsOf(typeId, values)`.

## Transitions

The same, deriving from `TransitionBlender` and declaring `[Transition]` / `[TransitionParam]`:

```cs
protected override DecodedFrame Render(DecodedFrame outgoing, DecodedFrame incoming, double t, ParamSet p)
```

`t` runs from 0 (all outgoing) to 1 (all incoming) across the transition, and the two frames are always the same size (the base class handles a mismatch). A transition needs its shader too.

## Effects, keyframes and easing

An effect applies to the **whole clip**. Any numeric parameter can be animated with keyframes (`EffectInstance.Keyframes`, one track per parameter, times in seconds from the clip start). A parameter holds its first keyframe's value before it and its last keyframe's value after it, as in other editors, so zooming in and staying zoomed is just keyframes. Each `KeyframePoint` has an `Easing` (linear, ease in/out, hold) that shapes the movement to the next keyframe (`KeyframeEasingMath`).

Animating is an explicit switch (the stopwatch): `TimelineEditor.SetEffectAnimated` turns it on (one keyframe holding the current look) or off (keyframes removed, the look at the playhead kept). While animated, changes at the playhead become keyframes. Editing goes through `SetEffectKeyframe` / `AddEffectKeyframe` / `MoveEffectKeyframe` / `DuplicateEffectKeyframe` / `RemoveEffectKeyframe` / `SetEffectKeyframeEasing`, which key every parameter of a keyframe together and undo as one step.

`EffectInstance.StartSec` / `DurSec` are an **optional limit** to part of the clip (an effect zone). They are independent of keyframes and off by default.

## Transform and presets

Zoom, pan and rotate are one primitive, the **Transform** effect (`centerX`, `centerY`, `zoom`, `rotation`). Named behaviours ("Zoom in", "Pan", …) are **presets**: plain data (`EffectPreset`) that `EffectPresetInstantiator.Create` turns into a Transform with keyframes. Built-ins live in `BuiltInEffectPresets`; users save their own from a bar's right-click menu (JSON in `~/.fig/presets`, see `EffectPresetStore`). To add a built-in behaviour, add an entry to `BuiltInEffectPresets` — no new effect code is needed.

## Selection and the inspector

Everything on the timeline that can be picked (a clip of any kind, a track, a marker, a transition, an effect bar, a
library file) is an `ISelectable` (`Fig.Core/Timeline/Editing/Selectable.cs`): a `Key`, a `DisplayName` and the
`Facet`s it can be edited as (picture, sound, fades, speed, effects, file, text, track, marker, transition).
`SelectionState` is one ordered list of keys of one group; `TimelineEditor.Resolve` turns a key back into the object.

* **The inspector never asks what kind of thing is selected.** `PropertiesViewModel` ANDs the facets of the selected
  items and `InspectorSections.For` returns one `InspectorSection` per shared facet, in tab order. The section's
  controls are the data template for its type in `EditorView.axaml`, bound to `Host` (the `PropertiesViewModel`).
* **Per-group behaviour is one table**, `SelectionGroups`: how a key finds its object, the status-bar text, and what
  Delete does. `TimelineEditor.DeleteSelection()` and `DescribeSelection()` read it. Do not add `if (selected is X)`
  branches in views; add a row, or a facet.
* **To add a kind of thing** (an image layer, a text layer): implement `ISelectable` (usually by deriving from `Clip`
  and overriding `Facets`), and add an `InspectorSection` for any new facet in `InspectorSections.cs`. Facets with no
  section yet are simply not shown.
* **Keyframes**: the stopwatch records (`TimelineEditor.RecordEffectKeyframe`) and never removes anything. Clearing
  is `SetEffectAnimated(..., false, ...)`, reached from an explicit "Clear" button or menu item.

## The window

`MainWindow` has no system decorations. `TitleBar` draws the brand, the menus (a standard `Menu`, themed in
`Styles/FigTheme.axaml` and `TitleBar.axaml`), the project name, the project actions and the window buttons; `WindowDecorationProperties`
roles make it draggable and give the edges resize grips; anything clickable in the bar (buttons, the `Menu`) has the `User` role, or the bar's `TitleBar` role turns its clicks into window drags. Colours and radii are tokens in `FigTheme.axaml`; use the
token, not a hex value. Menu actions are commands (View menu zoom is `EditorViewModel.TimelineZoomRequested`), so the
bar does not depend on the editor view.

## GPU rendering

Everything between decode and display runs on the GPU by default: effects, transitions, crops, fades and the
layer compositor are fragment shaders (SkSL, through Skia). Export uses the same shaders. Design:
`uml/playback/gpu-render-schema.puml` and `uml/playback/gpu-render-sequence.puml`.

* **A frame is described, then drawn.** Decoding stays on the CPU. What is on screen at a time is described as
  data: a `RenderScene` (`Fig.Core/Media/Rendering`) of layers, each with decoded pictures, their *resolved*
  effects (`EffectCall`: type, parameters with keyframes already sampled, and the time the effect sees), crop,
  transition, track effects and opacity. A renderer then draws it. `CpuSceneRenderer` (Core) uses the effect
  processors, blenders and SIMD compositor and is the reference; `SkiaSceneRenderer` and the preview
  (`GpuScene`, `GpuSceneRenderer`, `GpuEffects` in `Fig.App/Rendering`) use shaders.
* **Shaders are one-to-one ports of the CPU kernels.** `GpuEffects.cs` (colour effects, transform, crop,
  stock transitions), `GpuEffects.Creative.cs` (the creative library and chromatic aberration) and
  `GpuEffects.Graph.cs` (graphs). Match the CPU maths, including its clamping, rounding and integer pixel
  mapping, rather than "improving" one side. `GpuRenderingTests` and `SceneRendererTests` render everything both
  ways and fail when the two drift.
* **Adding an effect or transition means adding its shader.** `Every_builtin_effect_and_transition_runs_on_the_gpu`
  fails until you do: list the id in `CreativeEffectIds` (or the stock sets), add the case in `ApplyCreative`
  and the SkSL in `CreativeSources`. Something without a shader is still drawn (its layer runs on the CPU) but
  that pulls the whole layer off the GPU, which is what the test is there to prevent.
* **Noise has one formula.** SkSL has no integer bit operations, so `CreativeFrameOps.Noise` is a float hash
  (`hash3` in the shaders is the same function). Change both together.
* **Multi-pass effects** (blur, glow, the blur dissolve) use `ShaderScope.Materialize`, which renders a shader
  into an offscreen picture (on the GPU when the canvas is) so a later step can sample it many times. A blur
  loop needs a constant bound, so there is one program per size class (`BlurBounds`).
* **Graphs** run by walking the compiled `GraphPlan` and giving each node a shader instead of a picture
  (`GpuEffects.Graph.cs`). A node output read by more than one node is materialized once. A graph with a
  node that has no shader (`GraphPrimitiveIds`) is drawn on the CPU.
* **Frames are top-down BGRA, canvas-sized, with alpha 0 in letterbox bars.** The CPU compositor ignores
  per-pixel alpha, so the shaders force sources opaque. Crop insets are top-down too.
* **Export on the GPU.** Avalonia only hands out its GPU context on the render thread, so `GpuJobHost` (a one
  pixel control in the main window) runs queued jobs during its draw. `SkiaSceneRenderer` submits up to four
  frames at once so the GPU works while the next one decodes. If the window stops drawing (minimized) or the
  context is lost, that frame is finished in software and the rest of the export uses the CPU renderer.
  `SkiaSceneRenderer.CreateAsync` picks the GPU only when the canvas is really GPU-backed.
* **No GPU.** When the canvas turns out to be software, the preview switches to the SIMD CPU compositor
  (`PreviewViewModel.SetGpuCanvas`), which is faster there; export does the same.
* **Testing on a real GPU.** The automated tests can only use Skia's software backend, which accepts shaders a
  driver may reject. After changing a shader, run the app on a machine with a GPU and check the preview and an export.

## Playback, scrubbing and proxies

**Scrubbing the picture** is two passes (`PreviewViewModel.DecodeWorkerAsync`). A fast pass shows the nearest keyframe at or before the playhead (`PreviewDecodeMode.Scrub`; frames from it report `LastFrameApproximate` and are never cached). When the playhead stops, an exact pass settles on the real frame, and gives way (`AbortRequested`) the moment the playhead moves again. Small steps forward just keep decoding.

**Proxies** are all-keyframe H.264 (`ProjectManager`, `_proxy_intra.mp4`), so any frame decodes alone and scrubbing costs a few ms whatever the source's GOP. Older long-GOP proxies are regenerated. Exports always read the original file.

**Scrub audio** (`PlaybackEngine.Scrub.cs`, `ScrubSynth`) plays the audio under the playhead at the playhead's speed and direction, like a jog wheel (resampled, so pitch follows speed; backwards works). A single step plays a short burst at normal speed. It runs on its own queue and never touches the 1x playback clock. Toggle with Shift+S (`EditorViewModel.AudioScrub`).

**Keyboard transport** (`EditorViewModel.Shuttle.cs`): Space plays at 1x; L / J shuttle forward / backward at 1x, 2x, 4x, 8x (press again to go faster); K stops; Left / Right step a frame (Shift: a second). Any knob or jog controller (TourBox, Stream Deck, ...) can be mapped to these keys, or to Left / Right for continuous jogging, and gets scrub audio for free.

**Ruler and grid**: `RulerCalculator.PickScale` chooses the labelled ticks and the small ticks between them. The small ticks are the snap grid, so what you read is what snaps. The grid only shows through the lanes while dragging.

**Transitions** (`Media/Transitions/Blenders`) are CPU pixel kernels, kept as the reference and the no-GPU fallback; the preview and export draw them with the shaders in `GpuEffects`. To keep a CPU transition cheap: use `PixelOps.LerpBytes` (SIMD blend of a byte range), `PixelOps.ColumnBlend` (same weights on every row: wipe, curtains), and plain `Buffer.BlockCopy` for the parts of a row that are entirely one picture or the other. Only compute per pixel where the picture really changes (the soft band of a wipe, the inside of a swirl).

## Captions

Auto captions (Edit menu, or the library's Text tab) run [Whisper](https://github.com/ggml-org/whisper.cpp) on the CPU through [Whisper.net](https://github.com/sandrohanea/whisper.net), all in `Fig.Core/Captions`.

* **Models are downloaded, never shipped.** `SpeechModels.All` lists the quantized ggml builds (tiny 32 MB, base 60 MB, small 190 MB, large turbo 574 MB) with their Hugging Face URL and SHA-256. The first use downloads to `~/.fig/models/whisper` through a `.part` file, checks the hash and moves it into place. The whisper.cpp runtime itself is about 2.5 MB a platform; `Fig.App.csproj` keeps only the release's own platform.
* **It listens to the mix.** `SpeechTranscriber.MixForSpeech` renders the timeline (or the selected clips' stretch of it) through `AudioMixer` and brings it down to 16 kHz mono, so cuts, speed and muted tracks are heard as they play. Words come back timed on the timeline (whisper.cpp's one-segment-a-word mode); sound tags such as `[Music]` are dropped.
* **Captions are text clips.** `CaptionBuilder.Group` makes cues from words (sentence ends, pauses, a word limit, two balanced lines), and `TimelineEditor.AddCaptions` puts them on a new top track as one undo step. Each clip is a `TextClip` with `IsCaption` (what the SRT/TXT export collects, `TimelineEditor.CaptionCues`) and `Words`, timed from the clip start. A cut shifts the right half's words (`ClipTime.KeepAfter`).
* **The spoken word.** `TextWords` maps the i-th timed word to the i-th whitespace-separated word of the text, so fixing a typo keeps the timing and rewriting the line drops the highlight rather than lighting the wrong word. `TextLayout` makes the lit word a run of its own; the rasterizer's cache key includes it.
* **Editing captions.** The library's Captions tab (`CaptionsPanelViewModel`) lists a captions track's clips to type into; rows are updated in place on each change so the one being typed in keeps its cursor. `TimelineEditor.SetText` rewrites a caption and retimes its words with `TextWords.Retime`: words still there (matched in order) keep their times and new ones share the time between them, so the spoken word stays lit through any edit. `MergeCaptionWithNext`, `SplitCaption` (before the word at the cursor, cut where that word is said) and `RemoveCaption` swap clips with `ReplaceClipsCommand`; `ReplaceInCaptions` is find and replace. Each is one undo step.
* **Styling all captions.** While `EditorViewModel.CaptionStyleToAll` is on (the default, remembered), an inspector edit to a caption's look or position, the font, and a gizmo drag reach every caption on its track (`TimelineEditor.WithCaptionTrackmates`). What each caption says stays its own.
* **Text style.** `TextStyle` has wrap width (a share of the frame width), outline, all capitals and the highlight colour. Each is one entry in `ClipProperties`, so the inspector shows them with no extra code.

## Releasing a major version

`.github/workflows/release.yml` builds a release when a `vN.0.0` tag is pushed:

```bash
git tag v2.0.0 && git push origin v2.0.0
```

It publishes Fig as one file per platform and attaches them to a GitHub release with generated notes and a `SHA256SUMS`:

| File | What it is |
|---|---|
| `Fig-<v>-linux-x64` | The whole app in one executable. On first run it adds itself to the app menu with its icon (for the current user), and rewrites the launcher if the file is moved. `Fig --uninstall` takes it out of the menu. |
| `Fig-<v>-win-x64-setup.exe` | Installer ([Inno Setup](https://jrsoftware.org/isinfo.php), `packaging/windows/Fig.iss`): installs `Fig.exe` for the current user without admin (an admin can pick all users), with a Start menu entry, an optional desktop icon and an uninstaller. |
| `Fig-<v>-win-x64.exe` | The same `Fig.exe`, to run without installing. |

Each file holds the app, the .NET runtime, the native libraries and FFmpeg 8.1 ([BtbN's GPL shared builds](https://github.com/BtbN/FFmpeg-Builds), which include libx264; only avutil, avcodec, avformat, swscale and swresample, which is all Fig calls). The single-file settings are in `src/Fig.App/Fig.App.csproj` and apply to any `dotnet publish` with a runtime: the FFmpeg libraries to bundle are passed as a folder, `-p:FfmpegDir=...`. On first launch .NET unpacks the file (to `~/.net/<name>/` on Linux, `%TEMP%\.net\<name>\` on Windows); Fig deletes older versions' unpacked folders when it starts, and the Windows uninstaller removes its own. The tag's version is stamped into the assemblies. To build any other version, run the workflow by hand from the Actions tab and type the version.

To get a launcher for a build from source, run `packaging/linux/install.sh src/Fig.App/bin/Release/net10.0/Fig.App` (`--uninstall` removes it). The launcher, icon and X11 window class all use the id `io.github.skynse.Fig` (`Program.AppId`), which is how the taskbar matches the window to its icon. The release binary writes the same launcher and icons, embedded from `packaging/linux/share`. The icons are exported from `assets/icon/fig-mono.svg`; re-export them after editing it:

```bash
for s in 16 24 32 48 64 128 256 512; do rsvg-convert -w $s -h $s assets/icon/fig-mono.svg -o packaging/linux/share/icons/hicolor/${s}x${s}/apps/io.github.skynse.Fig.png; done
```

FFmpeg is found in this order: `FFMPEG_ROOT`, then an `ffmpeg/` folder beside the app (where a release unpacks its own), then the system's libraries.
