# Fig — Software Requirements Specification

**Document status:** Draft
**Project:** Fig — open-source non-linear video editor (C# / Avalonia / FFmpeg)
**Audience:** Developers, maintainers, and future contributors

---

## 1. Introduction

### 1.1 Purpose
This document defines the complete set of functional and non-functional requirements for Fig, an open-source non-linear video editor. It covers the currently implemented behavior of the software as well as planned capabilities, so the project stays understandable and extensible as it grows.

### 1.2 Scope
Fig is a desktop video editor for one or more media tracks, with:
- a timeline editing engine (cut, trim, move, ripple, undo),
- linked audio/video editing,
- live preview with synchronous audio-driven playback,
- a compositor for stacked video tracks, fades, effects, and transitions,
- a full timeline export pipeline (video + audio),
- editorial annotations (markers), and
- interoperability with OpenTimelineIO (OTIO).

Out of scope for the current build: collaborative editing, cloud storage, mobile/tablet targets, and AI-powered features (tracked as roadmap items, §5).

### 1.3 Definitions and acronyms
| Term | Meaning |
| --- | --- |
| **Clip** | A segment of a media asset placed on a track (video, audio, or text). |
| **Track** | A named lane on the timeline holding clips of one kind (video or audio). |
| **Linked clip** | Clips sharing a link group (linked by the user); they move/trim/cut/delete together. |
| **Embedded audio** | A video's own sound, played and edited as part of its video clip and drawn as a strip under the picture, until it is detached to an audio clip of its own. |
| **Timeline** | The ordered set of tracks and clips that constitute an edit. |
| **Playhead** | The current position on the timeline. |
| **Frame rate (FPS)** | The playback rate expressed as a rational number (e.g., 24000/1001). |
| **Project frame rate** | The timeline's frame rate: the frame grid for snapping, timecode and frame steps, and the export rate. Clips play in real time whatever their own rate. |
| **Proxy** | A low-resolution re-encode of a source used for smooth scrubbing/playback. |
| **OTIO** | OpenTimelineIO, an open interchange format for editorial timelines. |
| **Filmstrip** | A horizontally tiled sprite-sheet of aspect-correct frames for a clip. |
| **CRF** | Constant Rate Factor; a quality-based H.264 encoding control (lower = better). |

### 1.4 References
- `README.md` — project overview, features, and goals.
- `uml/*.puml` — architecture diagrams (data model, editing commands, media pipeline, playback, gestures, persistence, OTIO import, export).
- Source: `src/Fig.Core` (domain engine), `src/Fig.App` (Avalonia UI), `tests/Fig.Core.Tests`.

### 1.5 Document conventions
**Requirement IDs.** Functional requirements are `FR-nnn`; non-functional are `NFR-nnn`. IDs are stable once assigned and are never reused.

**Priority.**
- **P0** — required for the current milestone / core functionality.
- **P1** — important; should be implemented soon.
- **P2** — desirable; lower urgency / future.

**Status.** Matches the README legend:
- X **Complete** — implemented, tested, and in the current build.
- O **Partial** — a working foundation exists; behavior/integration is incomplete.
- - **Not started** — planned but not implemented.

---

## 2. Overall description

### 2.1 Product perspective
Fig is architected as a clean UI-free core (`Fig.Core`) surrounded by an Avalonia desktop shell (`Fig.App`):

```
src/Fig.Core/   Timeline engine, media pipeline, audio mixing, gestures, persistence, OTIO import, export
src/Fig.App/    Views, view models, playback device, export jobs UI, SVG icons
tests/          Unit tests over Fig.Core
```

The core owns all timeline behavior, media decoding, compositing, audio mixing, and export, and has no UI dependency. The UI layer binds to it, so the engine is fully unit-testable.

### 2.2 Product functions (summary)
1. Create, open, save, validate, and organize projects and media.
2. Edit a multi-track timeline with full undo/redo.
3. Preview composited video and audio synchronously.
4. Apply effects, fades, and between-clip transitions.
5. Mark, annotate, and enable/disable clips.
6. Import OTIO projects and preserve editorial metadata.
7. Export the timeline to an MP4 (H.264/AAC).

### 2.3 User characteristics
The primary user is a solo creator (hobbyist to semi-professional) who edits short-to-medium projects and wants a fast, keyboard-first, free tool. Secondary users are contributors to the codebase who value a testable, well-documented core.

### 2.4 Operating environment
- Windows, macOS, or Linux (Avalonia desktop).
- .NET SDK 10.0 or later.
- FFmpeg native libraries (libavformat/libavcodec/libswscale/libswresample) for the target platform.
- An audio output device (PulseAudio/PipeWire/ALSA on Linux) for playback.

### 2.5 Design and implementation constraints
- `Fig.Core` MUST NOT reference UI or platform APIs.
- All timeline mutations MUST be undoable command objects through a bounded command history.
- Media decoding MUST be driven from a dedicated worker thread; native decoders MUST only be disposed on that thread (never the UI thread).
- Playback MUST use the audio device as the authoritative clock.
- Project files are JSON; media is referenced in place (never embedded).

---

## 3. Functional requirements

### 3.1 Project and session management
| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-001 | The application SHALL allow creating a new named project. | P0 | X |
| FR-002 | The application SHALL list existing projects on the home screen and open one on selection. | P0 | X |
| FR-003 | The application SHALL save a project on demand and via a throttled autosave, with atomic writes and a rolling set of backups. | P0 | X |
| FR-004 | The application SHALL validate a project on open: detect offline sources, re-probe stream metadata, and regenerate missing/stale derived artifacts, reporting what was repaired. | P0 | X |
| FR-005 | The application SHALL support Save As (renaming/moving the project). | P0 | X |
| FR-006 | The application SHALL allow deleting a project from the home screen. | P0 | X |
| FR-007 | Closing an unsaved project SHALL prompt the user to save, discard, or cancel. | P0 | X |

### 3.2 Media library and import
| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-010 | The application SHALL import media (video, audio, image) via a file picker and via drag-and-drop, without freezing the UI. | P0 | X |
| FR-011 | Import SHALL probe the source for duration, dimensions, frame rate, and presence of audio. | P0 | X |
| FR-012 | Import SHALL deduplicate media by content hash. | P0 | X |
| FR-013 | The application SHALL detect offline sources and support relinking them. | P0 | X |
| FR-014 | The application SHALL generate a thumbnail card for imported video. | P0 | X |
| FR-015 | The application SHALL generate aspect-correct filmstrip tiles for video clips. | P0 | X |
| FR-016 | The application SHALL generate sample-accurate waveform peaks for media with audio. | P0 | X |
| FR-017 | The application SHALL generate a lightweight H.264 proxy for large sources and use it for preview playback. | P0 | X |
| FR-018 | The application SHALL allow removing media from the project. | P0 | X |
| FR-019 | Derived artifacts (filmstrip, waveform, proxy) SHALL be generated in the background and appear incrementally. | P0 | X |

### 3.3 Timeline editing
| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-030 | The timeline SHALL support multiple video and audio tracks with add/remove/select. | P0 | X |
| FR-031 | Dropping a video with audio SHALL create one video clip that plays its sound (embedded audio), drawn as a waveform strip under the picture. | P0 | X |
| FR-031a | "Detach audio" on a video clip SHALL move its sound to an unlinked audio clip on a free audio track (same range, speed, level, sound fades and volume keyframes) and silence the video clip, as one undo step. | P0 | X |
| FR-031b | Opening a project saved before embedded audio SHALL fold each video's linked audio clip with the same source and timing into the video clip; other pairs SHALL keep playing as before. | P0 | X |
| FR-032 | Clips SHALL be draggable, including across tracks, with overlap rejection. | P0 | X |
| FR-033 | Clips SHALL be resizable/trimmable from either edge. | P0 | X |
| FR-034 | The application SHALL split a clip at the playhead. | P0 | X |
| FR-035 | The application SHALL ripple-delete selected clips. | P0 | X |
| FR-036 | The application SHALL ripple-insert a clip, pushing later clips right. | P0 | X |
| FR-037 | The application SHALL overwrite-insert a clip, splitting any overlapped clip. | P0 | X |
| FR-038 | The application SHALL lift selected clips, leaving gaps. | P0 | X |
| FR-039 | Every edit SHALL be undoable/redoable through a bounded command history; continuous property edits (opacity, crop, volume, marker drags, transition resizes) SHALL coalesce into a single undo step. | P0 | X |
| FR-040 | The application SHALL snap to the frame grid, and SHALL support magnetic snapping to nearby clip boundaries. | P0 | X |
| FR-041 | The user SHALL be able to disable a clip so it is ignored by playback, mixing, compositing, and transitions. | P0 | X |
| FR-042 | Linked video+audio clips SHALL move, trim, cut, and ripple together. | P0 | X |
| FR-043 | Clips SHALL play in real time whatever their source frame rate: a clip is as long as its media, and its source time advances by its speed alone. An empty timeline whose rate the user hasn't chosen SHALL adopt the first dropped media's frame rate, snapped to the nearest standard rate; an implausible reported rate (outside 10–120 fps and no standard rate within 1%) SHALL be ignored. | P1 | X |
| FR-043a | The user SHALL be able to set the project frame rate (23.976, 24, 25, 29.97, 30, 50, 59.94, 60 fps) from the timeline header as one undoable step; clips keep their place and length. A project saved when clips were retimed to the timeline rate SHALL open with that factor moved into each clip's speed, so it plays as before. | P1 | X |
| FR-043b | Import SHALL measure a file whose container reports no duration (such as a recorded WebM) from its last packet. | P1 | X |

### 3.4 Timeline rendering
| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-050 | Video clips SHALL render aspect-correct filmstrip thumbnails tiled across the clip. | P0 | X |
| FR-051 | Audio clips SHALL render waveforms from decoded peaks in real time. | P0 | X |
| FR-052 | Fade ramps SHALL be rendered on clips and be draggable. | P0 | X |
| FR-052a | A video clip's picture and its embedded audio SHALL fade independently: the picture's fades on the picture, the sound's on its strip and on the Audio tab. | P0 | X |
| FR-053 | Timeline and track markers SHALL be rendered (ruler blocks, lane diamonds). | P0 | X |
| FR-054 | Cut transitions SHALL be rendered as a badge and an overlap span on both clips. | P0 | X |
| FR-055 | Disabled clips SHALL be visually dimmed. | P0 | X |
| FR-056 | The workspace (library, preview, timeline) SHALL be resizable via draggable splitters. | P0 | X |
| FR-057 | Tracks SHALL be tall enough for comfortable clip inspection. | P1 | X |

### 3.5 Preview and playback
| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-060 | The preview SHALL support multiple decode resolutions (270p–1080p). | P0 | X |
| FR-061 | Playback SHALL use the audio device as the master clock; the timeline playhead and video preview SHALL follow it. | P0 | X |
| FR-062 | Scrubbing SHALL decode on a dedicated worker thread with a bounded LRU frame cache. | P0 | X |
| FR-063 | During playback, the worker SHALL pre-decode frames ahead into the cache and skip stale requests. | P0 | X |
| FR-064 | The preview canvas SHALL follow the project's dominant video aspect, and mismatched media SHALL be letterboxed (never stretched). | P0 | X |
| FR-065 | Decoders SHALL only be disposed on the decode worker thread (scale changes, proxy swaps, and teardown must never race an in-flight decode). | P0 | X |
| FR-066 | Transport controls (play/pause, step frame, jump to start) SHALL be available. | P0 | X |

### 3.6 Audio
| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-070 | The mixer SHALL sum audible clips (audio clips and video clips playing embedded audio) into interleaved stereo at 48 kHz, honoring track mute, clip volume, and fade envelopes. | P0 | X |
| FR-071 | Clip speed ≠ 1 SHALL resample the decoded audio so pitch and duration match the timeline (no truncation). | P0 | X |
| FR-072 | Per-clip speed editing SHALL be exposed in the UI. | P1 | X |
| FR-072a | A video clip SHALL take an optional speed curve (a velocity edit, the Vegas model): eased keyframes of speed (0.1×–8×) in seconds from the clip start. The clip SHALL keep its place and length; the curve decides how much footage it uses (the area under the curve). Preview, export, the mixer (pitch follows the curve), cuts, trims, the filmstrip, the waveform and beat analysis SHALL all map time through the curve, and a cut through a ramp SHALL leave both halves starting on the right frame. | P1 | X |
| FR-072b | The speed curve SHALL be drawn over the clip's picture on a log scale (1× in the middle) and edited there: drag a point (up faster, down slower; sideways it snaps), double-click the line to add a point, double-click a point for 1×, right-click a point for its easing or to delete it; each drag SHALL be one undo step. Where the curve uses up the file before the clip ends, the clip SHALL show "out of footage" there and hold the last frame. | P1 | X |
| FR-072c | A video clip SHALL offer frame smoothing for its speed, in preview playback and export: "Blend frames" mixes the two real frames around each moment where the clip plays slower than its footage, and "Motion blur" (0–100 %) averages the frames each output frame passes where it plays faster. Scrubbing SHALL show plain frames. | P1 | X |

### 3.7 Effects and transitions
| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-080 | The application SHALL ship a catalog of built-in clip effects (brightness, contrast, saturation, hue, invert, sepia, vignette, sharpen, pixelate, flip, posterize, tint) applied as an ordered, toggleable stack. | P0 | X |
| FR-081 | Effects SHALL be applicable to and removable from a selected clip. | P0 | X |
| FR-082 | Video preview and export SHALL run each clip's enabled effect stack before compositing. | P0 | X |
| FR-083 | The application SHALL ship a transition catalog (cross-dissolve, wipe, slide, push, fade-to-black, iris, curtain) applied across abutting cuts. | P0 | X |
| FR-084 | A transition SHALL be applicable by dragging a catalog entry onto a cut. | P0 | X |
| FR-085 | Transitions SHALL be selectable, resizable (drag or slider), and removable, all undoable. | P0 | X |
| FR-086 | The transition catalog SHALL keep growing (wipes, irises, slide, etc.). | P1 | X |
| FR-086a | A transition SHALL join any mix of video clips, still images and titles at a cut; a still or title holds its picture for as long as the transition needs and keeps its transparency, so tracks below show through it during the blend. | P1 | X |
| FR-087 | Effect and transition parameters SHALL be typed (Double, Int, Bool, Color, List) with schema-defined ranges, defaults, and choices, and SHALL persist (legacy bare-number params keep loading). | P0 | X |
| FR-088 | The properties panel SHALL render each effect's parameters as controls generated from its schema (slider, checkbox, color, list) that write through the timeline editor with undo. | P0 | X |
| FR-089 | The applied effect stack SHALL be visible on the selected clip with per-effect enable/disable and remove controls; adding an effect SHALL be a single self-describing class with no separate registration. | P0 | X |
| FR-090 | Effect parameters SHALL support keyframes (add at playhead, step, clear) evaluated at render time; keyframes SHALL render as diamonds on the timeline and drive the preview/export. | P1 | X |

### 3.8 Markers and annotations
| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-091 | The user SHALL add a marker at the playhead, attached to the selected clip, active track, or timeline. | P0 | X |
| FR-092 | Markers SHALL be selectable and draggable in time (clip markers clamp to the clip range). | P0 | X |
| FR-093 | Markers SHALL be renameable and recolorable via the properties panel. | P0 | X |
| FR-094 | Markers SHALL be deletable (Delete key / context menu / panel). | P0 | X |
| FR-095 | Markers SHALL render on clips, tracks, and the timeline ruler. | P0 | X |
| FR-096 | Markers SHALL be multi-selectable (Ctrl/Cmd toggles, Shift selects a range, Ctrl/Cmd+A selects all visible); deleting a selection SHALL be one undo step, and Delete/Escape in the Markers panel SHALL act on markers only while markers are selected. | P0 | X |
| FR-097 | Range markers (a start and a duration) SHALL be creatable at the playhead or between two bookmarks, drawn as bars on the ruler, editable by dragging, and undoable. | P1 | X |
| FR-098 | Video SHALL be splittable at the selected markers (a range marker contributes its start) as one undo step, with embedded and linked audio following. | P1 | X |
| FR-099 | The Markers panel SHALL list sequence and clip markers with search, selection, rename, delete, and per-clip clearing of generated markers. | P1 | X |

### 3.9 Interoperability
| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-100 | The application SHALL import OpenTimelineIO `.otio` projects into the native project model. | P0 | X |
| FR-101 | OTIO import SHALL preserve markers, per-object metadata, global start time, media available ranges, and clip enable state. | P0 | X |

> **Note:** OTIO is used as a test/interchange vehicle — export small kdenlive projects to OTIO and open them in Fig to exercise capabilities. FCPXML/EDL and OTIO export are not requirements.

### 3.10 Export
| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-110 | The application SHALL export the full timeline to MP4 (H.264 video + AAC audio) at the timeline frame rate, honoring effects, transitions, fades, speed, and layering. | P0 | X |
| FR-110a | Export SHALL run at the timeline's exact frame rate (a 29.97 project exports at 30000/1001, never rounded to 30). | P0 | X |
| FR-110b | Export SHALL encode H.264 on the GPU when it can: Automatic tries NVIDIA NVENC, Intel Quick Sync, AMD AMF, then VA-API, and uses the first that opens, else libx264 on the CPU; the user MAY pick one, and one that isn't available SHALL fall back to the CPU. The quality setting SHALL map to each encoder's constant-quality control, and the export list SHALL show which encoder ran. | P1 | X |
| FR-111 | An export dialog SHALL let the user choose resolution (presets or custom), quality (CRF), and output path. | P0 | X |
| FR-112 | Exports SHALL run as background jobs with live progress visible in a jobs popup; failures SHALL be reported and partial output removed. | P0 | X |
| FR-113 | The software encoder SHALL use all available cores and expose Fast/Medium/Best quality presets. | P1 | O |
| FR-114 | Export SHALL support hardware encoders (NVENC/VAAPI/QSV/VideoToolbox) with a software fallback. | P1 | - |
| FR-115 | For timelines with no effects/transitions/speed changes, a "fast export" SHALL re-encode only around edit points and stream-copy the rest. | P2 | - |
| FR-116 | The application SHALL export each included range marker as a separate file, with an editable output name per range, using the same encode path as a full export. | P1 | X |

### 3.11 Input and configuration
| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-120 | Timeline input SHALL be resolved through a data-driven gesture registry rather than hardcoded in the view. | P0 | X |
| FR-121 | Gesture bindings SHALL be stored as editable configuration. | P0 | X |
| FR-122 | Core keyboard shortcuts SHALL exist for common actions (undo/redo, split, ripple delete, add/delete marker, enable/disable clip, save, save-as). | P0 | X |

### 3.12 Templates
| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-130 | The application SHALL expose Templates as a primary editor-library destination with search and category filtering. | P1 | O |
| FR-131 | A template SHALL persist a timeline blueprint plus typed, named media replacement slots. | P1 | X |
| FR-132 | The user SHALL be able to save the current timeline as a local reusable template. | P1 | X |
| FR-133 | Applying a template SHALL validate required media and compatible media kind, treat slot duration as authored timeline timing rather than an import requirement, create fresh timeline/track/clip identities, and preserve effects, transitions, markers, and automation. | P1 | O |
| FR-134 | The template workflow SHALL provide explicit per-slot media assignment, preview, trim/fill behavior, and a non-destructive choice of new sequence or new project. | P1 | - |
| FR-135 | Templates SHALL support author-exposed text, color, font, and optional media variables. | P2 | - |
| FR-136 | Templates SHALL be importable/exportable as portable packages with dependency and licensing metadata. | P2 | - |

### 3.13 Beat markers
| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-150 | The application SHALL analyze the selected audio clip's source range for beats and kick-like bass attacks in the background without blocking the UI, honoring the clip's trim and speed. | P1 | X |
| FR-151 | Analysis SHALL run once per request; changing sensitivity or kick focus SHALL re-select markers from the cached analysis without re-decoding the audio. | P1 | X |
| FR-152 | Candidate markers SHALL be previewed on the clip before they are committed, outside the document (no undo entry, autosave, or change notification per adjustment), with the clip's existing generated markers hidden while previewed. | P1 | O |
| FR-153 | Apply SHALL replace the clip's previously generated beat markers with the previewed set as one undo step and leave manually placed markers alone; Cancel SHALL leave the document untouched. | P1 | X |
| FR-154 | A pending preview SHALL be discarded, with an explanation, when the analyzed clip is deleted, retimed, or trimmed, or when the project changes. | P1 | O |
| FR-155 | Generated markers SHALL be tagged so they can be cleared separately from manual markers. | P1 | X |

### 3.14 Effect graphs
| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-160 | A user SHALL be able to build an effect as a directed acyclic graph of primitive nodes with typed image/mask ports and typed node parameters. | P2 | X |
| FR-161 | Primitives SHALL be self-describing classes discovered like effects, declaring their ports and parameter schemas; existing stock effects SHALL be usable as nodes. | P2 | X |
| FR-162 | A saved graph SHALL appear in the effect catalog as a first-class effect that stacks, toggles, keyframes, and takes presets exactly like a built-in. | P2 | X |
| FR-163 | The author SHALL promote chosen node parameters as the effect's controls, with optional range and default overrides. | P2 | X |
| FR-164 | A graph SHALL be validated on edit and on save (port types, cycles, unconnected inputs); an invalid graph SHALL fail safe (input frame unchanged) and report errors on the offending nodes. | P2 | X |
| FR-165 | Graph evaluation SHALL be a pure function of the input frame, parameters, and clip time (no feedback in the first version) so preview, scrubbing, and export match. | P2 | X |
| FR-166 | A graph made only of shader-capable primitives SHALL run on the GPU as shaders (one draw per frame; a result read by several nodes is rendered once and shared); any other graph SHALL fall back to the CPU path. | P2 | X |
| FR-167 | Graphs SHALL persist with a schema version and be importable/exportable as portable files. | P2 | X |
| FR-168 | A node-graph editor SHALL support add, connect, promote, and undo with a live preview. | P2 | O |

Design: `uml/media/effects-graph-schema.puml`, `uml/media/effects-graph-pipeline.puml`. FR-168 is partial: wiring, pinning, undo and the live preview are covered by tests; the Add menu, right-click menus and quick search have not been exercised by tests yet.

### 3.15 Effect bars on the timeline
| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-170 | Effect bars SHALL be selectable one at a time and as a group: Shift-click or Ctrl-click adds or removes a bar, and a box dragged from empty space selects every clip and effect bar it touches. | P0 | X |
| FR-171 | Dragging one bar of a group SHALL move every selected bar by the same amount, keyframes included and spacing preserved, as one undo step. | P0 | X |
| FR-172 | Delete SHALL remove every selected effect bar as one undo step. | P0 | X |


### 3.16 GPU rendering
| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-180 | The preview SHALL be composited by the GPU by default: effects, transitions, crops, fades and layer blending run as shaders, and nothing is composited on the CPU when the canvas is GPU-backed. | P0 | X |
| FR-181 | Every built-in effect and every built-in transition SHALL have a shader. A test fails when one is added without. | P0 | X |
| FR-182 | Every shader SHALL match the CPU kernel it ports (the reference) to within rounding, checked per effect and transition, including time-varying and noise-based ones. | P0 | X |
| FR-183 | Export SHALL render its frames on the GPU by default, from the same scene description the CPU renderer draws, with several frames in flight. When there is no GPU, or the GPU stops answering mid-export, the CPU renderer SHALL finish the export. | P0 | X |
| FR-184 | A layer holding something without a shader SHALL be drawn on the CPU and still appear; it SHALL NOT be dropped. | P0 | X |

Design: `uml/playback/gpu-render-schema.puml`, `uml/playback/gpu-render-sequence.puml`. On a machine without a GPU-backed canvas the CPU compositor is used, because Skia's software shaders are slower than the SIMD kernels. The automated tests run on Skia's software backend only, so a real GPU driver is not exercised by them.

### 3.17 Selection and inspector
| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-190 | Clips (video, audio, text), tracks, markers, transitions, effect bars and library media SHALL all be selectable things that declare a name and the facets they can be edited as. Nothing else in the app SHALL need to ask which kind of thing is selected. | P0 | X |
| FR-191 | The selection SHALL be one ordered list that can hold any mix of clips, effect bars, markers, transitions and tracks. A plain click SHALL replace it; Ctrl/Shift-click SHALL add to it; Ctrl+A SHALL select every clip, effect bar and marker (every marker, when only markers are selected). Delete SHALL remove everything selected as one undo step, and dragging a clip SHALL carry the selected effect bars (and dragging a track's effect bar the selected clips) as one undo step. | P0 | X |
| FR-192 | The inspector SHALL show one tab per facet that every selected item has, icon-only, and keep the open tab while values are edited. | P0 | X |
| FR-193 | Delete and the status bar text SHALL come from the selected things, through one table of per-group behaviour. | P0 | X |
| FR-194 | A rack row (level, fades) SHALL be able to open its tab by facet. | P1 | X |
| FR-194a | Trimming an edge that touches another clip SHALL move the cut between them (a rolling edit): the neighbour's edge follows, taking more or less of its own footage, and no clip moves; the neighbour keeps at least 0.1 s and can't run past its file. An edge with a gap after it SHALL trim up to the next clip and no further. The trim SHALL be one undo step. | P0 | X |
| FR-194b | Moving clips SHALL leave a faint outline where they came from; where they can't go (another clip is there) a red ghost SHALL show the spot while they wait where they last fit. | P1 | X |
| FR-194i | Dragging clips over another track of the same kind SHALL move them onto it, at the time the pointer puts them, when there is room there (several selected clips from one track move together; a linked partner of the other kind stays on its track and moves in time); otherwise they wait where they last fit and the red ghost shows the spot on that track. Undo SHALL put them back on their own track. | P0 | X |
| FR-194c | Any drag on the timeline (clips, trims, effect bars, keyframes, speed points, markers, the playhead, a selection box, media dragged in) held near an edge SHALL scroll the timeline that way, faster nearer the edge, and the drag SHALL follow. | P1 | X |
| FR-194d | Effects SHALL live on tracks, never on clips. The selected effect bars SHALL copy (Ctrl+C, or Copy on a bar's menu) and paste at the playhead (Ctrl+V when effects were copied last) keeping their spacing and keyframes, each back on the track it came from, or on the chosen track of the same kind when they all came from one track; one undo step, the pasted bars selected. | P1 | X |
| FR-194e | A video track's header SHALL have a mute button for its clips' own sound, beside the show/hide button. | P1 | X |
| FR-194f | Video tracks SHALL stack as the timeline shows them: a track higher on the timeline covers the ones below, in preview, export, the project thumbnail and picking a layer on the preview. A new video track SHALL go above the other video tracks. | P0 | X |
| FR-194g | Dragging a track's header up or down SHALL move the track (a line shows where it lands), as one undo step; the track menu SHALL also offer Move track up / down. | P1 | X |
| FR-194h | Every colour setting (text colour, text box colour, colour effect parameters) SHALL be picked with a colour picker (spectrum, palette, sliders, with a hex box for exact values); a drag in the picker SHALL be one undo step. | P1 | X |
| FR-194b | The outer edge of a clip SHALL always trim; fade handles SHALL only be grabbed in the top half of a clip, so a trim never catches a fade. | P0 | X |

Design: `uml/domain/selection-schema.puml`, `uml/domain/selection-sequence.puml`.

### 3.18 Window and look
| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-195 | Recording a keyframe (the stopwatch) SHALL only add keyframes; clearing every keyframe SHALL be a separate, explicit, undoable action. | P0 | X |
| FR-196 | The window SHALL draw its own title bar with the menus, the project name and save state, project actions and the window buttons in it, and its own resize edges. | P1 | X |
| FR-197 | The menu bar SHALL be a standard `Menu` of `MenuItem`s with access keys, drawn in the app's theme (rounded, padded); context menus SHALL use the same theme. | P1 | X |
| FR-198 | The interface SHALL be flat and soft: panels told apart by tone rather than outlines or shadows, generous corner radii, and all colours and radii defined once in the theme. | P1 | X |
| FR-199 | There SHALL be no separate "focus preview" mode; the panels are shown and hidden individually. | P2 | X |

### 3.19 Text and image layers
| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-200 | An image from the library SHALL be placeable on a video track as a clip with a default length, and selectable like any clip (picture, crop, position, fades, effects, file). | P1 | X |
| FR-201 | A text clip SHALL carry its text and style (font, bold, italic, size as a share of the frame height, colour, alignment, shadow, background) and be edited in a Text tab; titles SHALL be added from the library's Text tab by a click (at the playhead) or a drag (onto the timeline), as one undo step. | P1 | X |
| FR-202 | Text and image clips SHALL be positioned, scaled and rotated in a Position tab, and with a gizmo on the preview (click a layer to select it; drag to move, a corner to scale, the knob to turn; every selected layer follows; one undo step per drag), with keyframes. | P1 | O |
| FR-205 | Moving a layer on the preview SHALL snap its centre to the middle of the frame (Ctrl places freely), and turning it SHALL step by 15° with Shift. | P2 | X |
| FR-201a | Titles SHALL draw any character the system has a font for, falling back per character when the chosen font lacks one, and SHALL draw emoji in colour as single pictures (skin tones, joined sequences, keycaps, flags), identically in the preview and export. Emoji SHALL be entered like any text: typed through the system's input method or pasted (on KDE, the emoji selector copies to the clipboard). | P1 | X |
| FR-200a | An animated GIF (or animated WebP) SHALL come in as an image clip that keeps its transparency and loops its frames, with their own timings, for as long as the clip lasts; placed on the timeline it SHALL last at least one loop (and at least the still-image length). | P1 | X |
| FR-203 | Text and image layers SHALL composite with their own transparency, identically in the preview, on the GPU and in export. | P1 | X |
| FR-204 | An inspector edit SHALL apply to every selected clip that has the facet being edited (and the clips linked to them), as one undo step; a slider drag SHALL be one step. Speed stays on the primary clip, because it ripples. | P0 | X |

Design: `uml/domain/text-image-layers-schema.puml`, `uml/domain/text-image-layers-sequence.puml`. Gizmo: `uml/domain/layer-gizmo-schema.puml`, `uml/domain/layer-gizmo-sequence.puml`. FR-202 is partial: the Position tab and the gizmo work; layout keyframes are not built.

### 3.20 Preferences
| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-210 | Preferences SHALL be one typed class, grouped into sections, whose property initial values are the defaults. | P1 | X |
| FR-211 | One shared `PreferenceService`, created at startup, SHALL be passed to everything that reads preferences; nothing SHALL read them from a static. | P1 | X |
| FR-212 | Preferences SHALL be stored in `~/.fig/preferences.json`. | P1 | X |
| FR-213 | Saving SHALL be atomic; a damaged file SHALL be kept as `.bad` and defaults used; a failed write SHALL NOT crash the app; keys the build doesn't know SHALL survive a save. | P1 | X |
| FR-214 | Values SHALL be clamped back into range after loading and before saving. | P1 | X |
| FR-215 | Preferences SHALL open as an in-window page (File, Preferences; Ctrl+Comma), one page per section, with "Reset to defaults" per page. | P1 | X |
| FR-216 | The export dialog SHALL start from the user's export quality and folder. | P1 | X |
| FR-217 | The window's keyboard shortcuts SHALL come from one table (`ShortcutCatalog`) plus the user's changes, and SHALL be rebindable on a Keyboard page (click, press keys, Esc cancels); keys another action has SHALL be taken from it; each shortcut can be removed or reset. Shortcuts that are typed characters SHALL never fire while a text box has focus, and while a text box has focus it SHALL keep the keys it uses itself (copy, cut, paste, undo, redo, select all, moving and deleting by character or word); window shortcuts on those keys step aside. | P1 | X |
| FR-219 | A Rendering page SHALL choose the renderer (Automatic, GPU, CPU) for the preview and for exports, and say whether the window's canvas is GPU-backed. | P1 | X |
| FR-220 | A Storage page SHALL list Fig's folders with their sizes and open them, and SHALL clear the previews and proxies of every project that isn't open. | P2 | X |
| FR-218 | Remembered state (panel sizes, open tabs, zoom) SHALL stay automatic and SHALL NOT appear as preferences. | P2 | X |

Design: `uml/persistence/preferences-schema.puml`, `uml/persistence/preferences-sequence.puml`; the first set of preferences is in `docs/preferences.md`. The editor's Snap, Link, Loop and Audio scrubbing toggles are preferences: they start from the saved value and flipping one saves it.

---

## 4. Non-functional requirements

| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| NFR-001 | The application SHALL run on Windows, macOS, and Linux via Avalonia. | P0 | O |
| NFR-002 | Preview playback SHALL decode at least 30 fps at preview resolutions on typical hardware. | P0 | X |
| NFR-003 | Export SHALL complete short projects in seconds to a few minutes (hardware-encode path for the seconds end). | P1 | O |
| NFR-004 | Import, export, artifact generation, and project validation SHALL never block the UI thread. | P0 | X |
| NFR-005 | Frame buffers SHALL be pooled and the frame cache bounded to limit memory growth. | P0 | X |
| NFR-006 | Project writes SHALL be atomic with rolling backups so a crash never corrupts the project file. | P0 | X |
| NFR-007 | `Fig.Core` SHALL be free of UI dependencies and fully unit-testable. | P0 | X |
| NFR-008 | Native (FFmpeg) resources SHALL be disposed without use-after-free races, and decode/dispose SHALL be serialized on a single worker thread. | P0 | X |
| NFR-009 | The core test suite SHALL pass before a change is considered complete. | P0 | X |
| NFR-010 | AI-generated code SHALL NOT be merged; contributors SHALL understand every line they add. | P0 | X |

---

## 5. Roadmap / future requirements

These requirements are captured for extensibility. Rows marked X have shipped and stay here for traceability; the rest are not yet implemented. A row that repeats a §3 ID (FR-072, FR-086, FR-113 to FR-115) is the same requirement, listed here for roadmap visibility; the roadmap-only items use their own IDs (FR-140 and up) so they never collide with §3.

| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-072 | Per-clip speed control with UI (video + audio pitch handling). | P1 | X |
| FR-086 | Additional transition types (wipes, irises, slide, etc.). | P1 | X |
| FR-113 | Encoder threading + Fast/Medium/Best presets. | P1 | O |
| FR-114 | Hardware-accelerated export (NVENC/VAAPI/QSV/VideoToolbox). | P1 | - |
| FR-115 | Stream-copy fast export for simple timelines. | P2 | - |
| FR-140 | Clip transform (scale, position, rotation) and richer color-grading effects. | P1 | O |
| FR-141 | Audio pan/balance and per-track audio effects. | P2 | - |
| FR-142 | Timeline project resolution settings (explicit canvas size) instead of media-derived. | P1 | - |
| FR-143 | Hardware video decode (NVDEC/VAAPI) for 4K/HEVC scrubbing. | P2 | - |
| FR-144 | Parallel per-layer decoding for stacked-track preview/export. | P2 | - |
| FR-145 | Template library and niche starter kits ("quick start for creators"). | P2 | - |
| FR-146 | Smart editing aids: automatic cuts on silent spans. | P2 | - |
| FR-147 | AI transcription and subtitle generation. | P2 | - |

---

## 6. Traceability and maintenance

- **Status updates:** when a requirement changes status, update both this document and the README Goals table.
- **New requirements:** assign the next free ID in the relevant module range; never reuse a retired ID.
- **Rationale:** every requirement's "why" should remain traceable to either the README goals, a UML note, or a recorded design decision.
- **Related artifacts:** see `uml/domain/core-schema.puml` (editing commands), `uml/media/media-pipeline.puml`, `uml/playback/playback-engine.puml`, `uml/persistence/persistence.puml`, `uml/persistence/otio-import.puml`, `uml/export/export-pipeline.puml`, `uml/input/gestures-input.puml`, `uml/playback/gpu-render-schema.puml`, `uml/playback/gpu-render-sequence.puml`, `uml/domain/selection-schema.puml`, `uml/domain/selection-sequence.puml`, `uml/domain/text-image-layers-schema.puml`, `uml/domain/text-image-layers-sequence.puml`, `uml/persistence/preferences-schema.puml`, `uml/persistence/preferences-sequence.puml`, and (effect graphs) `uml/media/effects-graph-schema.puml` and `uml/media/effects-graph-pipeline.puml`.
