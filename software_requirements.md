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
| **Linked clip** | A video clip and its companion audio clip sharing a link group; they move/trim/cut/delete together. |
| **Timeline** | The ordered set of tracks and clips that constitute an edit. |
| **Playhead** | The current position on the timeline. |
| **Frame rate (FPS)** | The playback rate expressed as a rational number (e.g., 24000/1001). |
| **Conform** | Adjusting clip playback to reconcile the source frame rate with the timeline frame rate. |
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
5. Mark, annotate, enable/disable, and conform clips.
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
| FR-031 | Dropping a video with audio SHALL create a linked audio clip on an audio track. | P0 | X |
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
| FR-043 | The timeline rate SHALL adopt the first dropped media's frame rate when the timeline is empty; clips with a different source rate SHALL be conformed (duration and source mapping scaled by the rate ratio). | P1 | X |

### 3.4 Timeline rendering
| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-050 | Video clips SHALL render aspect-correct filmstrip thumbnails tiled across the clip. | P0 | X |
| FR-051 | Audio clips SHALL render waveforms from decoded peaks in real time. | P0 | X |
| FR-052 | Fade ramps SHALL be rendered on clips and be draggable. | P0 | X |
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
| FR-070 | The mixer SHALL sum audible clips into interleaved stereo at 48 kHz, honoring track mute, clip volume, and fade envelopes. | P0 | X |
| FR-071 | Clip speed ≠ 1 SHALL resample the decoded audio so pitch and duration match the timeline (no truncation). | P0 | X |
| FR-072 | Per-clip speed editing SHALL be exposed in the UI (currently only supported in the engine). | P1 | O |

### 3.7 Effects and transitions
| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-080 | The application SHALL ship a catalog of clip effects (brightness, grayscale) applied as an ordered, toggleable stack. | P0 | O |
| FR-081 | Effects SHALL be applicable to and removable from a selected clip. | P0 | X |
| FR-082 | Video preview and export SHALL run each clip's enabled effect stack before compositing. | P0 | X |
| FR-083 | The application SHALL ship a transition catalog (cross-dissolve) applied across abutting cuts. | P0 | O |
| FR-084 | A transition SHALL be applicable by dragging a catalog entry onto a cut. | P0 | X |
| FR-085 | Transitions SHALL be selectable, resizable (drag or slider), and removable, all undoable. | P0 | X |
| FR-086 | The transition catalog SHALL grow beyond cross-dissolve (wipes, irises, etc.). | P1 | O |

### 3.8 Markers and annotations
| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-090 | The user SHALL add a marker at the playhead, attached to the selected clip, active track, or timeline. | P0 | X |
| FR-091 | Markers SHALL be selectable and draggable in time (clip markers clamp to the clip range). | P0 | X |
| FR-092 | Markers SHALL be renameable and recolorable via the properties panel. | P0 | X |
| FR-093 | Markers SHALL be deletable (Delete key / context menu / panel). | P0 | X |
| FR-094 | Markers SHALL render on clips, tracks, and the timeline ruler. | P0 | X |

### 3.9 Interoperability
| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-100 | The application SHALL import OpenTimelineIO `.otio` projects into the native project model. | P0 | X |
| FR-101 | OTIO import SHALL preserve markers, per-object metadata, global start time, media available ranges, and clip enable state. | P0 | X |

> **Note:** OTIO is used as a test/interchange vehicle — export small kdenlive projects to OTIO and open them in Fig to exercise capabilities. FCPXML/EDL and OTIO export are not requirements.

### 3.10 Export
| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-110 | The application SHALL export the full timeline to MP4 (H.264 video + AAC audio) at the timeline frame rate, honoring effects, transitions, fades, speed, conform, and layering. | P0 | X |
| FR-111 | An export dialog SHALL let the user choose resolution (presets or custom), quality (CRF), and output path. | P0 | X |
| FR-112 | Exports SHALL run as background jobs with live progress visible in a jobs popup; failures SHALL be reported and partial output removed. | P0 | X |
| FR-113 | The software encoder SHALL use all available cores and expose Fast/Medium/Best quality presets. | P1 | O |
| FR-114 | Export SHALL support hardware encoders (NVENC/VAAPI/QSV/VideoToolbox) with a software fallback. | P1 | - |
| FR-115 | For timelines with no effects/transitions/speed changes, a "fast export" SHALL re-encode only around edit points and stream-copy the rest. | P2 | - |

### 3.11 Input and configuration
| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-120 | Timeline input SHALL be resolved through a data-driven gesture registry rather than hardcoded in the view. | P0 | X |
| FR-121 | Gesture bindings SHALL be stored as editable configuration. | P0 | X |
| FR-122 | Core keyboard shortcuts SHALL exist for common actions (undo/redo, split, ripple delete, add/delete marker, enable/disable clip, save, save-as). | P0 | X |

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

These requirements are captured for extensibility but are not yet implemented.

| ID | Requirement | Priority | Status |
| --- | --- | --- | --- |
| FR-072 | Per-clip speed control with UI (video + audio pitch handling). | P1 | O |
| FR-086 | Additional transition types (wipes, irises, slide, etc.). | P1 | O |
| FR-113 | Encoder threading + Fast/Medium/Best presets. | P1 | O |
| FR-114 | Hardware-accelerated export (NVENC/VAAPI/QSV/VideoToolbox). | P1 | - |
| FR-115 | Stream-copy fast export for simple timelines. | P2 | - |
| FR-130 | Clip transform (scale, position, rotation) and richer color-grading effects. | P1 | - |
| FR-131 | Audio pan/balance and per-track audio effects. | P2 | - |
| FR-132 | Timeline project resolution settings (explicit canvas size) instead of media-derived. | P1 | - |
| FR-133 | Hardware video decode (NVDEC/VAAPI) for 4K/HEVC scrubbing. | P2 | - |
| FR-134 | Parallel per-layer decoding for stacked-track preview/export. | P2 | - |
| FR-135 | Template library and niche starter kits ("quick start for creators"). | P2 | - |
| FR-136 | Smart editing aids: automatic cuts on silent spans. | P2 | - |
| FR-137 | AI transcription and subtitle generation. | P2 | - |

---

## 6. Traceability and maintenance

- **Status updates:** when a requirement changes status, update both this document and the README Goals table.
- **New requirements:** assign the next free ID in the relevant module range; never reuse a retired ID.
- **Rationale:** every requirement's "why" should remain traceable to either the README goals, a UML note, or a recorded design decision.
- **Related artifacts:** see `uml/core-schema.puml` (editing commands), `uml/media-pipeline.puml`, `uml/playback-engine.puml`, `uml/persistence.puml`, `uml/otio-import.puml`, `uml/export-pipeline.puml`, and `uml/gestures-input.puml`.
