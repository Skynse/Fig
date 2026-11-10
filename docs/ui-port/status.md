# UI port — status

`crates/fig-ui` is the Rune UI on the core crates. Every C# view, control and view model has a Rust counterpart; what
differs is listed under Known gaps.

## Working

- **Playback**: the audio device is the clock (`playback/audio.rs`, cpal, on by default); the preview is composited
  on its own thread by `fig-render` (`playback/render.rs`). Scrub, J/K/L shuttle, loop, volume, 270p–1080p, range
  playback (Preview movement). A notice when there is no sound device.
- **Opening a project** checks its files behind a loading screen (`validate_and_repair`: offline sources, stream
  info, missing thumbnails). Closing or quitting with unsaved changes asks to save, discard or cancel.
- **Timeline**: lanes, filmstrips, waveforms, fades, transitions, markers, beat preview; effect racks under clips and
  tracks (fold per track, drag/resize/carry, stopwatch, power, remove, keyframe lane, box select, menu); speed
  curves drawn over the picture with draggable points; one keyframe gesture and menu for every kind of diamond; the
  ripple slide; gestures.json rebinding of the wheel and middle drag.
- **Preview**: title/still handles, the Transform camera frame with its keyframe path, Swirl handles.
- **Inspector**: every clip property (style edits reach every caption on the track while "Style all captions
  together" is on), text, font, speed with tempo match (Song/Clip BPM), fades, effects with the Transform camera card
  and its keyframe ruler, track, marker (Place keyframe at marker), transition, file.
- **Library**: media, templates (use, save), text, transitions and effects with rendered previews, categories, user
  presets (save from a bar, delete), node effects (new, edit, duplicate, import, export, delete), markers (sections,
  select all, delete, split, row menu), captions (type in place, find/replace, merge, split, delete, track picker).
- **Beats flyout**: analyze, sensitivity, focus on kicks, apply/cancel.
- **Graph editor**: undo/redo/revert, leave prompt, multi and box select, copy/paste/duplicate, bypass, wire picking,
  insert on wire, connection checks, zoom, frame all, Shift+A search, Controls panel, before/after preview.
- **Dialogs**: export (single, or ranges / point-marker sections with per-file names and checks, colour picks,
  invert), auto captions, bookmark ranges, shortcuts, prompts; Home; Preferences.
- **Desktop**: panics go to `~/.fig/logs/fig.log`; a release binary adds itself to the app menu; `fig --uninstall`.

## Known gaps

- File drops: Wayland gives winit 0.30 none. Fig runs natively on Wayland (XWayland crashes NVIDIA on resize);
  `FIG_X11=1` runs it through XWayland when you want to drop files from a file manager.
- Rune can't set the X11 window class, so the taskbar may not match the window to the launcher's icon.
- Tooltips and status messages are reworded in places, not copied word for word.
- Headless testing: `--snap` runs in virtual time (~5× real time), so background work (project checks, analysis)
  needs a later `--at` than it would take on screen.
