using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fig.App.Services;
using Fig.App.ViewModels;
using Fig.Core.Input;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.App.Views
{
    public partial class TimelineView : Control
    {
        public static DataFormat<MediaAsset> MediaFormat => DragFormats.Media;
        public static DataFormat<EffectCatalogEntry> EffectFormat => DragFormats.Effect;
        public static DataFormat<EffectPreset> PresetFormat => DragFormats.Preset;
        public static DataFormat<TransitionCatalogEntry> TransitionFormat => DragFormats.Transition;

        public static readonly StyledProperty<TimelineEditor?> EditorProperty =
            AvaloniaProperty.Register<TimelineView, TimelineEditor?>(nameof(Editor));

        public static readonly StyledProperty<IReadOnlyDictionary<string, MediaAsset>?> MediaByIdProperty =
            AvaloniaProperty.Register<TimelineView, IReadOnlyDictionary<string, MediaAsset>?>(nameof(MediaById));

        public TimelineEditor? Editor
        {
            get => GetValue(EditorProperty);
            set => SetValue(EditorProperty, value);
        }

        public IReadOnlyDictionary<string, MediaAsset>? MediaById
        {
            get => GetValue(MediaByIdProperty);
            set => SetValue(MediaByIdProperty, value);
        }

        public static readonly StyledProperty<GestureRegistry?> GesturesProperty =
            AvaloniaProperty.Register<TimelineView, GestureRegistry?>(nameof(Gestures));

        public GestureRegistry? Gestures
        {
            get => GetValue(GesturesProperty);
            set => SetValue(GesturesProperty, value);
        }

        public TimelineViewport Viewport { get; } = new();

        private double _dropTimeSec = -1;

        // ripple-slide animation: draw at StartSec + offset, ease offset → 0
        private readonly Dictionary<string, (double FromOffsetSec, long StartTimestamp)> _rippleSlides = new();

        private EditorViewModel? _editorVm;

        private string? _selectedClipId
        {
            get => Editor?.Selection.SelectedClipIds.FirstOrDefault();
            set
            {
                if (Editor is null) return;
                if (value is null) Editor.Selection.Clear();
                else
                {
                    var ids = LinkedSelectionOn
                        ? Editor.LinkGroup(value).Select(c => c.Id).ToList()
                        : new List<string> { value };
                    Editor.Selection.SelectClips(ids.Count == 0 ? new List<string> { value } : ids);
                }
            }
        }

        private string? _selectedTrackId
        {
            get => Editor?.Selection.ActiveTrackId;
            set
            {
                if (Editor is not null) Editor.Selection.ActiveTrackId = value;
            }
        }

        private string? _selectedMarkerId => Editor?.Selection.SelectedMarkerId;

        // transition drag state
        private bool _draggingTransition;
        private string? _dragTransitionKey;
        private double _dragTransitionCutSec;
        private double _dragTransitionMaxSec;

        // pan state
        private bool _panning;
        private double _panStartX;
        private double _panStartScroll;

        // clip drag state
        private bool _draggingClip;
        private Clip? _dragClip;
        private Track? _dragOriginalTrack;
        private double _dragStartSec;
        private double _dragSrcInSec;
        private double _dragSrcOutSec;
        private double _dragPointerTime;
        private double _dragOriginalDurSec;
        private readonly List<Clip> _dragMembers = new();
        private IReadOnlyList<ClipGeometry> _dragBefore = Array.Empty<ClipGeometry>();
        private KeyModifiers _modifiersAtPress;
        private readonly Dictionary<string, double> _dragOriginals = new();
        private readonly Dictionary<string, double> _dragOriginalDurs = new();
        private readonly Dictionary<string, double> _dragSrcIns = new();
        private readonly Dictionary<string, double> _dragSrcOuts = new();
        // each dragged clip's speed curve as it was when the drag began, for clips with one
        private readonly Dictionary<string, List<KeyframePoint>> _dragCurves = new();

        // the zoom and scroll of each timeline that was open before
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Fig.Core.Timeline.Timeline, SavedView> _views = new();
        private sealed record SavedView(double PixelsPerSecond, double ScrollTime);

        public TimelineView()
        {
            ClipToBounds = true;
            Focusable = true;
            RenderOptions.SetBitmapInterpolationMode(this, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
            DragDrop.SetAllowDrop(this, true);
            DragDrop.AddDragOverHandler(this, OnDragOver);
            DragDrop.AddDropHandler(this, OnDrop);
            DragDrop.AddDragLeaveHandler(this, OnDragLeave);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == EditorProperty)
            {
                // each timeline keeps its own zoom and scroll while another one is open
                if (change.OldValue is TimelineEditor previous)
                    _views.AddOrUpdate(previous.Document, new SavedView(Viewport.PixelsPerSecond, Viewport.ScrollTime));
                if (change.NewValue is TimelineEditor next && _views.TryGetValue(next.Document, out var view))
                {
                    Viewport.SetPixelsPerSecond(view.PixelsPerSecond);
                    Viewport.SetScrollTime(view.ScrollTime);
                }
                _selectedGap = null;
                if (change.OldValue is TimelineEditor oldEditor)
                {
                    oldEditor.TimelineChanged -= OnTimelineChanged;
                    oldEditor.Selection.Changed -= OnTimelineSelectionChanged;
                    oldEditor.BeatPreviewChanged -= OnBeatPreviewChanged;
                }
                if (change.NewValue is TimelineEditor newEditor)
                {
                    newEditor.TimelineChanged += OnTimelineChanged;
                    newEditor.Selection.Changed += OnTimelineSelectionChanged;
                    newEditor.BeatPreviewChanged += OnBeatPreviewChanged;
                }
                // project/editor swap — drop any in-flight slide offsets
                ClearRippleSlides();
                InvalidateMeasure();
                InvalidateVisual();
            }
            else if (change.Property == MediaByIdProperty)
            {
                InvalidateVisual();
            }
            else if (change.Property == DataContextProperty)
            {
                BindEditorViewModel(DataContext as EditorViewModel);
            }
        }

        /// <summary>
        /// Height needed to show the ruler, every track lane, and the add-track control.
        /// Used so a parent <see cref="ScrollViewer"/> can scroll when tracks overflow.
        /// </summary>
        private double ComputeContentHeight()
        {
            var lanes = Editor is null || Editor.Document.Tracks.Count == 0
                ? TimelineGeometry.TrackHeight
                : Editor.Document.Tracks.Sum(TrackHeight);
            return RulerHeight + lanes + HeaderButtonSize + 12;
        }

        private double TrackHeight(Track track)
        {
            if (_laneHeights is not null && _laneHeights.TryGetValue(track, out var known))
                return known;
            var height = track.Clips.Count == 0 && track.Markers.Count == 0 && track.Effects.Count == 0
                ? EmptyTrackHeight
                : TimelineGeometry.LaneHeight(track, TrackScale) + TrackRackHeight(track);
            _laneHeights?.Add(track, height);
            return height;
        }

        // Lane heights depend on every clip's rack, and TrackTop sums them for each lane above, so within one frame
        // or one pointer move they are worked out once. Null outside those, where edits can change them.
        private Dictionary<Track, double>? _laneHeights;

        /// <summary>Remembers lane heights until the returned scope ends (a frame, a pointer move).</summary>
        private LaneHeightScope RememberLaneHeights() => new(this);

        private readonly struct LaneHeightScope : IDisposable
        {
            private readonly TimelineView _view;
            private readonly bool _owner;

            public LaneHeightScope(TimelineView view)
            {
                _view = view;
                _owner = view._laneHeights is null;
                if (_owner)
                    view._laneHeights = new Dictionary<Track, double>(ReferenceEqualityComparer.Instance);
            }

            public void Dispose()
            {
                if (_owner)
                {
                    _view._laneHeights = null;
                    _view._laneGeometry = default;
                }
            }
        }

        /// <summary>How tall pictures and waveforms are drawn (1 = standard); Alt+wheel and the toolbar change it.</summary>
        public double TrackScale { get; private set; } = 1;

        private double ClipTotal(Track track) => LabelHeight(track) + BodyHeight(track);

        private double BodyHeight(Track track) => LaneGeometry(track).Body;

        private double LabelHeight(Track track) => LaneGeometry(track).Label;

        // Whether a track is text-only (thin bars) takes a look at every clip on it, and drawing asks once per clip;
        // within a frame or a hover the answer can't change, so the last track asked about is remembered
        private (Track? Track, double Label, double Body) _laneGeometry;

        private (double Label, double Body) LaneGeometry(Track track)
        {
            if (_laneHeights is not null && ReferenceEquals(_laneGeometry.Track, track))
                return (_laneGeometry.Label, _laneGeometry.Body);
            var label = TimelineGeometry.LabelHeight(track);
            var body = TimelineGeometry.BodyHeight(track, TrackScale);
            if (_laneHeights is not null)
                _laneGeometry = (track, label, body);
            return (label, body);
        }

        private double SoundStrip => TimelineGeometry.AudioStripAt(TrackScale);

        private double TrackTop(int index)
        {
            if (Editor is null || index <= 0)
                return RulerHeight;

            var top = RulerHeight;
            for (var i = 0; i < Math.Min(index, Editor.Document.Tracks.Count); i++)
                top += TrackHeight(Editor.Document.Tracks[i]);
            return top;
        }

        private double TrackBottom(int index) =>
            TrackTop(index) + TrackHeight(Editor!.Document.Tracks[index]);

        protected override Size MeasureOverride(Size availableSize)
        {
            var height = ComputeContentHeight();
            // Outside a scroll viewer, fill the given slot; inside one, grow with track count.
            if (!double.IsInfinity(availableSize.Height))
                height = Math.Max(height, availableSize.Height);

            var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
            return new Size(width, height);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            BindEditorViewModel(DataContext as EditorViewModel);
            _scroller = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
            if (_scroller is not null)
                _scroller.ScrollChanged += OnScrollerChanged;
        }

        // The ruler and marker lane are pinned to the top of the pane: the track area scrolls under them.
        private ScrollViewer? _scroller;

        private void OnScrollerChanged(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

        /// <summary>How far the pane is scrolled; the ruler is drawn this far down so it stays at the top of the view.</summary>
        private double StickyOffset
            => _scroller is null ? 0 : Math.Clamp(_scroller.Offset.Y, 0, Math.Max(0, Bounds.Height - RulerHeight));

        /// <summary>
        /// A pointer position with the pinned ruler taken into account: over the ruler it is reported in the
        /// ruler's own coordinates (0 to <see cref="RulerHeight"/>), anywhere else it is unchanged.
        /// </summary>
        private Point RulerSpace(Point p)
        {
            var offset = StickyOffset;
            return offset > 0 && p.Y >= offset && p.Y < offset + RulerHeight ? new Point(p.X, p.Y - offset) : p;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            BindEditorViewModel(null);
            ClearRippleSlides();
            if (_scroller is not null)
                _scroller.ScrollChanged -= OnScrollerChanged;
            _scroller = null;
            base.OnDetachedFromVisualTree(e);
        }

        private void BindEditorViewModel(EditorViewModel? vm)
        {
            if (ReferenceEquals(_editorVm, vm))
                return;
            if (_editorVm is not null)
                _editorVm.RippleSlideStarted -= OnRippleSlideStarted;
            _editorVm = vm;
            if (_editorVm is not null)
                _editorVm.RippleSlideStarted += OnRippleSlideStarted;
        }

        private void OnTimelineSelectionChanged()
        {
            // picking anything else lets go of a selected gap
            if (Editor?.Selection.Count > 0)
                _selectedGap = null;
            InvalidateVisual();
        }

        private void OnBeatPreviewChanged() => InvalidateVisual();

        private void OnTimelineChanged()
        {
            // a selected gap may have closed, moved or filled
            _selectedGap = null;
            InvalidateMeasure();
            InvalidateVisual();
        }

        // ---- drag & drop ----

        private double XToTime(double x) => Viewport.XToTime(x - TrackHeaderWidth);

        private double XToTime(DragEventArgs e) => XToTime(e.GetPosition(this).X);

        private string? ClipName(Clip clip)
        {
            if (clip is TextClip text)
                return string.IsNullOrWhiteSpace(text.Text) ? "Text" : text.Text.Replace('\n', ' ');
            if (clip.MediaId is { } mediaId && MediaById is not null && MediaById.TryGetValue(mediaId, out var asset))
                return asset.FileName;
            return clip.DisplayName;
        }

        private static readonly Pen FilmstripSeamPen = new(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), 1);

        private bool IsMarkerHot(Marker m) => Editor?.Selection.IsMarkerSelected(m.Id) == true || _hoverMarkerId == m.Id;

        private enum DragMode { None, Move, ResizeStart, ResizeEnd, FadeIn, FadeOut }

        private DragMode _dragMode = DragMode.None;
        private enum EdgeKind { None, Left, Right, FadeIn, FadeOut }
        private EdgeKind _hoverEdge;
        // whether the fade under the pointer, or being dragged, is the picture's or a video's own sound's
        private FadeOf _hoverFadeOf;
        private FadeOf _dragFadeOf;

        private const double TrimEdgePx = 6;
        private const double FadeZonePx = 14;

        private static readonly IBrush AudioBodyBrush = new SolidColorBrush(Color.Parse("#15241d"));
        private static readonly IBrush AudioWaveFill = new SolidColorBrush(Color.Parse("#74d6a0"));
        private static readonly Pen AudioCenterPen = new(new SolidColorBrush(Color.FromArgb(70, 0x74, 0xd6, 0xa0)), 1);

        // header button columns: [0] = visibility (video) or mute (audio), [1] = mute (video, which has sound too),
        // then delete
        private const int HeaderToggleColumn = 0;
        private const int HeaderMuteColumn = 1;
        private const int HeaderRackColumn = 9;

        /// <summary>Where a track's delete button is: after its toggles.</summary>
        private static int DeleteColumn(Track track) => track.Kind == TrackKind.Video ? 2 : 1;

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            _openMenu?.Close();   // a press anywhere else closes the menu
            NoteModifiers(e.KeyModifiers);
            _modifiersAtPress = e.KeyModifiers;
            if (Editor is null)
                return;

            Focus();
            var pos = RulerSpace(e.GetPosition(this));

            // right-click: context menu (add marker / enable-disable / delete marker)
            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            {
                // a keyframe diamond, of any kind, has the same menu
                if (!TryOpenDiamondMenu(pos))
                    OpenContextMenu(pos);
                e.Handled = true;
                return;
            }

            // a keyframe diamond, of any kind: pick it, drag it, Ctrl-drag a copy
            if (HandleDiamondPress(e, pos))
            {
                e.Handled = true;
                return;
            }

            // Ctrl / Cmd on a trim edge rolls the cut instead (not a copy-drag or a selection box)
            _rollEdit = IsRollPress(e, pos);

            if (!_rollEdit && TryArmCopyDrag(e, pos))
            {
                e.Handled = true;
                return;
            }

            // a velocity edit's speed line: double-click it to add a point
            if (HandleSpeedPress(e, pos))
            {
                e.Handled = true;
                return;
            }

            // effect rack under a clip: expand/collapse, select, drag to move or resize
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
                && HitTestRack(pos) is { } rackHit && HandleRackPress(rackHit, pos, e.ClickCount))
            {
                e.Handled = true;
                return;
            }

            // middle-button pan (time axis). Always available — not gated on Ctrl.
            if (e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
            {
                var panGesture = Gestures?.Resolve(new GesturePattern { Button = Fig.Core.Input.MouseButton.Middle })
                    ?? TimelineGesture.Pan;
                if (panGesture == TimelineGesture.Pan || Gestures is null)
                {
                    _panning = true;
                    _panStartX = pos.X;
                    _panStartScroll = Viewport.ScrollTime;
                    e.Pointer.Capture(this);
                    e.Handled = true;
                    return;
                }
            }

            // transition badge hit-test: select + drag to resize; double-click seeks to the cut
            var transitionHit = HitTestTransition(pos);
            if (transitionHit is not null)
            {
                if (e.ClickCount >= 2)
                    SetPlayhead(Editor.SnapTime(Math.Max(0, transitionHit.CutSec)));
                Editor.Selection.SelectTransition(transitionHit.Key);
                _dragTransitionKey = transitionHit.Key;
                _dragTransitionCutSec = transitionHit.CutSec;
                _dragTransitionMaxSec = Math.Max(0.05,
                    Editor.GetMaxTransitionDuration(transitionHit.LeftClipId, transitionHit.RightClipId));
                _draggingTransition = true;
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            // keyframe diamond hit-test: seek the playhead to it
            // marker hit-test: takes priority over playhead drag (ruler) and clip drag.
            // double-click on a marker seeks the playhead to it.
            var markerHit = HitTestMarker(pos);
            if (markerHit is not null)
            {
                if (e.ClickCount >= 2)
                    SetPlayhead(Editor.SnapTime(Math.Max(0, AbsoluteMarkerTime(markerHit))));
                bool toggleMarker = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
                bool extendMarkers = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                Editor.SelectMarker(markerHit.Marker.Id, toggleMarker, extendMarkers);
                if (toggleMarker || extendMarkers)
                {
                    InvalidateVisual();
                    e.Handled = true;
                    return;
                }
                _dragMarkerId = markerHit.Marker.Id;
                _dragMarkerStartSec = AbsoluteMarkerTime(markerHit);
                _dragRangeGrab = HitTestRangeGrab(markerHit.Marker, pos);
                _dragRangeOffsetSec = XToTime(pos.X) - markerHit.Marker.StartSec;
                _draggingMarker = true;
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            // playhead drag: click in ruler area
            if (pos.Y <= RulerHeight)
            {
                _draggingPlayhead = true;
                SetPlayhead(Snap(XToTime(pos.X), includePlayhead: false));
                e.Handled = true;
                return;
            }

            if (pos.X <= TrackHeaderWidth)
            {
                var track = HitTestTrack(pos.Y);
                if (track is not null)
                {
                    var toggleRect = HeaderButtonRect(track, HeaderToggleColumn);
                    var delRect = HeaderButtonRect(track, DeleteColumn(track));
                    if (track.Kind == TrackKind.Video && HeaderButtonRect(track, HeaderMuteColumn).Contains(pos))
                    {
                        // a video track's sound
                        Editor.SetTrackMuted(track.Id, !track.Muted);
                        InvalidateVisual();
                        e.Handled = true;
                        return;
                    }
                    if (TrackHasRack(track) && RackToggleRect(track).Contains(pos))
                    {
                        SetTrackRackExpanded(track.Id, !IsTrackRackExpanded(track));
                        e.Handled = true;
                        return;
                    }
                    if (toggleRect.Contains(pos))
                    {
                        // video -> visibility toggle; audio -> mute toggle
                        if (track.Kind == TrackKind.Video)
                            Editor.SetTrackVisible(track.Id, !track.Visible);
                        else
                            Editor.SetTrackMuted(track.Id, !track.Muted);
                        InvalidateVisual();
                        e.Handled = true;
                        return;
                    }
                    if (delRect.Contains(pos))
                    {
                        Editor.RemoveTrack(track.Id);
                        _selectedTrackId = null;
                        InvalidateVisual();
                        e.Handled = true;
                        return;
                    }
                    _selectedClipId = null;
                    _selectedTrackId = track.Id;
                    // dragging the header up or down moves the track
                    if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                        ArmTrackDrag(track, pos, e.Pointer);
                    InvalidateVisual();
                    e.Handled = true;
                    return;
                }
            }

            // add-track button at bottom of header column: ask which kind first
            var addRect = AddTrackButtonRect();
            if (addRect.Contains(pos))
            {
                var menu = new ContextMenu
                {
                    Items =
                    {
                        new MenuItem { Header = "Video clip" },
                        new MenuItem { Header = "Audio clip" },
                    },
                };
                if (menu.Items[0] is MenuItem addVideo)
                    addVideo.Click += (_, _) =>
                    {
                        Editor.AddTrack(TrackKind.Video);
                        InvalidateVisual();
                    };
                if (menu.Items[1] is MenuItem addAudio)
                    addAudio.Click += (_, _) =>
                    {
                        Editor.AddTrack(TrackKind.Audio);
                        InvalidateVisual();
                    };
                ShowMenu(menu);
                e.Handled = true;
                return;
            }

            // the blade cuts where it is pressed
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && BladePress(pos, e.KeyModifiers))
            {
                e.Handled = true;
                return;
            }

            // clip hit-testing: press on a clip starts move/resize drag
            var time = Viewport.XToTime(pos.X - TrackHeaderWidth);
            var clipTrack = HitTestTrack(pos.Y);
            if (clipTrack is not null)
            {
                var clip = ClipAt(pos, out _);
                if (clip is not null)
                {
                    // Shift or Ctrl held: draw a selection region from here (a plain click still adds the clip)
                    if (!_rollEdit && (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.KeyModifiers.HasFlag(KeyModifiers.Control)))
                    {
                        ArmMarquee(pos, e.KeyModifiers);
                        _marqueeClickClip = clip;
                        e.Pointer.Capture(this);
                        InvalidateVisual();
                        e.Handled = true;
                        return;
                    }
                    ClickSelectClip(clip, _rollEdit ? KeyModifiers.None : e.KeyModifiers);
                    _pressedPx = pos.X;
                    BeginClipDrag(clip, clipTrack, time, pos);
                    InvalidateVisual();
                    e.Handled = true;
                    return;
                }
            }

            // empty space: a click deselects (and selects the gap it was in), a drag draws a selection box
            NotePressGap(clipTrack, time);
            ArmMarquee(pos, e.KeyModifiers);
            e.Pointer.Capture(this);
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            NoteModifiers(e.KeyModifiers);
            _pointerModifiers = e.KeyModifiers;

            if (Editor is null)
                return;

            // while something is being dragged the pointer keeps its content coordinates; hovering and
            // scrubbing the playhead use the pinned ruler's
            var pos = e.GetPosition(this);
            var leftPressed = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
            if (_draggingPlayhead || !leftPressed)
                pos = RulerSpace(pos);
            _lastDragPos = pos;
            if (leftPressed || DragInProgress)
            {
                PointerMovedAt(pos, leftPressed);
            }
            else
            {
                // hovering changes nothing, so lane heights hold for the whole move
                using var laneHeights = RememberLaneHeights();
                PointerMovedAt(pos, leftPressed);
            }
            // a drag held near an edge keeps scrolling
            if (leftPressed && DragInProgress)
                EnsureAutoScroll();
        }

        /// <summary>The pointer is at <paramref name="pos"/>: carries on whatever drag is under way, or updates hover.</summary>
        private void PointerMovedAt(Point pos, bool leftPressed)
        {
            if (Editor is null)
                return;

            if (UpdateCopyDrag(pos)) return;
            if (UpdateTrackDrag(pos)) return;

            if (_panning)
            {
                var dx = pos.X - _panStartX;
                Viewport.SetScrollTime(_panStartScroll - dx / Viewport.PixelsPerSecond);
                InvalidateVisual();
                return;
            }

            if (_draggingTransition && _dragTransitionKey is not null)
            {
                var cutX = TrackHeaderWidth + Viewport.TimeToX(_dragTransitionCutSec);
                var dx = Math.Abs(pos.X - cutX) / Math.Max(1, Viewport.PixelsPerSecond);
                var dur = Math.Clamp(2 * dx, 0.05, _dragTransitionMaxSec);
                Editor.SetTransitionDuration(_dragTransitionKey, Editor.SnapTime(dur));
                InvalidateVisual();
                return;
            }

            if (UpdateDiamondDrag(pos))
                return;

            if (_rackDrag is not null)
            {
                UpdateRackDrag(pos);
                return;
            }

            if (UpdateMarquee(pos))
                return;

            if (_draggingMarker && _dragMarkerId is not null)
            {
                var loc = Editor.FindMarker(_dragMarkerId);
                if (loc is null)
                {
                    _draggingMarker = false;
                    return;
                }
                if (loc.Marker.IsRange && _dragRangeGrab != RangeGrab.None)
                {
                    var t = Snap(Math.Max(0, XToTime(pos.X)), null, loc.Marker.Id);
                    var m = loc.Marker;
                    const double minLength = 0.1;
                    switch (_dragRangeGrab)
                    {
                        case RangeGrab.Start:
                        {
                            var start = Math.Min(t, m.EndSec - minLength);
                            Editor.EditMarker(m.Id, new MarkerEdit(StartSec: start, DurSec: m.EndSec - start));
                            break;
                        }
                        case RangeGrab.End:
                            Editor.EditMarker(m.Id, new MarkerEdit(DurSec: Math.Max(minLength, t - m.StartSec)));
                            break;
                        default:
                            Editor.EditMarker(m.Id, new MarkerEdit(
                                StartSec: Snap(Math.Max(0, XToTime(pos.X) - _dragRangeOffsetSec), null, m.Id)));
                            break;
                    }
                    InvalidateVisual();
                    return;
                }
                var raw = loc.Clip is not null ? XToTime(pos.X) - loc.Clip.StartSec : XToTime(pos.X);
                Editor.MoveMarker(_dragMarkerId, Snap(Math.Max(0, raw), null, _dragMarkerId));
                InvalidateVisual();
                return;
            }

            if (_draggingPlayhead)
            {
                SetPlayhead(Math.Max(0, Snap(XToTime(pos.X), includePlayhead: false)));
                return;
            }

            if (_draggingClip && _dragClip is not null)
            {
                var pointerTime = Math.Max(0, Viewport.XToTime(pos.X - TrackHeaderWidth));
                var deltaTime = pointerTime - _dragPointerTime;

                switch (_dragMode)
                {
                    case DragMode.Move:
                    {
                        // snap the grabbed clip, then move everything acting with it by the same amount
                        var origin = _dragOriginals[_dragClip.Id];
                        var members = _dragMembers.Select(m => m.Id).ToHashSet();
                        var rawStart = Math.Max(0, origin + deltaTime);
                        var snappedStart = Snap(rawStart, _dragClip.Id, excludeClips: members);
                        var delta = snappedStart - origin;
                        var earliest = _dragOriginals.Values.Min();
                        delta = Math.Max(delta, -earliest);                      // nothing may go before the start

                        var targets = _dragMembers.ToDictionary(m => m.Id, m => _dragOriginals[m.Id] + delta);
                        _ghostDelta = delta;
                        _ghostTrackId = null;

                        // over the magnetic main track the clips float and the track opens a hole for them
                        var hoverTrack = HitTestTrack(pos.Y);
                        if (MagnetMove(hoverTrack, rawStart - origin))
                        {
                            Carry(_dragClip.StartSec - origin);
                            break;
                        }

                        // over another track of the same kind: the clips go there, at the time the pointer puts them
                        var currentTrack = Editor.Document.Tracks.FirstOrDefault(t => t.Clips.Contains(_dragClip));
                        if (hoverTrack is not null && currentTrack is not null && hoverTrack.Id != currentTrack.Id
                            && hoverTrack.Kind == currentTrack.Kind)
                        {
                            _ghostBlocked = !Editor.MoveClipsToTrack(targets.Keys, hoverTrack.Id, targets);
                            // no room there: the clips wait where they last fit; the ghost shows the spot on that track,
                            // and letting go puts them on a track that has room
                            if (_ghostBlocked)
                            {
                                _ghostTrackId = hoverTrack.Id;
                                (_blockedTargets, _blockedNearTrackId) = (targets, hoverTrack.Id);
                            }
                            else
                            {
                                _dragOriginalTrack = hoverTrack;
                            }
                            PackMagnetAfterMove();
                            Carry(_dragClip.StartSec - origin);
                            break;
                        }

                        // no room there: the clips wait where they last fit, and a ghost shows where they'd go
                        _ghostBlocked = Editor.WouldOverlapMove(targets);
                        if (_ghostBlocked)
                            (_blockedTargets, _blockedNearTrackId) = (targets, currentTrack?.Id);
                        else
                            foreach (var m in _dragMembers)
                                m.StartSec = targets[m.Id];
                        PackMagnetAfterMove();
                        // selected effect bars come along by however far the clips really moved
                        Carry(_dragClip.StartSec - origin);
                        break;
                    }
                    case DragMode.ResizeStart:
                    {
                        // Ctrl-held: a touching neighbour rolls with the cut; otherwise stop at the clip before
                        ApplyLiveResizeStart(deltaTime);
                        var moved = _dragClip.StartSec - _dragOriginals[_dragClip.Id];
                        if (Math.Abs(ClampRoll(moved) - moved) > 1e-9)
                            ApplyLiveResizeStart(deltaTime + ClampRoll(moved) - moved);
                        ApplyRoll(atEnd: false);
                        PackMagnetLive();
                        break;
                    }
                    case DragMode.ResizeEnd:
                    {
                        ApplyLiveResizeEnd(deltaTime);
                        var moved = _dragClip.DurSec - _dragOriginalDurs[_dragClip.Id];
                        if (Math.Abs(ClampRoll(moved) - moved) > 1e-9)
                            ApplyLiveResizeEnd(deltaTime + ClampRoll(moved) - moved);
                        ApplyRoll(atEnd: true);
                        PackMagnetLive();
                        break;
                    }
                    case DragMode.FadeIn:
                    {
                        var fade = Math.Max(0, pointerTime - _dragClip.StartSec);
                        Editor.SetFadeIn(_dragClip.Id, fade, _dragFadeOf);
                        break;
                    }
                    case DragMode.FadeOut:
                    {
                        var end = _dragClip.StartSec + _dragClip.DurSec;
                        var fade = Math.Max(0, end - pointerTime);
                        Editor.SetFadeOut(_dragClip.Id, fade, _dragFadeOf);
                        break;
                    }
                }
                InvalidateVisual();
                return;
            }

            // idle: the blade follows the pointer
            if (UpdateBladeHover(pos, _pointerModifiers))
                return;

            // idle: a keyframe diamond, of any kind, highlights and says what it does
            if (UpdateDiamondHover(pos))
                return;

            // idle: effect bars show what they are and how to use them
            if (HitTestRack(pos) is { } rackOver)
            {
                Cursor = rackOver.Part is RackPart.Left or RackPart.Right
                    ? new Cursor(StandardCursorType.SizeWestEast)
                    : new Cursor(StandardCursorType.Hand);
                ToolTip.SetTip(this, DescribeRack(rackOver));
                return;
            }

            // idle: markers highlight, show their name and time, and use a hand (or resize) cursor
            var hoverMarker = HitTestMarker(pos);
            var hoverMarkerId = hoverMarker?.Marker.Id;
            if (hoverMarkerId != _hoverMarkerId)
            {
                _hoverMarkerId = hoverMarkerId;
                ToolTip.SetTip(this, hoverMarker is null ? null : DescribeMarker(hoverMarker));
                InvalidateVisual();
            }
            if (hoverMarker is not null)
            {
                Cursor = hoverMarker.Marker.IsRange && HitTestRangeGrab(hoverMarker.Marker, pos) is RangeGrab.Start or RangeGrab.End
                    ? new Cursor(StandardCursorType.SizeWestEast)
                    : new Cursor(StandardCursorType.Hand);
                return;
            }

            // idle: reflect resize / fade affordance when hovering a clip edge
            var overEdge = HoverClipEdge(pos, out var overFadeOf);
            Cursor = overEdge switch
            {
                EdgeKind.Left or EdgeKind.Right or EdgeKind.FadeIn or EdgeKind.FadeOut
                    => new Cursor(StandardCursorType.SizeWestEast),
                _ => Cursor.Default,
            };
            var hoverChanged = _hoverEdge != overEdge || _hoverFadeOf != overFadeOf;
            _hoverEdge = overEdge;
            _hoverFadeOf = overFadeOf;

            // header button hover (for delete/toggle affordance)
            var newHeaderHover = HoverHeaderButton(pos);
            if (newHeaderHover != _hoverHeaderButton)
            {
                _hoverHeaderButton = newHeaderHover;
                hoverChanged = true;
            }

            if (hoverChanged)
                InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _hoverEdge = EdgeKind.None;
            _hoverHeaderButton = null;
            Cursor = Cursor.Default;
            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (FinishCopyDrag(e) || EndDiamondDrag(e) || EndTrackDrag(e))
            {
                e.Handled = true;
                return;
            }
            ClearSnapGuide();

            FinishMarquee(e.KeyModifiers);
            if (e.Pointer.Captured == this)
                e.Pointer.Capture(null);

            if (_draggingClip && _dragClip is not null)
            {
                // the drag changed clips live; record it now as a single undo step, with the clips that had no room
                // where they were let go put on a track that has
                if (Editor is not null)
                    using (Editor.History.Group())
                    {
                        FinishMagnetDrag();
                        Editor.CommitClipGeometry(_dragBefore);
                        SpillBlockedClips();
                    }
                _draggingClip = false;
                _ghostBlocked = false;
                _magnetTrack = null;
                EndCarry();
                // clicking (not dragging) a clip inside a multi-selection narrows the selection to it
                if (_collapseToClipId is { } only && Math.Abs(_pressedPx - e.GetPosition(this).X) < MarqueeStartPx
                    && Editor is not null && Editor.FindClipForView(only) is { } c)
                    Editor.Selection.SelectClips(WithLinked(c, e.KeyModifiers).Select(x => x.Id));
                _collapseToClipId = null;
                InvalidateVisual();
            }
            EndRackDrag();
            _draggingMarker = false;
            _dragMarkerId = null;
            _dragRangeGrab = RangeGrab.None;
            _draggingTransition = false;
            _dragTransitionKey = null;
            _draggingPlayhead = false;
            if (_panning)
            {
                _panning = false;
                if (e.Pointer.Captured == this)
                    e.Pointer.Capture(null);
            }
        }

        private void BeginClipDrag(Clip clip, Track track, double time, Point pos)
        {
            var pointerX = pos.X;

            _dragClip = clip;
            _dragOriginalTrack = track;
            _dragStartSec = clip.StartSec;
            _dragOriginalDurSec = clip.DurSec;   // <-- capture fixed baseline
            _dragSrcInSec = clip.SourceIn;
            _dragSrcOutSec = clip.SourceOut;
            _dragPointerTime = Viewport.XToTime(pointerX - TrackHeaderWidth);

            var edge = EdgeAt(clip, track, pos, out _dragFadeOf);
            _dragMode = edge switch
            {
                EdgeKind.Left => DragMode.ResizeStart,
                EdgeKind.Right => DragMode.ResizeEnd,
                EdgeKind.FadeIn => DragMode.FadeIn,
                EdgeKind.FadeOut => DragMode.FadeOut,
                _ => DragMode.Move,
            };

            // who acts: a move carries everything selected; a trim or fade acts on the clip and its partners
            _dragMembers.Clear();
            if (_dragMode == DragMode.Move && Editor!.Selection.IsSelected(clip.Id))
            {
                foreach (var id in Editor.Selection.SelectedClipIds)
                    if (Editor.FindClipForView(id) is { } selected)
                        _dragMembers.Add(selected);
            }
            if (_dragMembers.Count == 0)
                _dragMembers.AddRange(WithLinked(clip, _modifiersAtPress));

            _dragOriginals.Clear();
            _dragOriginalDurs.Clear();
            _dragSrcIns.Clear();
            _dragSrcOuts.Clear();
            _dragCurves.Clear();
            foreach (var c in _dragMembers)
            {
                if (ClipTime.Curve(c) is { } curve)
                    _dragCurves[c.Id] = curve.ToList();
                _dragOriginals[c.Id] = c.StartSec;
                _dragOriginalDurs[c.Id] = c.DurSec;
                // every clip has a source range; one without a file (text, a still) plays itself, 0 → its length
                _dragSrcIns[c.Id] = c.SourceIn;
                _dragSrcOuts[c.Id] = c.SourceOut;
            }
            _dragBefore = _dragMembers.Select(Editor!.CaptureGeometry).ToList();
            RememberDragOrigins();
            _draggingClip = true;
            if (_dragMode == DragMode.Move)
                BeginCarryForClips(_dragMembers);
            if (_dragMode is DragMode.ResizeStart or DragMode.ResizeEnd)
                BeginRoll(atEnd: _dragMode == DragMode.ResizeEnd, roll: _rollEdit);
            BeginMagnet();
        }
    }
}
