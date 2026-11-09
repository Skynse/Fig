# Fig UI inventory — the authoritative 1:1 port spec

Source of truth: `src/Fig.App` (Avalonia 12.1.1, .NET 10) plus the view-model logic in `src/Fig.Core`
(read-only snapshot taken 2026-11-08; repo working tree, no commits made by this survey).

Purpose: every screen, panel, custom-drawn control, pixel dimension, colour token, gesture and
keyboard shortcut the Rust UI must reproduce. Everything below was read from source; where a number
is an estimate it says *est.*. Reference PNGs were rendered by an Avalonia headless harness (see §0.2).

## 0. Method, source stats, reference renders

### 0.1 UI source stats (files and physical lines incl. XAML)

| Area | Files | LOC | Notes |
|---|---|---|---|
| `Views/` (all XAML + code-behind) | 16 axaml + 21 cs | 12,490 | EditorView.axaml alone is 2,042; Timeline partials ≈ 5.5k |
| `ViewModels/` | 39 cs | 10,315 | Editor/Preview/Properties partials dominate |
| `Rendering/` | 11 cs | 2,789 | GPU scene + effects + Skia CPU path |
| `Services/` | 14 cs | 1,954 | prefs, shortcuts, playback, dialogs, icons, export |
| `Controls/` | 12 cs | 1,810 | all custom-drawn widgets |
| `Converters/` | 6 cs | 133 | value → brush/opacity/time |
| `Styles/` | 2 axaml | 205 | FigTheme + FigControls |
| App shell (`App`, `Program`, `MainWindow`, `ViewLocator`, `DragFormats`) | 5 cs | ~300 | |
| **Fig.App total** | **154 cs + 16 axaml** | **29,880** | (~26.2k C#, 3.9k XAML) |
| Fig.Core (model/logic the UI binds to) | 96 cs | 22,126 | timeline editor, geometry, media, effercts, project |

Other inventory facts: 1,756 vendored Lucide SVG icons under `Assets/lucide/icons/`; one icon PNG
(`fig-icon.png`), one `.ico`; fonts come from `Avalonia.Fonts.Inter` (`WithInterFont()`), UI text is
Inter throughout, monospace text uses the system monospace family.

### 0.2 Reference renders (real, from a headless Avalonia render)

The app cannot be screenshotted on this machine's display; an Xvfb (`-extension GLX`) run starts but
the Avalonia X11 backend cannot create an EGL surface (no `/dev/dri`, NVIDIA EGL `drmFreeDevices`
double-free, `MESA: error: ZINK: failed to choose pdev`). Instead a throw-away harness
(`target/ui-shots/UiShots`, scratch, not committed) boots the real `App`/`MainWindow` under
`Avalonia.Headless` + `UseSkia().UseHeadless(new { UseHeadlessDrawing = false })`, drives the real
view models with a synthetic project (3 tracks, 6 video clips, 2 titles, 1 audio clip, 2 markers),
and saves `TopLevel.CaptureRenderedFrame()`. That is the same composition pipeline the desktop app
uses (minus GPU effects), so layout, tokens and typography are exact; the preview is black because no
media is decoded.

| File | Size | Shows |
|---|---|---|
| `reference/editor-1200x700.png` | 1200×700 | Editor, full shell, timeline with 2 video + 1 audio track, marker, playhead |
| `reference/editor-1600x900.png` | 1600×900 | Same at a larger window; panel fractions |
| `reference/editor-1100x700.png` | 1100×700 | Min-size window (MinWidth 1100, MinHeight 700) |
| `reference/home-empty-1200x700.png` | 1200×700 | Home first-run state (no projects) |
| `reference/home-projects-1200x700.png` | 1200×700 | Home grid view with cards |
| `reference/home-projects-1600x900.png` | 1600×900 | Home at large window |
| `reference/editor-selected-1200x700.png` | 1200×700 | Editor with a track/clip selected: inspector tab strip (Effects / Track), status bar "Track selected" |
| `reference/preferences-1200x700.png` | 1200×700 | Preferences overlay: centred card, 7-page rail, General page |
| `reference/export-dialog.png` | 560×696 | Export dialog (single-export mode, real layout) |

Fallback material in the repo: `assets/preview.png`, `assets/toolbar.png`, `assets/mtimeline.png`,
`assets/retime.png`, `assets/stress.png`, `assets/graph.png`; Penpot mockup `UI/mockup.penpot`.

Regeneration recipe (once a harness exists): configure `AppBuilder.Configure<Fig.App.App>().UseSkia()
.WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
.SetupWithoutStarting()`, build `AppViewModel(new ProjectStore(dir), new GestureRegistry(path))`,
`new MainWindow { DataContext = app }.Show()`, then `CaptureRenderedFrame().Save(png)`.

## 1. Shell (window chrome)

### 1.1 Window

| Property | Value | Source |
|---|---|---|
| Decorations | none — `WindowDecorations="None"`, `ExtendClientAreaToDecorationsHint="True"` | `Views/Shell/MainWindow.axaml` |
| Min size | **1100 × 700** | same |
| Design size | 1200 × 700 (`d:DesignWidth/Height`) | same |
| Background | `SurfaceBg` `#131417` | same |
| Title | `fig`; taskbar class/id `io.github.skynse.Fig` (X11 `WmClass`) | `Program.cs` |
| Icon | `/Assets/fig.ico` | MainWindow |
| Startup | Home view; opening a project switches to Editor and **maximizes** the window | `ViewModels/Shell/AppViewModel.cs` |
| Unsaved close | `Preferences.General.AskBeforeClosingUnsaved` (default true) | `Services/Config/Preferences.cs` |

Root grid: `RowDefinitions="Auto,*"` → `TitleBar` (row 0, **46 px**) then
`ContentControl Content="{Binding CurrentView}"` (row 1, Home / Loading / Editor).
Overlays stacked in the same `Grid`:

| Overlay | Placement | Visibility |
|---|---|---|
| Preferences page | `ContentControl Margin="0,46,0,0"` (below title bar) | `IsPreferencesOpen` |
| Toasts | top-right `Margin="0,16,16,0"`, not hit-testable | always mounted |
| GPU job host | 1×1 px bottom-left (`GpuJobHost`, draws nothing) | always |
| Resize grips | `Panel` with 8 transparent borders | hidden when maximized |

### 1.2 Resize grips (custom chrome, no system frame)

| Grip | Size | Margin | Cursor | ElementRole |
|---|---|---|---|---|
| N / S | h=5 | 10,0 | TopSide / BottomSide | ResizeN / ResizeS |
| W / E | w=5 | 0,10 | LeftSide / RightSide | ResizeW / ResizeE |
| NW/NE/SW/SE | 10×10 | corner | diagonal | ResizeNW/NE/SW/SE |

`OnPropertyChanged(WindowState)` hides the whole `ResizeGrips` panel when `WindowState != Normal`.

### 1.3 Title bar (46 px, undecorated drag region)

Root `Grid ColumnDefinitions="Auto,*,Auto" Height="46"`, `WindowDecorationProperties.ElementRole="TitleBar"`.
Every interactive child is given `ElementRole="User"` by a style (`Button, ToggleButton, Menu`), so
clicks on them work inside the drag region while empty title-bar space drags the window.

Left cluster (`StackPanel Spacing="2" Margin="12,0,0,0"`):
1. App icon `fig-icon.png` 22×22, margin `0,0,10,0`, tooltip "fig", HighQuality interpolation.
2. Menu bar (flat items: padding `11,6`, margin 0, min-height 30, radius 8, font 13, `TextSecondary`;
   hover `ChipBg`; open `HoverBg`).

Center (`IsHitTestVisible=False`): project name (`SemiBold 13`, `TextPrimary`, ellipsis, MaxWidth 360),
save status (`SaveStatusText`, 12, `TextMuted`), or `fig` (13, muted) when no project is open.

Right cluster (`Spacing="4" Margin="0,0,6,0"`, editing-only project actions then window buttons):

| Control | Size / style | Icon | Action |
|---|---|---|---|
| All projects | 32×32 `bar-icon` | `layout-grid` 16 | close project (Ctrl+W) |
| Export jobs | 32×32 + badge (accent pill, radius 7, min-w 14, h 14, font 9 bold, `#0e1626`) | `list-checks` 16 | toggle jobs window |
| Media panel toggle | 32×32 ToggleButton, checked bg `AccentSofter` | `panel-left` 16 | Ctrl+1 |
| Inspector toggle | 32×32 ToggleButton | `panel-right` 16 | Ctrl+2 |
| Export (primary) | h=32, padding `14,0`, radius 9, bg `Accent`, fg `#0e1626`, SemiBold 13; hover `#8bb8ff`; disabled opacity .45 | `upload` 14 + "Export" | open export dialog |
| Minimize / Maximize / Close | 42×30 `caption`, radius 8, transparent; hover `ChipBg`; close hover `#d9534f` white; icon 14/13 | `minus`, `square`↔`copy`, `x` | window state |

### 1.4 Menus (all strings, as shipped)

| Menu | Items (accelerator comes from ShortcutCatalog) |
|---|---|
| **File** | Import… · Save · Save as… · Save as package… (tooltip: "The project and all its media in one .figpck file…") · Export… · Export marked ranges… · Export captions (SRT, TXT)… · **Preferences…** · Close project · Exit |
| **Edit** | Undo · Redo · Cut · Copy · Paste · Duplicate · Split at playhead · Split every track · Delete before the playhead · Delete after the playhead · Freeze frame · Ripple delete · Lift delete · Enable / disable (Ctrl+E) · Group clips · Ungroup clips · Main track magnet (check) · Blade tool (check) · Add marker at playhead (M) · Create ranges from bookmarks… · Delete marker (Shift+Delete) · Auto captions… |
| **View** | Media panel (check) · Inspector (check) · Reset layout · Audio scrubbing (check) · Zoom to fit sequence · Zoom in · Zoom out · Fit tracks · Taller tracks · Shorter tracks |
| **Help** | Keyboard shortcuts (opens read-only cheat sheet) |

Menu look (FigTheme): presenter radius 12, padding 6, border 1, MinWidth 220, font 13; item radius 8,
padding `10,7`, margin `0,1`, MinHeight 30; input-gesture text margin `24,0,0,0`; separators margin
`8,5`; presenter bg `#23252b`, border `#2f323a`; item hover `#323640`, pressed `#3a3f4a`; disabled fg
`#5d616b`; accelerator fg `#868a95`.

### 1.5 Toasts (`Views/Shell/ToastHost.axaml`)

Top-right stack (`Margin 0,16,16,0`, item bottom margin 8). Card: padding `14,10`, bg `#2a2a2b`,
radius 6, **border `#4da3ff` 1 px**, text 13 `#eeeeee`. `IsHitTestVisible=False` on the host.
Unhandled UI exceptions are logged (`CrashLog`) and shown as
`Something went wrong ({what}). Details are in the log folder.`

### 1.6 Loading view (`Views/Shell/LoadingView.axaml`)

Full-bleed `#1e1e1e`; centered stack max-width 420 spacing 16: "fig" (28 Bold `#cccccc`),
indeterminate `ProgressBar` 280×6 accent `#4da3ff` on `#333333`, status line (13 `#cccccc`, wrap,
center), detail line (12 `#868a95`, wrap, center). Used while opening/validating a project.

### 1.7 Preferences overlay (`Views/Preferences/PreferencesView.axaml`, `ViewModels/Preferences/`)

Not a window: a **centred modal card** drawn over the editor, below the title bar (background dimmed by the
card's shadow; the editor behind is still visible). The card is roughly **850×500** at a 1200×700 window
(radius 14, `PanelBg`-tone surface, generous padding), laid out as: header (`Preferences` 15 SemiBold +
"Reset to defaults" ghost button + ✕ close, right), then a two-column body — a **left page rail** of 7
rows (icon + name, selected row filled `#2a2d34`, radius 8: General, Editing, Playback, Export, Keyboard,
Rendering, Storage) and the selected page content on the right. Each page is a stack of rows:
label (12–13 SemiBold) + note (12 muted) on the left, control on the right (switch, slider, text box,
ComboBox). Opened by Ctrl+, / File menu; closed by Esc (which first cancels a shortcut recording) or ✕.
Key rows:

- General: "Autosave after" seconds; "Ask before closing unsaved projects".
- Editing: "Snap"; "Snap distance" (px); "Select linked clips together"; "Undo steps".
- Playback: "Audio scrubbing"; "Loop playback".
- Export: "Quality" (CRF start); "Export folder".
- Keyboard: rebindable table (one row per `ShortcutCatalog` action, grouped by Section), record button;
  while recording, window key bindings are suspended (`ShortcutsSuspended`) so Ctrl+S can be captured.
- Rendering: "Renderer" (Automatic / GPU / CPU) + "Graphics" canvas status note.
- Storage: cache sizes for previews/proxies, clear button.

## 2. Screens and navigation

| Screen | View | VM | Shown when |
|---|---|---|---|
| Home / project browser | `Views/Home/HomeView.axaml` | `HomeViewModel` | app start, "All projects", Ctrl+W closes project |
| Loading | `Views/Shell/LoadingView.axaml` | `LoadingViewModel` | between Home and Editor |
| Editor | `Views/Editor/EditorView.axaml` | `EditorViewModel` | project created/opened |
| Graph editor (full editor takeover) | `Views/Graph/GraphEditorView.axaml` | `GraphEditorViewModel` | `IsGraphEditorOpen`; overlays the whole editor incl. status bar (`Classes="no-transport"`) |
| Export dialog | `Views/Export/ExportDialog.axaml` | `ExportDialogViewModel` | File ▸ Export / toolbar Export |
| Export jobs window | `Views/Export/ExportJobsWindow.axaml` | reuses `EditorViewModel` | jobs button / job start |
| Auto captions dialog | `Views/Captions/CaptionsDialog.axaml` | `CaptionsDialogViewModel` | Edit ▸ Auto captions, Text tab |
| Bookmark ranges dialog | `Views/Markers/BookmarkRangesDialog.axaml` | `BookmarkRangesViewModel` | Edit ▸ Create ranges from bookmarks… / Export dialog |
| Keyboard shortcuts | `Views/Shell/ShortcutsDialog.cs` (built in code) | — | Help ▸ Keyboard shortcuts |
| Prompt / confirm | `Services/Dialogs.cs` (built in code) | — | rename project, new project, delete project |

## 3. Home screen (`HomeView.axaml`, 211 lines)

Root `Grid RowDefinitions="Auto,Auto,*,Auto"`, background `SurfaceBg`, margin 0.

| Row | Content |
|---|---|
| 0 header | `Margin 24,18,24,16`, cols `*,Auto,Auto` spacing 16: "Projects" (16 SemiBold); "Import Project…" (148 w, `fig`); "+ New Project" (148 w, `fig primary`, Ctrl+N) |
| 1 toolbar | visible if projects exist, `Margin 24,20,24,16`, cols `*,Auto,Auto,Auto,Auto`: "Recent projects" (13 SemiBold) + count (12 muted); search TextBox 260 w placeholder "Search projects"; sort ComboBox 140 w (`Last edited`, `Name`, `Duration`); Grid/List ToggleButtons 60 w each |
| 2 content | first-run state (clapperboard icon 40, "Create your first project" 14 SemiBold, "Start a new edit or import an existing timeline.", + New Project); no-results state (title, note, "Clear search"); grid view; list view |
| 3 footer | shortcut hints: `Enter` Open project · `/` Search · `Ctrl+N` New project |

Grid view card: `StackPanel Width="228" Margin="0,0,16,20"`, thumbnail `Border Height="128"
CornerRadius="14" BorderThickness="2"` (hover border `#6ea5ff`, focus-visible `#9cc0ff` 2 px), then a
row with name (13 SemiBold, ellipsis) + ⋯ button (28×28), then "Last edited · …" and "Duration · …"
(12 muted). Context menu / flyout identical: Open project · Rename · Show in folder · Delete project…
(danger).

List view: header `*,200,120,40` ("Project", "Last edited", "Duration"); rows are Buttons h=60 bg
`#212328` radius 6, hover `#272a30`, icon `clapperboard` 20 + name 13 SemiBold + two meta columns.

Template thumbnails use `PathToBitmapConverter` on the project's saved thumbnail; placeholder uses
`PlaceholderText`. Cards show a `clapperboard` icon when no thumbnail. Keys: `Ctrl+N` new,
`/` focuses search (unless typing), `Esc` clears a focused non-empty search, `Enter` opens the
focused/first card. Home has no title-bar project actions (IsEditing false → Export/jobs hidden).

## 4. Editor layout (exact)

`EditorView.axaml` root `Grid` containing a `DockPanel Margin="8"` (whole editor inset 8 px) plus the
graph-editor overlay. Everything sits on `SurfaceBg` #131417.

### 4.1 Dock structure

```
DockPanel (margin 8, drop target for files)
├─ status bar (Dock=Bottom): Grid "*,Auto" margin 4,6,4,0, font 11 TextMuted
│    left  SelectionSummary      right StatusHint
└─ Workspace Grid (fills remainder)
     RowDefinitions: 0.54* (MinHeight 220) │ 4 │ 0.46* (MinHeight 240)
     ColumnDefinitions: 0.20* (MinWidth 220) │ 4 │ 0.58* (MinWidth 360) │ 4 │ 0.22* (MinWidth 260)
```

Runtime overrides (code-behind `ApplyLayout`): rows become `1 − TimelineFraction` and
`TimelineFraction` (defaults **0.56 / 0.44**; XAML's 0.54/0.46 is only the designer value), columns
become `LeftFraction` (0.20) / center `max(0.2, 1−left−right)` / `RightFraction` (0.22). Fractions
clamp: sides `[0.12, 0.40]`, timeline `[0.20, 0.75]`, persisted to `~/.fig/config/workspace.json`.

| Splitter | Grid pos | Size | Visibility |
|---|---|---|---|
| Left | col 1, row 0 | w=4, transparent, tooltip "Drag to resize the panel" | `Layout.IsLeftVisible` |
| Right | col 3, row 0 | w=4 | `Layout.IsRightVisible` |
| Timeline | row 1, cols 0–4 | h=4 | always |

Collapsed states: left collapses to a **46 px** rail (the icon rail stays); right collapses to a
**44 px** strip holding one `chevron-left` button ("Show the inspector (Ctrl+2)"). All panels are
`Border` with `PanelBg` #1a1b1f, `CornerRadius` 14, 1 px `BorderSubtle` (transparent → effectively
borderless), inner padding 10–12.

Status bar strings are dynamic (`SelectionSummary`, `StatusHint`: e.g. "Audio scrubbing on",
"Reversing clip…", "Nothing selected" + "Click a clip to select it · Space plays and pauses").

### 4.2 Left: library panel (col 0)

Two-part `DockPanel`: a **44 px icon rail** docked left (bg `SurfaceBg`, 1 px right border) + content.

Rail: `ListBox` of 7 `LibraryTabItem`s, padding 4,8, item margin bottom 4, item = `Border 36×36` with
an 18 px icon; selected bg `AccentSoft`; hover `HoverBg`. Tabs (icon, title, tooltip):

| # | Enum | Icon | Title |
|---|---|---|---|
| 1 | Media | `film` | Media |
| 2 | Templates | `clapperboard` | Templates |
| 3 | Text | `type` | Text |
| 4 | Transitions | `blend` | Transitions |
| 5 | Effects | `wand-sparkles` | Effects |
| 6 | Markers | `flag` | Markers |
| 7 | Captions | `captions` | Captions |

Content header: title 14 SemiBold + count (12 muted), right `chevron-left` button (26×26) hides panel
(Ctrl+1). Content body per tab:

| Tab | Structure |
|---|---|
| **Media** | top: search TextBox "Search media" + `ellipsis` flyout ("Collect sources"); grid `ListBox` with `WrapPanel`, card `Border Width="126" Margin="4"` bg `CardBg` radius 10, 1 px border, 72 px thumbnail area (`#0e0f11`), overlays: in-project `folder-check` chip / external `link` chip (top-left, 11 px icon on `#000000a0` radius 6), duration chip `MM:SS` 11 white (top-right), accent round **+** add button 22×22 (bottom-right, appears on hover/selected), "Proxy" pill (`AccentSoft`), file name (12, ellipsis, tooltip). Selected card: 2 px `Accent` border. Drag source (starts at 8 px threshold). Card context menu: Add to timeline at the playhead · Show in folder · Generate Proxy · Remove. Empty: `film` 28 + "Drop media here or use Import"; no match: "No media matches your search" |
| **Templates** | search "Search templates" + `plus` (save timeline as template); category ComboBox; cards 72×56 mini-diagram, name 12 SemiBold, description 10, duration + canvas labels, "Use" primary button, slot label |
| **Text** | "Add text" primary, "Auto captions…", hint "Click a style to add it at the playhead, or drag it onto a video track." 2-column `UniformGrid` of presets: 44 px preview (bg `#0e0f11`) with the style rendered, name 11 |
| **Transitions** | search "Search transitions", category ComboBox, hint "Drag a transition onto a cut." 2-col grid, 44 px `TransitionPreview` (real blender midpoint, 112×56 cache), name 11 |
| **Effects** | bottom hint "Drag onto a video track, or select a track or clip and press +."; search "Search effects" + `plus` (new graph effect) + `folder-open` (import); category ComboBox (`All, Motion, Blur, Light, Glitch, Retro, Distort, Color, Stylize, Custom`); "Presets" section (`EffectPreset` cards with 28 px round icon tile `#2a3d61`, name 12 SemiBold, description 10, + button) then "Effects" section (`EffectCatalogEntry` cards with 64×44 live `EffectPreview`, name 12 SemiBold, description 10, + button). Context menus: Add to the selected clip; Edit graph…/Duplicate/Export…/Delete for custom |
| **Markers** | bottom "Export marked ranges" primary; search "Search markers"; "+ Point" / "+ Range"; selection summary; "Select all" / "Delete selected"; "Split video at markers"; hint "Ctrl-click to toggle · Shift-click for a range · Delete to remove"; lists "Sequence markers" and "Clip markers" as `marker-row` buttons (min-height 50, 4 px colour strip + name 12 SemiBold + subtitle; selected = `AccentSofter` bg `#222f48`, `Accent` border), hint "Clip markers move with their clip."; context menu: Jump to marker · Edit marker... · Split video at selected markers · Delete selected markers · Clear generated beats on this clip · Clear all markers on this clip |
| **Captions** | "Auto captions…" primary + "Export…"; captions-track ComboBox (when many); "Style all captions together"; "Find in captions" + replace row + "Replace all" + match-case; hint "Enter: next caption · Shift+Enter: new line · ✂ splits at the cursor"; virtualized row list: row = time button, split / merge / delete icon buttons (26×24), `TextBox` caption text; row states current (`Accent` border) / selected (`AccentSofter`); empty state "No captions yet" |

### 4.3 Center: preview panel (col 2)

`Border` padding 10, header row: "Preview" 14 SemiBold + `ToggleButton` "Show controls" (124 w,
`fig compact`). Then `PreviewView` (its own UserControl, 123 lines).

`PreviewView` = `Border Background="#0e0f11" CornerRadius 12 ClipToBounds` with `DockPanel`:

- **Bottom transport bar**: `Border Dock=Bottom` bg `CardBg` padding `12,5`, Grid `*,Auto,*` min-height
  32.
  - Left: `TimeDisplay` monospace 11 `#b8bbc4` (`00:00:00.00 / 00:00:28.18`), buffering icon
    `loader-circle` 13 `#7db1ff`, error icon `circle-alert` 13 `#ff6b6b`.
  - Center: `skip-start`, `step-back`, **play** (30×30 bg `#30343b` radius 3), `step-forward`,
    `skip-end`, `repeat` (loop, icon tinted accent when on). Small buttons 28×28, radius 3.
  - Right: mute icon + volume `FigSlider` (72×18, 0..1, default 1), quality `ComboBox` (84×24, font 10,
    bg `#272a30`, radius 6: 270p/360p/540p/720p/1080p), `gauge` decode-status icon 13 `#686c76`.
- **Picture area**: `Border Background="#0b0c0e"` holding `Grid`: `PreviewSurface` (custom control) +
  three overlays (`LayerGizmo`, `TransformOverlay`, `SwirlOverlay`), each visible from
  `ShowOverlay`. Picture is letterboxed (`Stretch` fit preserving aspect).
- Right-click picture: Play/Pause · Go to start · Go to end · Show/Hide controls on the preview.

`PreviewSurface` (`Views/Editor/PreviewSurface.cs`): GPU mode draws a `GpuScene` (decoded frames +
crop/effects/transitions/blends) directly onto Skia's GPU canvas via `ICustomDrawOperation`; CPU mode
writes a finished BGRA frame into one reused `WriteableBitmap`. Raises "is GPU" once after first draw.

### 4.4 Right: inspector panel (col 4)

Header `Border` padding `2,1,2,9`, bottom 1 px border: title (`Properties.Title`) 14 SemiBold,
subtitle 11 muted (ellipsis), hide button (26×26 `chevron-right`, Ctrl+2).

When nothing is selected: centered stack — 48×48 circle `CardBg` with `panels-top-left` 22 muted,
"Nothing selected" (13 SemiBold), "Select a clip, track, marker, transition, or media item to edit its
settings." (12 faint, max 230 wide, wrapped).

With a selection: a **segmented icon tab strip** (`ListBox.segmented`, bg `CardBg`, radius 10, padding
3, item 30 h, radius 8, selected `HoverBg`), one tab per inspector section implied by the selection's
`Facet` flags, then the section content. Sections and facets (`ViewModels/Editor/InspectorSections.cs`):

| Order | Section | Facet | Title | Icon |
|---|---|---|---|---|
| 1 | TextSection | Text | Text | `type` |
| 2 | PictureSection | Picture | Picture | `image` |
| 3 | LayoutSection | Layout | Position | `move` |
| 4 | SoundSection | Sound | Audio | `volume-2` |
| 5 | RetimeSection | Retime | Speed | `gauge` |
| 6 | FadesSection | Fades | Fades | `blend` |
| 7 | EffectsSection | Effects | Effects | `sparkles` |
| 8 | TrackSection | Track | Track | `layers` |
| 9 | MarkerSection | Marker | Marker | `bookmark` |
| 10 | TransitionSection | Transition | Transition | `arrow-right-left` |
| 11 | SourceSection | Source | File | `file` |

(`Facet.Crop` is edited inside Picture.) Section templates are in `EditorView.axaml` lines 348–986.

Shared widget rows (data templates):
- `DoubleParamEditorViewModel`: Grid `ColumnDefinitions="72,*,52,Auto"` spacing 7, margin `0,3` —
  label (12/11 muted, ellipsis, tooltip), `FigSlider`, `NumberField` (h 22), keyframe buttons
  (prev `chevron-left` 12, diamond toggle 20×20, next `chevron-right` 12; tooltips "Previous
  keyframe", "Add or remove a keyframe at the playhead", "Next keyframe").
- `BoolParamEditorViewModel`: CheckBox or icon `ToggleButton` + keyframe buttons.
- `ColorParamEditorViewModel`: label + Avalonia `ColorPicker` (hex converter) + keyframe buttons.
- `ChoiceParamEditorViewModel`: label + ComboBox (font 11).
- `PropertyGroupViewModel`: `Border.inspector-group` (padding `0,11,0,12`, 1 px bottom rule) with
  header title (12 SemiBold) + "Reset" ghost button and an ItemsControl of editors.

Section-specific content (all in scroll viewers with `HorizontalScrollBarVisibility=Disabled`):
- **Text**: TextBox (multi-line, min-height 64, "Title text") + font ComboBox (virtualized) + text groups.
- **Layout**: layout groups (position/scale/rotation via number fields 42×22 `inspector-value`).
- **Speed**: "Retime" card with BPM helper flyout (Song BPM, Clip BPM, "Allow half / double time",
  match button), Speed `FigSlider` 0.25..4 default 1 + NumberField `{0:0.00}×` (min .1, max 8, step .05,
  tooltip "Drag up or down to adjust (Shift: fine) · click to type, up to 8×"), range labels 0.25×/4.00×,
  speed-curve note + "Add speed curve"/"Remove speed curve", retime groups, "Clip duration" card
  ("Original", "After retiming", monospace values).
- **Fades**: fade groups + "Fade envelope" 88 px `FadeEnvelope` + "Adjust fades here or from the clip corners."
- **Effects**: "Applied effects" + "Bypass all"; empty state (`wand-sparkles` 22 + "No effects on this clip
  yet" + "Add one below, or drag it from the Effects library onto the clip."); effect list (`effect-card`
  padding `10,6`, radius 8, bg `#212328`, icon 15 `AccentText`, name 12 Medium, power toggle, remove ×,
  opacity dims when disabled); optional `Transform` card (Animate toggle, `KeyframeRuler`, prev/diamond/next,
  Time/Zoom/Rotation TextBoxes 136 w monospace centred, aspect ratio, easing ComboBox
  (`Linear, Ease in, Ease out, Ease in & out, Hold`), "Back to full frame here", "Preview movement",
  "Limit to part of the clip" + Length); generic params card (`CardBgAlt`, selected effect name accent,
  params, "Edit graph…"); "+ Add effect".
- **Track**: Name / Type / State labels.
- **Marker**: Name TextBox; Colour swatches 24×24 radius 4 (2 px transparent border, hover `#868a95`,
  selected `#ececef`); point marker: Position, "Attached to", "Place keyframe at marker", "Jump to marker";
  range marker: Start/End, duration, "Include in batch export", "Output name", "Jump to start"/"Jump to end";
  "Delete marker" danger.
- **Transition**: type name 15 SemiBold + description; Duration; "Controls" + Reset + params; "Preview
  transition"; Replace / Remove.
- **File**: File name; Kind / Resolution / Duration; Proxy section (status, detail, action button).

### 4.5 Bottom: timeline panel (row 2, cols 0–4)

`Border` padding 10, `DockPanel`:

1. **Timeline tabs** (Dock=Top): `ItemsControl` of `Button.timeline-tab` (padding `9,4`, radius 8, font 13;
   active = `TextPrimary` SemiBold on `ChipBg`), double-click renames, right-click Rename…/Duplicate/Delete;
   plus `plus` button "New timeline (right-click a tab to duplicate one)".
2. **Toolbar** (Dock=Top, margin bottom 8), Grid `Auto,*,Auto`:
   - Left: project frame-rate ComboBox (`23.976, 24, 25, 29.97, 30, 50, 59.94, 60 fps`; min-height 26,
     padding `8,2`, font 11) + frame-shape ComboBox (`Auto, 16:9, 9:16, 1:1, 4:5`).
   - Center `StackPanel.timeline-tools` spacing 2, buttons 34×34: Undo (`undo` 19), Redo (`redo` 19),
     gap 12, Select tool (`mouse-pointer-2`, checked when !Blade), Blade (`slice`), Split at playhead
     (`split`), then selection-only group: Delete before playhead (`arrow-left-to-line`), Delete after
     (`arrow-right-to-line`), Freeze frame (`snowflake`), Delete (`trash`); gap 12; Main-track magnet
     (`panel-bottom`), Snapping (`magnet`).
   - Right: "Fit sequence" (104 w `fig compact`), zoom-out 30, zoom `FigSlider` 110 w log 0..1000
     (double-click → 100%), zoom % text (min 44, right-aligned, 11), zoom-in 30, shorter tracks
     (`fold-vertical` 13), fit tracks (`rows-3`), taller tracks (`unfold-vertical`), Beats button
     (flyout: beat markers sensitivity/kicks/apply), "⋯" (44 w; menu: Enable / disable clip Ctrl+E,
     Delete selected marker Shift+Delete, Analyze beats for markers).
3. **Timeline scroller** (`ScrollViewer`, horizontal scrollbar **disabled** — horizontal scrolling is
   viewport-driven; vertical auto; background `#272a30`) containing `TimelineView`
   (`ClipToBounds`, stretched, `MinHeight = viewport height`).

### 4.6 Graph editor overlay (`GraphEditorView.axaml`, 232 lines + `GraphCanvas.cs` 822)

Takes over the editor when open (`Classes="no-transport"` so transport keys are ignored). Root
`Grid RowDefinitions="48,*,Auto,28"`, bg `SurfaceBg`.

- Row 0 toolbar (48 px, `PanelBg`, 1 px bottom border), cols `Auto,Auto,Auto,*,Auto` margin 8:
  back ("‹ Effects"), name TextBox 210×28, Add / Edit / View menus, then validity dot (28×28 circle:
  green `#17302a`/`#589b74` ✓, or `#2a2510`/`#eab308` ⚠), separator, undo/redo/revert, Save (primary).
- Row 1: `Grid ColumnDefinitions="*,360"` → `GraphCanvas` + right inspector (`PanelBg`, 1 px left
  border): "Preview" `SplitPreview` (Before/After, split 0.5); "Selected node" + `NodeParamRow` editors
  with pin buttons; "Controls" (exposed sliders: label TextBox, up/down, remove).
- Row 2: node-picker / status strip (28 px).
- Canvas geometry: node **176 w**, header 26, rows 22, top pad 8, param row 22, param gap 6, port
  radius 5 (hit 9), bg `#141517`, grid `#1b1c1f`, card `#232427`, row `#1b1c1f`, text `#f0f0f0` /
  `#c8c8c8` / muted `#8a8a8a` / faint `#666668`, accent `#5f9cf8`.
- Canvas gestures: drag empty space = pan; wheel = zoom; drag node = move; drag port = wire (invalid
  wires refused with a reason); box-drag = marquee select; right-click = node/wire menus; Shift+A =
  node search; double-click node = rename?

## 5. Timeline anatomy (the hard part)

### 5.1 Geometry constants (exact px, `Fig.Core/Timeline/Viewport/TimelineGeometry.cs` + `TimelineView.Theme.cs`)

| Constant | Value | Meaning |
|---|---|---|
| `TrackHeaderWidth` | **140** | left header column (drawn inside the control) |
| `MarkerLaneHeight` | 20 | top strip: range bars + bookmark flags |
| `TickRulerHeight` | 22 | tick ruler |
| `RulerHeight` | **42** | 20 + 22; pinned while tracks scroll under it |
| `PlayheadCapHeight` | 8 | small triangle under the ruler |
| `ClipInset` | 6 | gap above a clip inside its lane (room for marker pennants) |
| `ClipLabelHeight` | 14 | name strip above a clip body |
| `ClipHeight` | 72 | video picture strip |
| `AudioClipHeight` | 52 | audio waveform strip |
| `AudioStripHeight` | 22 | sound strip under a video that plays its own audio |
| `TextClipHeight` | 26 | text-only bar (no label strip) |
| `MinTextClipHeight` | 18 | floor when scaled down |
| `LaneBottomPadding` | 8 | under the clip |
| `TrackHeight` (video lane) | 6+14+72+8 = **100** | |
| `AudioTrackHeight` | 6+14+52+8 = **80** | |
| `EmptyTrackHeight` | 34 | lane with no clips/markers/effects |
| `ClipCornerRadius` | 6 | clip/ghost corners (min with w/2) |
| `SplitGap` | 1 | hairline between abutting clips |
| `TrackAccentBarWidth` | 2 | selected-track accent at x=0 |
| `HeaderButtonSize` / gap / padding | 22 / 4 / 7 | header icons |
| `TrackScale` | 0.4 … 2.0, step 1.2 | Alt+wheel / toolbar; snap to 1 within 0.02 |
| `RackFoldScale` | 0.75 | below this effect racks fold to bars |
| `MinLabelWidth` | 24 | narrower clips skip their name |
| `ClipDetailWidth` | 6 | narrower clips draw flat colour only |
| `TrimEdgePx` | 6 | trim grab zone at either end |
| `FadeZonePx` | 14 | fade grab zone inward from each corner |
| `MarqueeStartPx` | 4 | movement before a press becomes a box |
| `AutoScrollZonePx` | 48 | edge auto-scroll band; max 22 px/tick |
| `RippleAnimDurationMs` | 220 | ripple slide animation |
| Zoom factor | 1.25 per step | 1 … 2000 px/s; default **100 px/s** |
| Page turn lead | 0.10 of visible width | playhead keep-in-view |

Track lane height = `ClipInset + LabelHeight(track) + BodyHeight(track, scale) + LaneBottomPadding`.
`BodyHeight` = audio ? 52·s : text-only ? max(18, 26·s) : (72 + (has audio strip ? 22 : 0))·s.
`LabelHeight` = 0 for a text-only track, else 14. `TrackTop(i) = RulerHeight + Σ heights(0..i−1)`.
`ComputeContentHeight = RulerHeight + Σ lanes + HeaderButtonSize + 12`.

### 5.2 Drawing order (`TimelineView.Rendering.cs`, one `Render` pass, immediate-mode-style)

1. Surface background (`EditorTheme.CardBrush` `#202226`) over the whole bounds.
2. Per track (top to bottom, list order — not `Track.Index`):
   1. lane background (alternate `#202226` / `#1d1f22` by row index), header column `#191b1e`,
      selected-lane tint (`#147b9fc6`) + 2 px accent bar at x=0 when selected;
   2. 1 px vertical border at x=140, 1 px horizontal lane divider, snap grid when dragging & snapping;
   3. track name (11, `TextMuted`) at `HeaderLabelX() = 7 + 3·26 + 2 = 87`, vertically centred;
   4. header toggles: visibility (`eye`/`eye-off`) or mute (`volume-x`/`volume`), mute for video tracks,
      delete (`trash`) when selected or hovered, effect-rack fold toggle at column 9.
   5. clips (clipped to `Rect(140, 42, w−140, h−42)`):
      - cull if `x + w < 140 || x > bounds`;
      - w < 6 → flat rounded? no: plain `DrawRectangle` (selected → `#d9e0e7`), skip everything else;
      - shadows: 1 px down-offset `#00000060`;
      - radius 6 (clamped w/2), split-gap subtraction when the clip has touching neighbours;
      - **label strip** (video/image/audio): fill = clip fill, name 10 white at x+4 (offset +26 when the
        clip has effect bars), clip-limited;
      - **body**: video → filmstrip tiled aspect-correct by source time; audio → mirrored max-envelope,
        one line per pixel column; image → thumbnail tiled by aspect; text → bar with `T` + words;
      - video's own-sound strip (22·scale) at the bottom with its own waveform and fades;
      - fade ramps (diagonal overlays), speed curve (log-scaled ⅛×..8× band, 1× dashed centre),
        transition spans + badge, keyframe diamonds on the body top edge, effect rack (below body),
        disabled clip = 65 % black veil (`#78101012`), selection = 2 px `#d9e0e7` outline inset 1,
        resize handles at edges (and fade tips when selected);
   6. track-level effect racks (independent regions) across the lane;
3. markers over tracks: clip pennants (pole down the picture), track diamonds, timeline marker lane;
4. add-track `plus` button under the last lane (`Rect(8, bottom+4, 22, 22)`);
5. drop previews: target lane tint `#2244aa44`, media ghost (`#88ffffff` fill + 2 px accent outline),
   effect ghost (ochre `#c79a3d` 35 α lane, ghost bar 24 h), transition-cut marker (3 px `#eab308`
   line + 10×12 diamond), red insert line for media dropped into empty space;
6. pinned ruler + marker lane (`PushTransform` by `StickyOffset` so they stay at the pane top);
7. empty-timeline hint ("Drag media here to start your edit" 14 / "or double-click a clip in the Media
   panel to add it at the playhead" 12);
8. guides and drag feedback: snap guide (1.5 px `#e6ffd60a`), marquee (`#285f9cf8` fill, 1 px outline,
   touching clips outlined), copy preview, diamond copy preview, blocked/origin ghosts (dashed white 28 α
   fill or blue 50 α fill), insert mark, selected gap (dashed blue, `#2e5f9cf8`), blade line
   (`#f0ff5a5a` 1.5 px), track drop line;
9. **playhead**: 1 px `PlayheadBrush` `#f0645a` line from `MarkerLaneHeight+8` to bottom + a 10×8
   triangle cap (`±5 px`) riding the pinned ruler.

Culling is per-clip, per-track and per-frame. Everything is redrawn on `InvalidateVisual()`; no retained
scene graph. The paint loop allocates almost nothing: cached immutable pens (`PenOf(color,width)`),
cached `FormattedText` (key = text + size + brush + max width/height/lines/ellipsis, LRU-capped 4096,
cleared on overflow), cached filmstrip bitmaps, cached wave geometries (per clip, weak table; shaped
once per width change, clipped to the on-screen part when > 6000 px wide), cached racks keyed by a
fingerprint of effects/length/sound flags.

### 5.3 Ruler and grid (`TimelineView.Ruler.cs`)

- Background `#181a1d`, 1 px bottom border; pinned (drawn translated by scroll offset).
- Scale picked by `RulerCalculator.PickScale(pxPerSec, fps)` → three tick levels (major 12 px, mid 8,
  minor 5) and labels only at major ticks; minor ticks dropped below 5 px spacing.
- Tick colours: major `#8b9098`, mid `#5a5f67`, minor `#40444b`; labels 10 px `TextMuted`, 4 px right
  of the tick, y = 20+2.
- Label format: `H:MM:SS` / `M:SS` at everyday zoom; when a major tick is < 1 s apart it switches to
  `MM:SS:FF` (timecode). Frame-accurate: times are snapped to the project's `FrameRate` grid.
- While dragging a clip/marker **and** snapping is on, the same minor ticks are drawn faintly through
  the lanes (white 14 α minor / 30 α major) — the "snap grid".
- Snap reach = `Preferences.Editing.SnapReachPx` (default 9 px, clamp 2..40) converted to seconds at
  the current zoom; grid step = ruler minor step; the playhead is always a snap target.
- Playhead is drawn pinned and the timeline auto-pages when playing (`PageTurnLead` 10 %).

### 5.4 Markers (`TimelineView.Markers.cs`, 371 lines)

Three levels — timeline (ruler lane), track, clip.

| Element | Drawing |
|---|---|
| Marker lane (top 20 px) | Range markers: rounded bar (height 16, corner 3, fill = marker colour darkened 42 % toward surface, 1 px colour border, label 9–10 white at 4 px inset, name + duration when it fits). Point markers: flag (10×10 pennant) on a pole at the time, label to the right. Selected/hovered: brighter outline; labels bold. |
| Track markers | 8×8 diamonds at the bottom edge of the lane, marker colour, dark 1 px outline |
| Clip markers | Pennant head (10×7) at the top of the clip + pole down through the picture; grabbed by head (10 px) or pole |
| Beat preview | Dashed `#5ac8fa` poles + open pennants (not yet applied) |

Default marker colour `#ffd60a`. Interactions: click selects (Ctrl toggles, Shift extends a range of
markers); double-click seeks; drag moves point markers (snapped, clamped ≥0); range markers drag body /
start edge / end edge (min length 0.1 s); range grab zones are the bar edges (`SizeWestEast` cursor) vs
the body (`Hand` cursor). Right-click menu on marker: Jump to marker · Edit marker... · Split video at
selected markers · Delete selected markers (+ per-clip clear items). Hover shows a tooltip with name and
time (`DescribeMarker`).

### 5.5 Clips — per-kind rendering

| Kind | Fill | Border | Body contents |
|---|---|---|---|
| Video | `#1679db` | `#55a2b2` 1 px | filmstrip (tiles by source time × speed at 1 frame per ~tile width), sound strip if `PlaysAudio`, fades, speed curve, transition tint, keyframe diamonds |
| Audio | `#2d674a` | `#589b74` | mirrored waveform `#8bc9a3`/`#74d6a0`, centre line 70 α, fades, keyframes |
| Text | `#a84f7d` | `#d17aa6` | no label strip; words written inside starting after a `T` badge, ellipsized |
| Image | `#5e57a8` | `#8b84d6` | thumbnail tiled edge-to-edge by aspect |

Disabled clips: 65 % black veil + reduced fade visibility. Selected: 2 px `#d9e0e7` rounded outline and
both trim handles drawn; hovered edge draws its handle. Keyframe diamonds: 8×8 rotated squares on the
body's top edge, white/blue depending on selection, with hover tooltips; effect keyframes live on effect
bars. Transitions: the incoming/outgoing span tinted `#46e0a308` / `#78eab308`, plus a pill badge with
the transition icon centred on the cut (14–16 h), hit ±15 px horizontally, ±16 px around the body centre.

### 5.6 Effect racks (`TimelineView.Racks.cs`, 766 lines — the densest custom drawing)

Each clip's effect stack is drawn as bars stacked under it in the lane (the lane grows to fit).

- Bar heights: compact **6**, full **32** (with 20 px label row + keyframe lane under it); gaps 2/3;
  padding 4; min bar width 4.
- Default folded when track scale < 0.75 unless the track's rack was opened by hand (header toggle).
- Colours: effect bars `#6f5526` fill / `#c79a3d` edge (autumn ochre); Transform (zoom) `#1f3a66` /
  `#5f9cf8`; level `#2d674a` / `#589b74`. Selected bar: white 1.5 px outline.
- Bars show: icon, effect name (10 px) + its params count, a mini keyframe lane with diamond keyframes,
  Animate (timer) toggle, power toggle, remove ×; disabled effects dim.
- Track-level effect regions share rows when they don't overlap (`AssignRows(pack: true)`); clip racks
  stack one per row.
- Hit parts: `Body`, `Left`, `Right` (resize), `Power`, `Remove`, `Keyframe`, `Animate`. Drag body =
  move region in time (1 undo step); drag edges = trim; Ctrl-drag = copy; clicking a bar inside a
  multi-selection narrows to it.
- Rack geometry cached per owner via `ConditionalWeakTable` + fingerprint; brushes cached per colour.

### 5.7 Keyframes on the timeline (`TimelineView.Keyframes.cs`)

One interaction set for **all** keyframes (clip automation, effect params, speed points): 8×8 diamonds
along the top edge of the clip body (or the effect bar's keyframe lane). Click picks (selecting the host
too) and seeks; drag moves in time (snapped, host-relative); Ctrl-drag copies; right-click opens the
shared `KeyframeMenu` (Jump to keyframe · Duplicate to playhead · easing submenu · Delete keyframe);
hover highlights + tooltip; Delete removes the picked one. Speed points additionally drag vertically
(log scale) to change speed. Diamond hit radius 5–7 px.

### 5.8 Viewport, zoom and scroll (`TimelineView.Viewport.cs`, `TimelineViewport.cs`)

- Model: `PixelsPerSecond` (`MinPixelsPerSecond` 1, `MaxPixelsPerSecond` 2000, default 100) and
  `ScrollTime`; `TimeToX = (t − ScrollTime)·pps`; `ZoomAt(cursorX, factor)` keeps the time under the
  cursor fixed.
- Per-timeline zoom/scroll remembered in a `ConditionalWeakTable` when switching timelines.
- Vertical: the timeline control grows with track count inside the parent ScrollViewer; the ruler and
  marker lane are pinned (`StickyOffset = clamp(scrollY, 0, bounds.Height − RulerHeight)`); pointer
  positions over the pinned ruler are converted into ruler space (`RulerSpace`).
- Zoom UI: toolbar slider log-mapped — slider `0..1000` → `pps = 1 · (2000/1)^(value/1000)`, text
  `pps/100 %`; double-click slider → 100 %; Ctrl+= / Ctrl+- step 1.25; Shift+Z fits sequence.
- `ZoomToFitSequence`: pps = (width − 140) / max(0.5, end), scroll 0.
- `FitTracks`: 24-step binary search on track scale so all lanes fit the viewport, then scroll home.
- Wheel: plain = vertical scroll (parent ScrollViewer); **Ctrl+wheel** = zoom at cursor (1.25);
  **Shift+wheel** = horizontal pan; **Alt+wheel** = taller/shorter tracks. All four are rebindable
  through `GestureRegistry` (`GesturePattern` = button/wheel-dir/modifiers → `TimelineGesture`:
  `None, ZoomIn, ZoomOut, TallerTracks, ShorterTracks, ScrollHorizontal, ScrollVertical, Pan,
  MoveClip, ResizeClipStart, ResizeClipEnd, DragPlayhead`). Defaults: middle-drag = Pan, plain
  wheel = ScrollVertical. Config file `~/.fig/config/gestures.json`.
- Keep-playhead-in-view while playing: off the right → new page starts 10 % before playhead; off the
  left (playing backwards) → page ends 10 % after it.

### 5.9 Filmstrips, waveforms, thumbnails

- Media assets carry `Thumbnail` (image path), `Filmstrip` (a horizontal strip image) and decoded
  `Peaks` (audio). Generated by Fig.Core (`FilmstripGenerator`, `ProxyGenerator`, `BeatAnalysisService`)
  and backfilled in the background after a project opens (`BackfillMediaPreviewsAsync`).
- Filmstrip: tiles aspect-correct, each tile chosen by the source time of the clip (so trimming/speed
  changes pick the right frames); the last tile is clipped, never squeezed.
- Waveform: one column per device pixel, amplitude = loudest sample in the covered source slice,
  mirrored around the centre line; no stretching, so zooming spreads the same samples wider; geometry
  cached per clip and reshaped when width/height/range/speed curve changes; > 6000 px wide → clip to
  the visible part.
- Bitmaps loaded through a `PathToBitmapConverter`/`GetBitmap` cache and drawn with high-quality
  interpolation.

## 6. Interaction contract

### 6.1 Pointer gestures on the timeline

| Gesture | Result | Source |
|---|---|---|
| Left click clip | select (linked partners included unless Alt or link pref off); click in a multi-selection collapses to it on release (if moved < 4 px) | `Selection.cs` |
| Ctrl-click clip | add/remove from selection | same |
| Shift-click clip | add to selection | same |
| Left drag clip body | move; all selected clips move together; snaps to edges/markers/playhead/grid; Alt = free (frame grid only) | `TimelineView.cs` |
| Left drag clip edge (±6 px) | trim start/end, walled by neighbours; linked partners trim with it | `Resize.cs`, `Roll.cs` |
| Ctrl/Cmd-drag clip edge | **roll**: the touching neighbour gives/takes the difference | `Roll.cs` |
| Drag fade handle (inner 14 px / existing fade tip) | fade in/out; a video's own sound strip has separate fades | `TimelineView.cs` |
| Ctrl/Cmd-drag clip | copy drag (ghost follows; Esc cancels) | `Duplication.cs` |
| Left drag empty space | marquee box; selects clips whose drawn rect it touches | `Selection.cs` |
| Shift/Ctrl-drag starting on a clip | marquee from there (also selects the pressed clip on click) | `TimelineView.cs` |
| Click empty gap then Delete | selects that gap (dashed blue) and closes it | `Tools.cs` |
| Middle drag | pan time (`DragPlayhead`/`Pan` gestures rebindable); always available | `TimelineView.cs` |
| Drag in ruler (y ≤ 42) | scrub playhead (snapped, no playhead self-snap) | `Viewport.cs` |
| Drag marker | move point marker / body / start / end of a range | `Markers.cs` |
| Click transition badge | select + drag to resize the transition (duration = 2·|dx|, clamped 0.05..max, snapped); double-click seeks to the cut | `TimelineView.cs` |
| Drag keyframe diamond | move it in time; Ctrl = copy; speed point also vertical | `Keyframes.cs` |
| Double-click speed line | add a speed point | `Velocity.cs` |
| Drag effect bar (body/edges) | move/resize effect region; Ctrl = copy | `Racks.cs` |
| Click effect bar power/× | toggle/remove effect | `Racks.cs` |
| Drag track header up/down | reorder tracks (drop line shown; one undo step) | `TrackDrag.cs` |
| Click header eye/volume | show/hide or mute | `TrackHeaders.cs` |
| Click header trash | delete track | same |
| Click header rack toggle | open/collapse effect rows on that track | `Racks.cs` |
| Click add-track `+` under header column | menu: Video clip / Audio clip | `TimelineView.cs` |
| Blade tool (B) click | cut clip at the pointer (Shift: every track at that time); blade line follows the cursor | `Tools.cs` |
| Drag near an edge during a drag | auto-scroll: 48 px band, up to 22 px/tick, faster closer to the edge, drag follows | `DragFeedback.cs` |
| Right-click | target-specific context menu | `ContextMenus.cs` |

Hover states: clip edge → `SizeWestEast`; fade tip → `SizeWestEast`; marker → `Hand` (range edges
`SizeWestEast`); effect bar → `Hand` (edges `SizeWestEast`); panel/hovered buttons tint; clip card hover
reveals its `+`; tooltips for markers, racks, diamonds, effects and every toolbar button.

### 6.2 Context menus (right-click, by target)

| Target | Items |
|---|---|
| Clip | Cut / Copy / Paste / Duplicate; Split at playhead; Delete; Enable / disable; Freeze frame; Add marker at playhead; Copy to…/Reverse…; effect submenu (add effect, bypass); properties… |
| Track header | Add video track / Add audio track; Rename / Delete track; mute/visibility toggles; fit height |
| Ruler | Add marker at playhead; Add range at playhead; zoom to fit; paste |
| Empty space | Paste at the pointer; Select all; Add marker; new timeline? |
| Transition | Preview transition; Replace…; Remove; duration submenu |
| Marker | Jump to marker; Edit marker...; Split video at selected markers; Delete selected markers (danger); per-clip: clear generated beats / clear all markers |
| Effect bar / keyframe | (keyframe menu) Jump · Duplicate · Easing ▸ · Delete; (rack) toggle, remove, edit graph |
| Media card (library) | Add to the selected clip · Delete preset / Edit graph… · Duplicate · Export… · Delete |

(Exact label lists are built in `TimelineView.ContextMenus.cs` and the library XAML; all strings are
listed there verbatim and must be copied 1:1.)

### 6.3 Keyboard (rebindable, `Services/Config/ShortcutCatalog.cs`)

All are data: id · section · label · default gesture · command. Saved changes live in
`preferences.json → Keyboard.Shortcuts`. `Display()` renders `Ctrl+,` style names. Typable gestures
(letters, digits, Space, arrows, Home/End/PageUp/Down, Oem keys without Ctrl/Alt/Meta) are routed after
the focused control so text boxes and lists keep their keys.

| Section | Id | Label | Default |
|---|---|---|---|
| Project | `project.save` | Save | Ctrl+S |
| Project | `project.close` | Close project | Ctrl+W |
| Project | `app.preferences` | Preferences | Ctrl+, |
| Editing | `edit.undo` | Undo | Ctrl+Z |
| Editing | `edit.redo` | Redo | Ctrl+Y |
| Editing | `edit.split` | Split at playhead | Ctrl+B |
| Editing | `edit.splitAll` | Split every track at the playhead | Ctrl+Shift+B |
| Editing | `edit.trimStart` | Delete before the playhead | Q |
| Editing | `edit.trimEnd` | Delete after the playhead | W |
| Editing | `edit.freeze` | Freeze frame | (none) |
| Editing | `edit.group` | Group clips (move and cut together) | Ctrl+G |
| Editing | `edit.ungroup` | Ungroup clips | Ctrl+Shift+G |
| Editing | `edit.magnet` | Main track magnet on or off | (none) |
| Editing | `tool.select` | Select tool | A |
| Editing | `tool.blade` | Blade tool (click a clip to cut it) | B |
| Editing | `edit.copy` | Copy | Ctrl+C |
| Editing | `edit.cut` | Cut | Ctrl+X |
| Editing | `edit.paste` | Paste | Ctrl+V |
| Editing | `edit.duplicate` | Duplicate | Ctrl+D |
| Playback | `play.loop` | Loop playback | Ctrl+L |
| Playback | `play.frameBack` | Previous frame | Left |
| Playback | `play.frameForward` | Next frame | Right |
| Playback | `play.secondBack` | Back one second | Shift+Left |
| Playback | `play.secondForward` | Forward one second | Shift+Right |
| Playback | `play.shuttleForward` | Play forward (faster each press) | L |
| Playback | `play.shuttleBack` | Play backward (faster each press) | J |
| Playback | `play.shuttleStop` | Stop | K |
| Playback | `play.audioScrub` | Audio scrubbing on or off | Shift+S |
| Playback | `play.previousEdit` | Previous cut | Up |
| Playback | `play.nextEdit` | Next cut | Down |
| Playback | `play.start` | Go to the start | Home |
| Playback | `play.end` | Go to the end | End |
| View | `view.mediaPanel` | Show or hide the media panel | Ctrl+1 |
| View | `view.inspector` | Show or hide the inspector | Ctrl+2 |
| View | `view.zoomIn` | Zoom the timeline in | Ctrl++ |
| View | `view.zoomOut` | Zoom the timeline out | Ctrl+- |
| View | `view.zoomFit` | Zoom to fit the sequence | Shift+Z |

### 6.4 Keyboard (fixed, timeline/local — shown in the cheat sheet `ShortcutsDialog`)

| Keys | Action |
|---|---|
| Space | Play / pause (works anywhere in the editor, including popup roots, via `EditorTransportKeys`) |
| Delete | Delete selection leaving a gap; with a gap selected, close the gap |
| Shift+Delete | Delete the selected marker |
| M | Add a marker at the playhead |
| N | Snapping on/off |
| Ctrl+A | Select everything in the focused panel (timeline: all; markers panel: all markers) |
| Ctrl+Wheel | Zoom in/out |
| Wheel / Shift+Wheel | Scroll sideways (vertical track scroll for plain wheel) |
| Alt+Wheel | Taller / shorter tracks |
| Middle-drag | Pan the timeline |
| Ctrl+drag clip | Copy it |
| Ctrl+drag clip edge | Roll the cut |
| Alt+drag | Move without snapping |
| Shift+drag | Select with a box |
| Click a gap, Delete | Close the gap |
| Blade: click | Cut there (Shift: every track) |
| Escape | Put the blade away, else clear selection; cancels a copy drag |
| Ctrl+E | Enable / disable selected clips |
| Ctrl+N (Home) | New project |
| Enter (Home) | Open focused project |
| / (Home) | Focus search |
| Esc (Home) | Clear a focused search |

Focus rules worth copying: window `KeyBinding`s never fire when a text box owns the key or a
FigSlider/NumberField is focused with arrows/Home/End; timeline navigation keys (Up/Down/Home/End/
PageUp/PageDown) are tunnelled so they beat the ScrollViewer; graph editor and other
`Classes="no-transport"` roots suppress transport keys; while recording a shortcut in Preferences all
window bindings are suspended.

### 6.5 Drag-and-drop

Internal formats (`DragFormats.cs`, one shared `DataFormat<T>` per type):

| Format | Payload | Sources | Timeline drop behaviour |
|---|---|---|---|
| Media | `MediaAsset` | library media card (8 px drag threshold) | insert at the pointer, snapped; on the magnetic main track it goes to the nearest cut; ghost clip rect; empty-space fallback shows a red insert line; audio assets go to an audio track / new track |
| Effect | `EffectCatalogEntry` | effects library card | appended to the clip under the pointer (3 s default region if on a track), 3 px accent outline on the target clip + ghost bar |
| Preset | `EffectPreset` | effects library preset card | same as effect |
| Transition | `TransitionCatalogEntry` | transitions library card | applied at the cut nearest the pointer (within 15 px) or the nearest abutting edge of the clip under the pointer; cut marker + diamond shown |
| Text | `TextPreset` | text library card | new `TextClip` at the pointer (text-only track or the video track at the drop time) |

External files: the whole `DockPanel` is `AllowDrop`; `EditorView.OnFileDragOver/OnFileDrop` accept
dropped files and import them as media (video/audio/image), then place them. The timeline itself also
accepts the same `DataFormat`s and shows `_dropPreviewTrack/_dropPreviewTime` highlights.

### 6.6 Selection model (shared with Core)

`SelectionState` holds items keyed by `SelectionKey(group, id)` for `Clip`, `Track`, `Marker`,
`Transition`, effects (owners) — groups from `Fig.Core.Timeline.Editing.SelectionState`. Linked clips
(`LinkGroupId`, e.g. video + detached audio) act as one for move/trim/cut/delete. Multi-selection of
mixed kinds is allowed; inspector shows the sections shared by all selected facets.

## 7. Editor command surface (from `EditorViewModel*` partials)

Transport: play/pause, shuttle J/K/L (1×, 2×, 4×, 8× forward/back with stop), step frame/second,
jump start/end, previous/next edit point, loop, audio scrub, seek.
Editing: split, split all, trim start/end to playhead, freeze frame, ripple delete, lift delete,
delete selection, duplicate, copy/cut/paste, group/ungroup, enable/disable, reverse, magnet toggle,
blade toggle, select tool, close gap, add/remove track, move clips to track, pack track.
Marks: add marker at playhead, delete marker, create ranges from bookmarks, beat analysis.
Timelines: new/switch/rename/duplicate/delete timeline, frame rate, frame shape, zoom fit/in/out,
fit/taller/shorter tracks.
Media: import, collect sources, reveal in folder, generate proxy, remove media, add media to timeline,
import OTIO, save/save as/save as package, export, export marked ranges, export captions.
Captions: auto captions, export SRT/TXT, panel navigation/replace, split/merge captions.
Graph: open/close graph editor, new graph effect, import/export/duplicate/delete graph effect.
Jobs: toggle jobs window, cancel export.

## 8. Dialogs

### 8.1 Export (`Views/Export/ExportDialog.axaml`)

Window 560 w, `SizeToContent=Height`, MinHeight 420, `CanResize=False`, centred on owner, bg `#1D1E20`.
Styling: label 12 `#8A8A8A`, section 13 SemiBold, rule 1 px `#2a2d34`. Content margin 24, spacing 16.

Two modes (single export / marked-ranges batch):
- Header: title + subtitle or "Each checked range becomes a separate video file.", close ✕.
- Chapter mode: select-all row (checkbox + summary + colour chips + Invert), empty note, scrollable
  rows (checkbox, colour strip, name, editable file name, range `MM:SS`, duration, error note),
  "Create ranges from bookmarks…", "Output settings" + "Change settings…".
- Format block: "MP4 video" / "H.264 / AAC" + fps; Resolution preset ComboBox + Width/Height;
  size label; Quality `FigSlider` 0..10 + "Smaller file"/"Higher quality" + Advanced CRF NumericUpDown
  (0..51) + Encoder ComboBox (Automatic/GPU/CPU choices) + Orientation ComboBox (Turns).
- Destination: Output file textbox + "Browse…"; chapter mode: destination folder.
- Footer: validation message + Cancel + primary `ExportButtonText` (disabled unless `CanExport`).

### 8.2 Export jobs (`ExportJobsWindow.axaml`)

560 w, MinHeight 220, MaxHeight 620, size-to-content. Title "Export jobs"; empty state "Nothing is
exporting. Use Export in the toolbar to render the timeline."; per job: file name + status + Cancel,
`ProgressBar.job` (6 px, radius 3, `#6ea5ff`; done `#7FC8A0` on `#0e0f11`), error line; footer
"You can continue editing while these export." + Close.

### 8.3 Auto captions (`CaptionsDialog.axaml`)

520 w, size-to-content. Title "Auto captions" + the privacy note. Speech-model list (`#232427` cards,
name, description, state, Remove when installed) + models folder note. Options grid: Language,
"Each caption" (grouping), "Write the captions in English", "Only where the selected clips are".
Progress bar 4 px + status. Footer: Stop (working) / Cancel (idle) + primary `GenerateLabel`.

### 8.4 Bookmark ranges (`Views/Markers/BookmarkRangesDialog.axaml` + `BookmarkRangesViewModel`)

Plans ranges from timeline bookmarks: list of row previews (`PlannedRangeRow`), include-sequence-bounds
option, and create/cancel. Created ranges are range markers with `IncludeInExport` and `OutputName`.

### 8.5 Prompt / confirm (`Services/Dialogs.cs`, built in code)

Shared dark modal (`#1D1E20` surface, primary `#29466E`, danger fill `#7A2B2F`, muted `#8A8A8A`).
`PromptTextAsync(owner, title, label, initial, confirm)` — one-line input, blank falls back;
`ConfirmAsync(owner, title, message, confirm, danger)` — returns bool. Used for rename project, new
project ("New project" / "Project name" / "Untitled" / "Create"), delete project.

### 8.6 Keyboard shortcuts (`Views/Shell/ShortcutsDialog.cs`, built in code)

Window 460 w, `SizeToContent=Height`, non-resizable, bg `#1a1b1f`; scrollable stack margin 24 spacing 6;
heading 15 SemiBold; per section a muted 12 header (`#868a95`) then rows `Grid "170,*"`:
keys in monospace `#b8bbc4`, description default colour; Close button bottom-right (88 min).

## 9. Visual spec — tokens

### 9.1 Colour tokens (`Styles/FigTheme.axaml`, the app-wide set)

| Token | Value | Use |
|---|---|---|
| `SurfaceBg` | `#131417` | window/canvas background |
| `PanelBg` | `#1a1b1f` | panels, preference page |
| `CardBg` | `#212328` | cards, segmented control, clip cards |
| `CardBgAlt` | `#272a30` | secondary cards, selected media card |
| `ChipBg` | `#2a2d34` | chips, tool buttons, hover-lift, ghost buttons |
| `HoverBg` | `#30343b` | hover, selected segment |
| `PressedBg` | `#383c44` | pressed |
| `Border` | `#2d3037` | 1 px input outlines |
| `BorderSubtle` | transparent | panels are told apart by tone |
| `TextPrimary` | `#ececef` | primary text |
| `TextSecondary` | `#b8bbc4` | secondary text |
| `TextMuted` | `#868a95` | muted labels |
| `TextFaint` | `#5d616b` | hints, disabled |
| `Accent` | `#6ea5ff` | primary accent (blue) |
| `AccentSoft` | `#2a3d61` | checked/toggled fill |
| `AccentSofter` | `#222f48` | subtle accent fill |
| `AccentText` | `#9cc0ff` | accent text |
| `Danger` | `#ff7a7a` | destructive text |
| `DangerBg` / `DangerBgHover` | `#3a2226` / `#4a292e` | destructive button |
| `Warn` / `WarnBg` | `#e8b53a` / `#2a2512` | warnings |
| menu tokens | `#23252b` presenter, `#2f323a` border, hover `#323640`, pressed `#3a3f4a` | menus |

Extra literals used inline throughout (must be tokens in Rust):
`#0e0f11` / `#0b0c0e` (preview/picture wells), `#0e1626` (text on accent, e.g. Export label),
`#8bb8ff` (Export hover), `#d9534f` (close hover), `#272a30` / `#1b1d22` / `#25282e` (inputs),
`#2a3d61` (primary soft), `#d9e0e7` (selection), `#ffd60a` (snap guide / marker default),
`#5f9cf8` (editor accent), `#f0645a` (playhead), `#74d6a0`/`#8bc9a3` (audio), `#1679db` (video),
`#a84f7d` (text clip), `#5e57a8` (image clip), `#c79a3d` (effect bars), `#ff6b6b` (danger on dark).

`EditorTheme` (custom-drawn controls): `Surface #18191c`, `Card #202226`, `Border #34373c`,
`TextPrimary #d4d7dc`, `TextMuted #858b94`, `Accent #5795df`, `Playhead #f0645a`,
`TrackLane #202226`, `TrackLaneAlt #1d1f22`, `RulerBackground #181a1d`, `SelectedLaneTint #147b9fc6`.

### 9.2 Shape and spacing

Radii: panel **14**, card **10**, control **8**, menu **12**, chip pill **999**, clip **6**, toast 6,
preview well 12, caption/marker rows 6, small badges 3–7, number field 3, swatch 4.

Spacing habits: window padding 8; panel padding 10–12; card padding 12,10; control gap 6–10; toolbar
gaps 2 (buttons), 6–8 (button groups), 12 (delimiters); grid splitter 4 px; section rule 1 px.

### 9.3 Typography (Inter everywhere; `Typeface.Default` + `FormattedText` in custom draws)

| Role | Size | Weight | Colour |
|---|---|---|---|
| Panel titles ("Preview", "Inspector", project name) | 14 / 16 | SemiBold | TextPrimary |
| Timeline-tab / menu / button labels | 13 | Regular / Medium | TextSecondary → TextPrimary when active |
| Inspector section headers | 12 | SemiBold | TextSecondary |
| Body / values | 12 | Regular | TextSecondary |
| Field labels, toolbar hints | 11 | Regular | TextMuted |
| Clip labels, badges, timeline text | 10 | Regular | white / TextMuted |
| Toast | 13 | Regular | `#eeeeee` |
| App wordmark (loading) | 28 | Bold | `#cccccc` |
| Timecode / durations | 11 | monospace | TextSecondary |

### 9.4 Icons and assets

- Icon system: `StrokeIcon` control renders stroke-only SVGs from `avares://Fig.App/Assets/lucide/icons/`
  (Lucide, 1,756 files, 24×24 viewBox, `stroke="currentColor"`, `fill="none"`). `IconService` parses
  `<path>/<circle>/<line>/<rect>/<polyline|polygon>` into `StreamGeometry` and strokes them (never fills),
  cached per key. Aliases: `split→scissors`, `ripple→ungroup`, `lift→square-minus`, `marker→flag`,
  `skip-start→skip-back`, `skip-end→skip-forward`, `volume→volume-2`, `trash→trash-2`.
  Timeline/header drawing calls `IconService.DrawStroked(context, key, rect, brush, strokeWidth)`.
- Icon sizes in use: 11–14 (chips/menus), 14–16 (buttons), 18–19 (rail/tools), 20–28 (empty states),
  40 (home first-run).
- `Assets/fig-icon.png` (app icon in the title bar, 22×22, exported from `assets/icon/fig-mono.svg`),
  `Assets/fig.ico` (window/taskbar). Packaging icons under `packaging/linux/share/icons/hicolor`.
- Fonts: Inter (bundled `Avalonia.Fonts.Inter`, `WithInterFont()`); monospace = system monospace.
- Raster previews: catalog `EffectPreview` 96×64 cache, `TransitionPreview` 112×56 cache.

## 10. Rendering needs (what the Rust port must draw, and how fast)

| Surface | Requirement | Notes |
|---|---|---|
| Shell + editor chrome | 60 fps while dragging splitters/panels | normal retained UI |
| Timeline | 60 fps while panning/zooming/playing with thousands of clips; idle ~0 | fully custom-drawn, immediate-mode-friendly: one `Render` pass, per-clip culling, cached pens/texts/bitmaps/geometry |
| Timeline at scale | 10k+ clips must stay interactive; clips < 6 px wide draw as flat colour, < 24 px skip their labels; text cache capped at 4096 | measured in `assets/stress.png` context |
| Video preview | 30–60 fps decode + composite; GPU path composites decoded frames + effects + crops + transitions + blends directly on the Skia GPU canvas; CPU path writes BGRA into one reused `WriteableBitmap` | `PreviewSurface`, `GpuScene`, `GpuSceneRenderer`, `SkiaSceneRenderer`, `GpuEffects*` (shaders) |
| Filmstrips/waveforms | generated off the UI thread, drawn from cached bitmaps/geometry | `Fig.Core` generators + App caches |
| Overlays | gizmos and keyframe rulers drawn over the preview at pointer rate | `LayerGizmo`, `TransformOverlay`, `SwirlOverlay`, `PreviewOverlay<T>` |
| Graph canvas | node editor pan/zoom/wire at 60 fps, hundreds of nodes | `GraphCanvas`, custom drawn |
| Effect/transition thumbnails | rendered once per card, cached | `EffectPreview`, `TransitionPreview` |

Threading model: UI thread owns all view state; decode/composite on background threads; export runs
through a GPU job host (`GpuJobHost`) because the render thread owns the GL context; preview seeks are
coalesced by a playback engine (`PlaybackEngine`, `PlaybackEngine.Scrub` partial).

## 11. Port checklist

Complexity: **S** ≈ under a day, **M** ≈ 1–4 days, **L** ≈ 1–2+ weeks for a careful 1:1 port.

### 11.1 Shell & chrome

| Item | C# source | Cx | Dependencies | Risk for an immediate-mode/retained Rust UI |
|---|---|---|---|---|
| Window chrome (undecorated, drag, resize grips, min size, maximize) | `MainWindow.axaml(.cs)`, `TitleBar.axaml(.cs)` | M | windowing framework (rune/gpui) | framework may not expose hit-test drag regions / edge resize with element roles; must test compositor behaviour per OS |
| Title bar (brand, status text, 4 buttons + badge, 3 caption buttons) | `TitleBar.axaml` | S | icon renderer | text centering/ellipsis; badge overlays |
| Menu bar (4 menus, ~60 items, checkable items, accelerators, enable state) | `TitleBar.axaml` | M | menu/popup system | frameworks rarely ship menu bars; context-menu plumbing needed; accelerator text alignment |
| Toasts | `ToastHost.axaml` | S | animated overlay | fade/position animation |
| Loading view | `LoadingView.axaml` | S | indeterminate progress | |
| Preferences overlay + 7 pages, keyboard rebinding recorder | `PreferencesView.axaml`, `ViewModels/Preferences/*` | L | text input, dropdowns, key capture | recording keys requires intercepting before global bindings |
| Shortcuts cheat sheet | `ShortcutsDialog.cs` | S | | |
| Dialog primitives (prompt, confirm) | `Services/Dialogs.cs` | S | modal window | |

### 11.2 Screens

| Item | C# source | Cx | Dependencies | Risk |
|---|---|---|---|---|
| Home (grid/list, search, sort, cards, context menus, first-run) | `HomeView.axaml(.cs)`, `HomeViewModel.cs` | M | project store, thumbnail decode | card grid layout, hover/focus rings |
| Editor layout with 3 splitters + persisted fractions + collapse | `EditorView.axaml`, `WorkspaceLayout.cs` | M | splitview-style layout | exact star fractions/min sizes and collapsed rail widths |
| Library rail + 7 tabs (media/templates/text/transitions/effects/markers/captions) | `EditorView.axaml` 1014–1713, `EditorViewModel.Library.cs` + partials | L | virtualized lists, drag sources, image decode | 7 full panel UIs; virtualized wrap/grid lists; drag-source payload typing |
| Preview panel + transport bar | `PreviewView.axaml(.cs)`, `PreviewViewModel.cs` | M | video surface, sliders, combo | custom video surface integration is the make-or-break (see §10) |
| Inspector (11 sections, 5 param widget templates, keyframe buttons, colour picker) | `EditorView.axaml` 348–986, `PropertiesViewModel*`, `InspectorSections.cs` | L | color picker, sliders, number fields | colour picker needs building or a crate; facet-based tab switching |
| Timeline panel (tabs, toolbar, scroller) | `EditorView.axaml` 1824–2030 | M | | dense toolbar with many icon buttons/flyouts |
| Graph editor (full-screen node editor + right inspector) | `GraphEditorView.axaml(.cs)`, `GraphCanvas.cs`, `GraphEditorViewModel.cs` | L | node canvas, menus, split preview | self-contained canvas but 800+ lines of hit-testing/drawing; can be deferred |
| Export dialog (560 w, 2 modes, settings) | `ExportDialog.axaml`, `ExportDialogViewModel.cs` | M | numeric inputs, file picker | chapter-mode table |
| Export jobs window (2nd window) | `ExportJobsWindow.axaml` | S | multi-window | frameworks differ on multi-window |
| Auto captions dialog | `CaptionsDialog.axaml`, `CaptionsDialogViewModel.cs` | M | progress, lists | whisper integration is Core-side |
| Bookmark ranges dialog | `BookmarkRangesDialog.axaml` | S | | |

### 11.3 Timeline (priority; estimate ≈ 2.5–3× everything else combined)

| Item | C# source | Cx | Dependencies | Risk |
|---|---|---|---|---|
| Lane geometry & measure (variable lane heights, racks, text-only lanes, track scale) | `TimelineGeometry.cs`, `TimelineView.cs` | M | — | must match cached-height model to keep O(1) hit-tests |
| Lane/header drawing (grid, selection tint, header icons, add-track) | `TimelineView.Rendering.cs`, `TrackHeaders.cs` | M | icon renderer, text cache | |
| Clip drawing (fills, label strip, split gaps, shadows, selection, disabled veil, handles, slivers) | `Rendering.cs`, `ClipDrawing.cs` | L | rounded-rect draws, image draws | exact culling + sliver rules; text cache |
| Filmstrips / thumbnails | `ClipDrawing.cs` (DrawFilmstrip), Core generators | L | image decode, texturing | tiling by source time; cache invalidation |
| Waveforms | `ClipDrawing.cs` (DrawAudioWaveform/ShapeWave) | L | path building | per-pixel envelopes, geometry caching, speed-curve warping |
| Effect racks (bars, folding, rows, keyframe lanes, drag/move/resize) | `Racks.cs` (766) | L | | most intricate custom control; needs its own hit-test model |
| Ruler + snap grid + scale picking | `Ruler.cs`, `RulerCalculator` (Core) | M | text; frame-accurate time | label format switches at 1 s; pinned draw |
| Markers (3 levels, ranges, pennants, previews, drag) | `Markers.cs` (371) | M | | overlap/near-hit rules |
| Keyframe diamonds (all kinds) + shared menu | `Keyframes.cs`, `KeyframeMenu.cs` | M | popup menu | one abstraction must cover clip/effect/speed keyframes |
| Speed curve (velocity envelope) | `Velocity.cs` | M | path + log scale | dragging points vertically |
| Transitions (span tint, badge, drag resize, drop targeting) | `Rendering.cs`, `DragDrop.cs` | M | | |
| Clip drag/move (members, ghost, blocked state, spill to free track) | `TimelineView.cs`, `DragFeedback.cs`, `Magnet.cs` | L | input state machine | the hardest interaction logic: live edits + single undo step |
| Trim / roll / ripple live edits | `Resize.cs`, `Roll.cs`, `Magnet.cs` | L | | source-range math lives in Core (`TimelineEditor`), must be reused, not re-derived |
| Marquee / selection / copy-drag / track drag | `Selection.cs`, `Duplication.cs`, `TrackDrag.cs` | M | | |
| Blade tool, gap selection/close | `Tools.cs` | S | | |
| Viewport/zoom/scroll/pinned ruler/auto-page/auto-scroll | `Viewport.cs` | M | scroll container | pinned overlays inside scroll; custom horizontal scrollbar-less model |
| External + internal drag & drop (5 payload types) | `DragDrop.cs`, `EditorView.axaml.cs`, `DragFormats.cs` | M | framework DnD | file drops may need per-framework plumbing |
| Ripple slide animation (220 ms ease) | `Ripple.cs` | S | frame timer | |
| Context menus (7 target kinds) | `ContextMenus.cs` (313) | M | menu system | many strings, one-at-a-time menu |
| Text/number/slider widgets | `FigSlider.cs`, `NumberField.cs`, `FadeEnvelope.cs`, `KeyframeRuler.cs` | M | | each has scrub/keyboard/typing behaviour that must match |

### 11.4 Custom widgets

| Widget | Source | Cx | Behaviour summary |
|---|---|---|---|
| `FigSlider` | `Controls/FigSlider.cs` (255) | M | 3 px rail, 10 px thumb (13 hover), grab-the-thumb (no jump), click-rail jumps, Shift = 10× finer, double-click resets to `Default`, arrows step (Shift 10), Home/End ends, focus ring |
| `NumberField` | `Controls/NumberField.cs` (281) | M | reads as label, drag vertically to scrub (400 px = range, Shift fine, Ctrl coarse), click to type (Enter applies, Esc cancels, Up/Down step), format/units parsing |
| `KeyframeRuler` | `Controls/KeyframeRuler.cs` (255) | M | host keys on a ruler, 10 px diamonds, playhead, optional limit band, click seeks, double-click adds, right-click menu |
| `FadeEnvelope` | `Controls/FadeEnvelope.cs` (94) | S | mini fade shape preview (88 px tall in inspector) |
| `StrokeIcon` | `Controls/StrokeIcon.cs` | S | stroke-only SVG icon with brush + stroke width |
| `PreviewOverlay<T>` + `LayerGizmo` / `TransformOverlay` / `SwirlOverlay` | 4 files (161/258/168/…) | L | letterbox mapping, selection boxes, move/scale/rotate handles, snap-to-centre, 15° shift steps, camera frame + keyframe path, swirl centre/radius/rotation handles |
| `EffectPreview` / `TransitionPreview` | 2 files | S | cached 96×64 / 112×56 real renders |
| `GpuJobHost` | `Rendering/GpuJobHost.cs` | S | 1 px control that owns GPU access for export |

## 12. Static strings worth copying verbatim (selection)

Tooltips/empty states are part of the look. Key ones:
"Drag to resize the panel", "Drag to resize the timeline", "Hide this panel (Ctrl+1)", "Hide the
inspector (Ctrl+2)", "Media panel (Ctrl+1)", "Inspector (Ctrl+2)", "Export jobs", "All projects
(Ctrl+W)", "Show controls", "Show or hide the zoom frame on the preview", "Drop media here or use
Import", "No media matches your search", "Drag media here to start your edit", "or double-click a clip
in the Media panel to add it at the playhead", "Click a clip to select it · Space plays and pauses",
"Nothing selected", "Select a clip, track, marker, transition, or media item to edit its settings.",
"No effects on this clip yet", "Add one below, or drag it from the Effects library onto the clip.",
"Double-click to reset · Shift-drag for fine control", "Drag up or down to adjust (Shift: fine) · click
to type", "Double-click for 1× · Shift-drag for fine control", "Drag onto a video track, or select a
track or clip and press +.", "Drag a transition onto a cut.", "Click a style to add it at the playhead,
or drag it onto a video track.", "Clip markers move with their clip.", "Ctrl-click to toggle ·
Shift-click for a range · Delete to remove", "Clip markers move with their clip.", "Enter: next caption ·
Shift+Enter: new line · ✂ splits at the cursor", "Download once, to ~/.fig/models/whisper",
"You can continue editing while these export.", "Nothing is exporting. Use Export in the toolbar to
render the timeline.", "Create your first project", "Start a new edit or import an existing timeline.",
"Check the spelling or clear the search.", "Something went wrong ({what}). Details are in the log
folder."

Non-obvious behaviours to preserve:
1. Shortcuts that are also typed characters are **not** window key bindings; they run only after the
   focused control declines the key (otherwise typing "k" in search would stop playback).
2. Opened projects maximize the window; entering the editor is the only place that happens.
3. The timeline has **no horizontal scrollbar**; horizontal position is `Viewport.ScrollTime`, driven by
   wheel/shift-wheel/middle-drag/auto-scroll, and the ruler is pinned while tracks scroll vertically.
4. Clips are drawn inset 6 px from the lane top, and marker pennants live in that inset; the body of a
   text clip starts at the top of the clip (no name strip).
5. Abutting clips lose 1 px at the join so each keeps its rounded corners.
6. Effect racks grow the lane height; below track scale 0.75 they fold to 6 px bars unless the track's
   rack toggle was clicked.
7. Ripple edits animate 220 ms; the edit itself is committed first and the animation only offsets the
   drawing.
8. One undo step per drag (`History.Group()`), including magnet packing and blocked-spill.
9. Dragging over the magnetic main track opens a hole at the nearest cut; clips dropped where a track
   has no room land on the nearest track of their kind that has room (a new one if needed).
10. Drag a clip with Alt to bypass snapping (frame grid only); snapping is on by default with a 9 px
    reach; the snap guide is the yellow `#ffd60a` line.
11. Ctrl-drag on an edge = roll; Ctrl-drag on a body = copy; Esc cancels copy drags.
12. `Space` is handled by the editor root (transport) so it works inside popup roots; text boxes and
    sliders keep their own keys.
13. The playback engine owns seeks; moving the playhead by key/button updates the timeline via
    `SetPlayheadFromPlayback` without re-triggering a seek.
14. Every colour/radius used by a screen is a token; the theme comments insist surfaces differ by tone,
    never by outlines or shadows, and text is never pure white.

## 13. Coverage statement and gaps

Read in full: MainWindow, TitleBar, ToastHost, LoadingView, HomeView (+cs), EditorView.axaml (all
2,042 lines) and its code-behind, PreviewView, FigTheme, FigControls, GraphEditorView, ExportDialog,
ExportJobsWindow, CaptionsDialog, PreferencesView (headings/rows), InspectorSections, WorkspaceLayout,
ShortcutCatalog, Preferences, EditorTheme, TimelineGeometry, TimelineViewport, GestureRegistry,
TimelineGesture, DragFormats, Dialogs, IconService, and the Timeline partials (TimelineView.cs,
Rendering, Theme, HitTesting, Ruler, TrackHeaders, Snapping, Tools, Keyboard, Viewport, Magnet,
DragFeedback, Racks, ClipDrawing, Markers, Keyframes, Resize, Ripple, Roll, Velocity, Carry, TrackDrag,
Duplication, DragDrop, ContextMenus, Selection — read as summaries where noted).

Not exhaustively transcribed (deliberately, they are data/logic the Rust port will re-read from source):
every `EditorViewModel` command body, every properties-view-model field label, template/effect/transition
catalog contents, `OtioImporter`, caption word timing, and the exact context-menu item lists (all in
`TimelineView.ContextMenus.cs`, ~313 lines — read before implementing menus). `BookmarkRangesDialog`
layout was read only via its view model. No screenshot of the Graph editor, auto-captions dialog or
bookmark-ranges dialog was captured before the time box closed; the harness can render them with the
same approach (`app.Editor.OpenGraphEditorCommand`, `new CaptionsDialog { DataContext = … }`,
`new BookmarkRangesDialog(timeline)`) plus `Editor.Selection.SelectClips(...)` for specific inspector
states.

Per-screen effort estimate for the Rust 1:1 port (based on the inventory above):
Shell+chrome 3–4 d · Home 2 d · Editor layout+library 5–7 d · Preview 4–6 d (framework-dependent) ·
Inspector 5–7 d · **Timeline 15–20 d** · Graph editor 4–6 d · Dialogs 3 d · theme/widgets 4–5 d.
Total ≈ 6–8 weeks for one experienced Rust UI engineer, with the timeline + preview as the two
long poles.
