# UI port — status

`crates/fig-ui` was rebuilt on the core crates (commit "Rebuild fig-ui on the core crates").

## Working

- **Playback**: the audio device is the clock (`playback/audio.rs`, cpal, behind the `audio` feature); the
  preview is composited on its own thread by `fig-render` with every layer, effect, crop, transition and
  title (`playback/render.rs`), pushed to a `NativeSurface`. Scrub (quick frame, then exact), J/K/L shuttle,
  loop, volume, preview quality 270p–1080p. The playhead and timecode move through live values while playing.
- **Timeline**: lanes, headers, filmstrips, waveforms, fades, keyframe diamonds, clip markers, transitions,
  ruler with frame-accurate labels, markers and ranges, beat preview. Gestures: select (linked, Ctrl, Shift,
  marquee), move with snapping and the main-track magnet (hole at the nearest cut, spill to a free track),
  trim, roll (Ctrl on an edge), fades, Ctrl-drag copy, markers and range edges, transition duration,
  keyframe drag/copy, blade, track reorder, gap select and close, scrub, pan, wheel zoom/scroll/height,
  auto-scroll. Context menus for clips, tracks, ruler, empty space, transitions and markers.
- **Inspector**: every clip property from `fig_timeline::properties`, grouped by tab, with sliders, typed
  values, keyframe buttons; text, font, speed and speed curves, fades, effects (add, bypass, params,
  edit graph), track, marker, transition, file and proxy.
- **Library**: media (import, drag onto the timeline, proxies, reveal, remove), templates, text presets,
  transitions, effects and presets, markers, captions.
- **Dialogs**: export (single or marked ranges, background jobs with progress and cancel), auto captions
  (model download, Whisper, grouping), bookmark ranges, shortcuts, prompts; Save As, packages, caption
  export, collect sources. Home (grid/list, search, sort, rename, delete, import .figpck/.otio),
  Preferences (all pages, shortcut recording), graph editor.

## Known gaps

- Sound needs `alsa-lib-devel` installed and `cargo build -p fig-ui --features audio`; without it
  playback runs silently on a wall clock.
- File drops: Wayland gives winit none, so on Linux Fig runs through XWayland (`FIG_WAYLAND=1` opts out).
- Not yet ported: effect racks drawn under clips, dragging speed-curve points on the timeline, the 220 ms
  ripple slide animation, crop/swirl overlays on the preview, timeline-level gesture rebinding.
