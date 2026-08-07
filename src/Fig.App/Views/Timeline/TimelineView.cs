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
using Fig.App.Services;
using Fig.App.ViewModels;
using Fig.Core.Input;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.App.Views
{
    public class TimelineView : Control
    {
        public static DataFormat<MediaAsset> MediaFormat => DragFormats.Media;
        public static DataFormat<EffectCatalogEntry> EffectFormat => DragFormats.Effect;
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

        private readonly Dictionary<string, Bitmap> _filmstripCache = new();
        private double _dropTimeSec = -1;

        // ripple-slide animation: draw at StartSec + offset, ease offset → 0
        private readonly Dictionary<string, (double FromOffsetSec, long StartTimestamp)> _rippleSlides = new();
        private DispatcherTimer? _rippleTimer;
        private const double RippleAnimDurationMs = 220;

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
                    Editor.Selection.SelectOnly(value);
                    foreach (var c in Editor.LinkGroup(value))
                        Editor.Selection.Select(c.Id);
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

        // marker drag state
        private bool _draggingMarker;
        private string? _dragMarkerId;
        private double _dragMarkerStartSec;

        // transition drag state
        private bool _draggingTransition;
        private string? _dragTransitionKey;
        private double _dragTransitionCutSec;
        private double _dragTransitionMaxSec;

        private static double AbsoluteMarkerTime(MarkerLocation loc)
            => loc.Clip is not null ? loc.Clip.StartSec + loc.Marker.StartSec : loc.Marker.StartSec;

        // playhead
        public double PlayheadTimeSec { get; private set; }
        public event Action<double>? PlayheadChanged;
        private bool _draggingPlayhead;

        /// <summary>User-scrub: moves the playhead and notifies listeners (seek happens in the view model).</summary>
        private void SetPlayhead(double sec)
        {
            PlayheadTimeSec = sec;
            PlayheadChanged?.Invoke(sec);
            InvalidateVisual();
        }

        /// <summary>Playback-driven: moves the playhead visually without re-triggering a seek.</summary>
        public void SetPlayheadFromPlayback(double sec)
        {
            PlayheadTimeSec = sec;
            InvalidateVisual();
        }

        public void ZoomInAtPlayhead()
        {
            ZoomAtTimelineX(PlayheadTimeSecToX(), ZoomFactor);
        }

        public void ZoomOutAtPlayhead()
        {
            ZoomAtTimelineX(PlayheadTimeSecToX(), 1.0 / ZoomFactor);
        }

        public void ZoomToFitSequence(double endTimeSec)
        {
            var width = Math.Max(1, Bounds.Width - TrackHeaderWidth);
            var duration = Math.Max(0.5, endTimeSec);
            Viewport.SetPixelsPerSecond(Math.Clamp(width / duration, TimelineViewport.MinPixelsPerSecond, TimelineViewport.MaxPixelsPerSecond));
            Viewport.SetScrollTime(0);
            InvalidateVisual();
        }

        private double PlayheadTimeSecToX()
        {
            var width = Math.Max(1, Bounds.Width - TrackHeaderWidth);
            var x = Viewport.TimeToX(PlayheadTimeSec);
            return Math.Clamp(x, 0, width);
        }

        private void ZoomAtTimelineX(double cursorX, double factor)
        {
            Viewport.ZoomAt(cursorX, factor);
            InvalidateVisual();
        }

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
        private readonly Dictionary<string, double> _dragOriginals = new();
        private readonly Dictionary<string, double> _dragOriginalDurs = new();
        private readonly Dictionary<string, double> _dragSrcIns = new();
        private readonly Dictionary<string, double> _dragSrcOuts = new();

        // drop ghost (media -> timeline)
        private double _dropPreviewTime = -1;
        private Track? _dropPreviewTrack;
        private MediaAsset? _dropPreviewAsset;

        // catalog drop targets (effect → clip, transition → cut)
        private string? _dropEffectClipId;
        private double _dropTransitionCutSec = -1;
        private Track? _dropTransitionTrack;

        private static readonly IBrush SelectionBrush = EditorTheme.AccentBrush;

        private static readonly IBrush SurfaceBackground = EditorTheme.CardBrush;
        private static readonly IBrush TrackLaneBrush = EditorTheme.CardBrush;
        private static readonly IBrush TrackLaneAltBrush = new SolidColorBrush(EditorTheme.TrackLaneAlt);
        private static readonly IBrush RulerBackground = new SolidColorBrush(EditorTheme.RulerBackground);
        private static readonly IBrush BorderBrush = EditorTheme.BorderBrush;
        private static readonly IBrush RulerTickBrush = new SolidColorBrush(Color.Parse("#4a4a4a"));
        private static readonly IBrush RulerTextBrush = EditorTheme.TextMutedBrush;
        private static readonly IBrush SelectedLaneTintBrush = new SolidColorBrush(EditorTheme.SelectedLaneTint);
        private static readonly IBrush DeleteHoverBrush = new SolidColorBrush(Color.Parse("#5a1f1f"));

        // Clip fills
        private static readonly IBrush VideoBrush = new SolidColorBrush(Color.Parse("#3b82f6"));
        private static readonly IBrush AudioBrush = new SolidColorBrush(Color.Parse("#22c55e"));
        private static readonly IBrush AudioWaveformBrush = new SolidColorBrush(Color.Parse("#86efac"));
        private static readonly IBrush TextClipBrush = new SolidColorBrush(Color.Parse("#eab308"));

        private static readonly IBrush VideoBorder = new SolidColorBrush(Color.Parse("#1d4ed8"));
        private static readonly IBrush AudioBorder = new SolidColorBrush(Color.Parse("#15803d"));
        private static readonly IBrush TextBorder = new SolidColorBrush(Color.Parse("#a16207"));

        private static readonly IBrush ClipShadow = new SolidColorBrush(Color.Parse("#00000060"));
        private static readonly IBrush TrackHeaderTextBrush = EditorTheme.TextMutedBrush;
        private static readonly IBrush HeaderIconDimBrush = new SolidColorBrush(Color.Parse("#6a6a6a"));

        // transitions
        private static readonly IBrush TransitionBandBrush = new SolidColorBrush(Color.FromArgb(70, 0xe0, 0xa3, 0x08));
        private static readonly IBrush TransitionBandSelectedBrush = new SolidColorBrush(Color.FromArgb(120, 0xea, 0xb3, 0x08));
        private static readonly IBrush TransitionBadgeBrush = new SolidColorBrush(Color.FromArgb(235, 0xe0, 0xa3, 0x08));

        private const double RulerHeight = 24;
        private const double ClipCornerRadius = 4;
        private const double TrackHeaderWidth = 130;
        private const double ZoomFactor = 1.25;
        private const double HeaderButtonSize = 26;
        private const double HeaderButtonGap = 8;
        private const double HeaderPaddingLeft = 6;
        private const double TrackAccentBarWidth = 3;

        public TimelineView()
        {
            ClipToBounds = true;
            Focusable = true;
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
                if (change.OldValue is TimelineEditor oldEditor)
                    oldEditor.TimelineChanged -= OnTimelineChanged;
                if (change.NewValue is TimelineEditor newEditor)
                    newEditor.TimelineChanged += OnTimelineChanged;
                // project/editor swap — drop any in-flight slide offsets
                ClearRippleSlides();
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

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            BindEditorViewModel(DataContext as EditorViewModel);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            BindEditorViewModel(null);
            ClearRippleSlides();
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

        private void OnTimelineChanged() => InvalidateVisual();

        private void OnRippleSlideStarted(IReadOnlyList<RippleSlideDelta> deltas)
        {
            var now = Stopwatch.GetTimestamp();
            foreach (var d in deltas)
                _rippleSlides[d.ClipId] = (d.FromOffsetSec, now);
            EnsureRippleTimer();
            InvalidateVisual();
        }

        private void EnsureRippleTimer()
        {
            if (_rippleTimer is not null)
                return;
            _rippleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _rippleTimer.Tick += OnRippleTimerTick;
            _rippleTimer.Start();
        }

        private void OnRippleTimerTick(object? sender, EventArgs e)
        {
            if (_rippleSlides.Count == 0)
            {
                StopRippleTimer();
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var freq = (double)Stopwatch.Frequency;
            List<string>? done = null;

            foreach (var (id, state) in _rippleSlides)
            {
                var elapsedMs = (now - state.StartTimestamp) * 1000.0 / freq;
                if (elapsedMs >= RippleAnimDurationMs)
                    (done ??= new List<string>()).Add(id);
            }

            if (done is not null)
            {
                foreach (var id in done)
                    _rippleSlides.Remove(id);
            }

            InvalidateVisual();

            if (_rippleSlides.Count == 0)
                StopRippleTimer();
        }

        private void StopRippleTimer()
        {
            if (_rippleTimer is null)
                return;
            _rippleTimer.Tick -= OnRippleTimerTick;
            _rippleTimer.Stop();
            _rippleTimer = null;
        }

        private void ClearRippleSlides()
        {
            _rippleSlides.Clear();
            StopRippleTimer();
        }

        /// <summary>
        /// Current draw offset in seconds for a clip (eases from FromOffset → 0).
        /// Positive offset draws the clip to the right of its committed StartSec.
        /// </summary>
        private double GetRippleOffsetSec(string clipId)
        {
            if (!_rippleSlides.TryGetValue(clipId, out var state))
                return 0;

            var elapsedMs = (Stopwatch.GetTimestamp() - state.StartTimestamp) * 1000.0 / Stopwatch.Frequency;
            var t = Math.Clamp(elapsedMs / RippleAnimDurationMs, 0, 1);
            // ease-out cubic
            var eased = 1 - Math.Pow(1 - t, 3);
            return state.FromOffsetSec * (1 - eased);
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);

            var pattern = new GesturePattern
            {
                Wheel = true,
                WheelDir = e.Delta.Y > 0 ? WheelDirection.Up : WheelDirection.Down,
                Ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control),
                Shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift),
            };

            var gesture = Gestures?.Resolve(pattern) ?? TimelineGesture.None;
            var pos = e.GetPosition(this);

            switch (gesture)
            {
                case TimelineGesture.ZoomIn:
                    Viewport.ZoomAt(pos.X - TrackHeaderWidth, 1.25);
                    break;
                case TimelineGesture.ZoomOut:
                    Viewport.ZoomAt(pos.X - TrackHeaderWidth, 1 / 1.25);
                    break;
                case TimelineGesture.ScrollHorizontal:
                    Viewport.ScrollBy(e.Delta.X != 0 ? e.Delta.X : -e.Delta.Y);
                    break;
                case TimelineGesture.ScrollVertical:
                    Viewport.ScrollBy(e.Delta.Y);
                    break;
                default:
                    return;   // not handled -> don't mark handled
            }

            InvalidateVisual();
            e.Handled = true;
        }

        // ---- drag & drop ----

        private void ClearDropPreview()
        {
            _dropTimeSec = -1;
            _dropPreviewTime = -1;
            _dropPreviewTrack = null;
            _dropPreviewAsset = null;
            _dropEffectClipId = null;
            _dropTransitionCutSec = -1;
            _dropTransitionTrack = null;
        }

        private void OnDragOver(object? sender, DragEventArgs e)
        {
            if (Editor is null)
            {
                e.DragEffects = DragDropEffects.None;
                return;
            }

            var pos = e.GetPosition(this);
            var time = XToTime(pos.X);
            var track = HitTestTrack(pos.Y);

            if (e.DataTransfer.Contains(MediaFormat))
            {
                e.DragEffects = DragDropEffects.Copy;
                _dropPreviewTime = Editor.SnapTimeMagnetic(time);
                _dropPreviewAsset = e.DataTransfer.TryGetValue(MediaFormat);
                _dropPreviewTrack = track;
                _dropTimeSec = _dropPreviewTime;
                _dropEffectClipId = null;
                _dropTransitionCutSec = -1;
                _dropTransitionTrack = null;
                InvalidateVisual();
                return;
            }

            if (e.DataTransfer.Contains(EffectFormat))
            {
                _dropPreviewAsset = null;
                _dropPreviewTrack = null;
                _dropPreviewTime = -1;
                _dropTimeSec = -1;
                _dropTransitionCutSec = -1;
                _dropTransitionTrack = null;

                Clip? clip = null;
                if (track is not null)
                    clip = HitTestClip(track, time, pos.X);

                if (clip is VideoClip)
                {
                    e.DragEffects = DragDropEffects.Copy;
                    _dropEffectClipId = clip.Id;
                }
                else
                {
                    e.DragEffects = DragDropEffects.None;
                    _dropEffectClipId = null;
                }
                InvalidateVisual();
                return;
            }

            if (e.DataTransfer.Contains(TransitionFormat))
            {
                _dropPreviewAsset = null;
                _dropPreviewTrack = null;
                _dropPreviewTime = -1;
                _dropTimeSec = -1;
                _dropEffectClipId = null;

                var cut = track is not null ? FindCutNear(track, time) : null;
                if (cut is not null && track is not null)
                {
                    e.DragEffects = DragDropEffects.Copy;
                    _dropTransitionCutSec = cut.Value.Left.StartSec + cut.Value.Left.DurSec;
                    _dropTransitionTrack = track;
                }
                else
                {
                    e.DragEffects = DragDropEffects.None;
                    _dropTransitionCutSec = -1;
                    _dropTransitionTrack = null;
                }
                InvalidateVisual();
                return;
            }

            if (e.DataTransfer.Formats.Contains(Avalonia.Input.DataFormat.File))
            {
                e.DragEffects = DragDropEffects.Copy;
                return;
            }

            e.DragEffects = DragDropEffects.None;
        }

        private void OnDragLeave(object? sender, DragEventArgs e)
        {
            ClearDropPreview();
            InvalidateVisual();
        }

        private async void OnDrop(object? sender, DragEventArgs e)
        {
            if (Editor is null)
                return;

            if (e.DataTransfer.Contains(MediaFormat))
            {
                var asset = e.DataTransfer.TryGetValue(MediaFormat);
                if (asset is null)
                    return;

                var snapped = Editor.SnapTimeMagnetic(XToTime(e));
                var dropY = e.GetPosition(this).Y;
                var targetTrack = HitTestTrack(dropY);

                if (targetTrack is not null)
                    Editor.AddMediaLinked(asset, targetTrack.Id, snapped);
                else
                    Editor.AddMediaNewTracks(asset, snapped);

                ClearDropPreview();
                InvalidateVisual();
                return;
            }

            if (e.DataTransfer.Formats.Contains(DataFormat.File))
            {
                var files = e.DataTransfer.TryGetFiles();
                if (files is not null)
                {
                    var vm = DataContext as EditorViewModel;
                    var snapped = Editor.SnapTimeMagnetic(XToTime(e));
                    var dropY = e.GetPosition(this).Y;
                    var targetTrack = HitTestTrack(dropY);
                    foreach (var file in files)
                    {
                        var path = file.TryGetLocalPath();
                        if (path is null || vm is null)
                            continue;
                        var asset = await vm.ImportFileAsync(path);
                        if (asset is not null)
                        {
                            if (targetTrack is not null)
                                Editor.AddMediaLinked(asset, targetTrack.Id, snapped);
                            else
                                Editor.AddMediaNewTracks(asset, snapped);
                        }
                    }
                }
                ClearDropPreview();
                InvalidateVisual();
                return;
            }

            if (e.DataTransfer.Contains(EffectFormat))
            {
                var entry = e.DataTransfer.TryGetValue(EffectFormat);
                var pos = e.GetPosition(this);
                var track = HitTestTrack(pos.Y);
                var clip = track is not null ? HitTestClip(track, XToTime(pos.X), pos.X) : null;
                if (entry is not null && clip is VideoClip)
                {
                    Editor.AddEffect(clip.Id, entry.CreateInstance());
                    Editor.Selection.SelectOnly(clip.Id);
                }
                ClearDropPreview();
                InvalidateVisual();
                return;
            }

            if (e.DataTransfer.Contains(TransitionFormat))
            {
                var entry = e.DataTransfer.TryGetValue(TransitionFormat);
                var pos = e.GetPosition(this);
                var track = HitTestTrack(pos.Y);
                var cut = track is not null ? FindCutNear(track, XToTime(pos.X)) : null;
                if (entry is not null && cut is not null)
                {
                    Editor.ApplyTransitionAtCut(cut.Value.Left.Id, cut.Value.Right.Id, entry.CreateRef());
                    Editor.Selection.SelectOnly(cut.Value.Left.Id);
                }
                ClearDropPreview();
                InvalidateVisual();
            }
        }

        /// <summary>
        /// Finds an abutting cut on <paramref name="track"/> near <paramref name="timeSec"/>.
        /// Tolerance scales with zoom (~16px).
        /// </summary>
        private (Clip Left, Clip Right)? FindCutNear(Track track, double timeSec)
        {
            var tolSec = Math.Max(0.08, 16.0 / Math.Max(1, Viewport.PixelsPerSecond));
            var clips = track.Clips.OrderBy(c => c.StartSec).ToList();
            for (var i = 0; i < clips.Count - 1; i++)
            {
                var left = clips[i];
                var right = clips[i + 1];
                var cut = left.StartSec + left.DurSec;
                if (Math.Abs(right.StartSec - cut) > 1e-3)
                    continue;
                if (Math.Abs(timeSec - cut) <= tolSec)
                    return (left, right);
            }
            return null;
        }

        private double XToTime(double x) => Viewport.XToTime(x - TrackHeaderWidth);



        private double XToTime(DragEventArgs e) => XToTime(e.GetPosition(this).X);

        private string? ClipName(Clip clip)
        {
            if (clip.Kind == ClipKind.Text)
                return ((TextClip)clip).Text;

            if (clip is VideoClip vc && MediaById is not null && MediaById.TryGetValue(vc.SourceId, out var va))
                return va.FileName;
            if (clip is AudioClip ac && MediaById is not null && MediaById.TryGetValue(ac.SourceId, out var aa))
                return aa.FileName;
            return clip.Id;
        }

        private Bitmap? GetBitmap(string path)
        {
            if (_filmstripCache.TryGetValue(path, out var cached))
                return cached;
            if (!File.Exists(path))
                return null;
            try
            {
                var bitmap = new Bitmap(path);
                _filmstripCache[path] = bitmap;
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Tiles aspect-correct filmstrip frames edge-to-edge across the clip instead of
        /// stretching a single crop. Frames are indexed by source time so trimming/speed
        /// changes map to the right thumbnails, and the last partial tile is clipped
        /// rather than squeezed.
        /// </summary>
        private void DrawFilmstrip(DrawingContext context, Rect rect, VideoClip clip, MediaAsset asset, Bitmap strip)
        {
            if (asset.FilmstripFrameCount <= 0 || asset.FilmstripFrameIntervalSec <= 0)
                return;

            var frameW = asset.FilmstripFrameWidth;
            var frameH = asset.FilmstripFrameHeight;
            if (frameW <= 0 || frameH <= 0)
                return;

            var speed = clip.Speed <= 0 ? 1.0 : clip.Speed;
            var ratio = clip.SourceRate is { } sr ? sr.Fps / (Editor?.Document.Rate.Fps ?? 1) : 1.0;

            // each tile scaled to the clip height, preserving the source aspect ratio
            var destH = rect.Height;
            var destW = frameW * (destH / frameH);

            var (visLeft, visRight) = VisibleXRange(rect);
            if (visRight <= visLeft)
                return;

            using (context.PushClip(rect))
            {
                // align the start to a tile boundary so the source-frame mapping stays exact
                var tileIndexFromLeft = Math.Floor((visLeft - rect.X) / destW);
                var x = rect.X + tileIndexFromLeft * destW;
                while (x < visRight)
                {
                    var clipLocalSec = (x - rect.X) / (rect.Width / clip.DurSec);
                    var srcTimeSec = clip.SrcInSec + clipLocalSec * speed * ratio;

                    var frameIndex = (int)(srcTimeSec / asset.FilmstripFrameIntervalSec);
                    frameIndex = Math.Clamp(frameIndex, 0, asset.FilmstripFrameCount - 1);

                    var srcRect = new Rect(frameIndex * frameW, 0, frameW, frameH);
                    var destRect = new Rect(x, rect.Y, destW, destH);

                    context.DrawImage(strip, srcRect, destRect);

                    x += destW;
                }
            }
        }

        /// <summary>Horizontal span of <paramref name="rect"/> that is actually on screen, in viewport coordinates.</summary>
        private (double Left, double Right) VisibleXRange(Rect rect)
        {
            var left = Math.Max(rect.X, TrackHeaderWidth);
            var right = Math.Min(rect.X + rect.Width, Bounds.Width);
            return (left, right);
        }

        /// <summary>
        /// Renders the audio waveform directly from the decoded peak samples, one vertical
        /// mirrored line per pixel column across the clip body. No stretching: the source
        /// range (SrcIn..SrcOut) is mapped linearly to the visible width, so zooming in
        /// just spreads the same samples wider (more detail, never distorted).
        /// </summary>
        private void DrawResizeHandle(DrawingContext context, double x, double y, double height)
        {
            var handleBrush = new SolidColorBrush(Color.FromArgb(200, 0x4d, 0xa3, 0xff));
            var width = 3.0;
            context.DrawRectangle(handleBrush, null, new Rect(x - width / 2, y + 3, width, height - 6));
        }

        /// <summary>Draws diagonal opacity-ramp overlays for fade-in / fade-out durations.</summary>
        private void DrawFadeRamps(DrawingContext context, Rect rect, Clip clip)
        {
            var px = Viewport.PixelsPerSecond;
            var shade = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
            var line = new Pen(new SolidColorBrush(Color.FromArgb(200, 0xff, 0xff, 0xff)), 1);

            if (clip.FadeInSec > 1e-6)
            {
                var w = Math.Min(clip.FadeInSec * px, rect.Width);
                if (w > 0.5)
                {
                    var geo = new StreamGeometry();
                    using (var gc = geo.Open())
                    {
                        gc.BeginFigure(new Point(rect.X, rect.Bottom), true);
                        gc.LineTo(new Point(rect.X, rect.Y));
                        gc.LineTo(new Point(rect.X + w, rect.Y));
                        gc.EndFigure(true);
                    }
                    context.DrawGeometry(shade, null, geo);
                    context.DrawLine(line, new Point(rect.X, rect.Bottom), new Point(rect.X + w, rect.Y));
                }
            }

            if (clip.FadeOutSec > 1e-6)
            {
                var w = Math.Min(clip.FadeOutSec * px, rect.Width);
                if (w > 0.5)
                {
                    var geo = new StreamGeometry();
                    using (var gc = geo.Open())
                    {
                        gc.BeginFigure(new Point(rect.Right, rect.Bottom), true);
                        gc.LineTo(new Point(rect.Right, rect.Y));
                        gc.LineTo(new Point(rect.Right - w, rect.Y));
                        gc.EndFigure(true);
                    }
                    context.DrawGeometry(shade, null, geo);
                    context.DrawLine(line, new Point(rect.Right, rect.Bottom), new Point(rect.Right - w, rect.Y));
                }
            }
        }

        private readonly Dictionary<string, IBrush> _markerBrushes = new();

        private IBrush MarkerBrush(string color)
        {
            if (!_markerBrushes.TryGetValue(color, out var brush))
            {
                brush = new SolidColorBrush(Color.Parse(color));
                _markerBrushes[color] = brush;
            }
            return brush;
        }

        /// <summary>
        /// Draws a small colored tab above the clip for each marker that falls within
        /// its duration, plus a thin tick down into the label strip.
        /// </summary>
        private void DrawClipMarkers(DrawingContext context, double clipX, double clipTop, Clip clip)
        {
            if (clip.Markers.Count == 0)
                return;

            var px = Viewport.PixelsPerSecond;
            foreach (var marker in clip.Markers)
            {
                if (marker.StartSec < 0 || marker.StartSec > clip.DurSec)
                    continue;

                var mx = clipX + marker.StartSec * px;
                var brush = MarkerBrush(marker.Color);
                var selected = Editor?.Selection.SelectedMarkerId == marker.Id;
                context.DrawRectangle(brush,
                    selected ? new Pen(Brushes.White, 1.5) : null,
                    new Rect(mx - 3, clipTop - 7, 6, 8));
                context.DrawLine(new Pen(brush, selected ? 2 : 1), new Point(mx, clipTop), new Point(mx, clipTop + 7));
            }
        }

        /// <summary>Draws timeline markers as colored blocks in the ruler.</summary>
        private void DrawTimelineMarkers(DrawingContext context)
        {
            if (Editor is null || Editor.Document.Markers.Count == 0)
                return;

            foreach (var m in Editor.Document.Markers)
            {
                var mx = TrackHeaderWidth + Viewport.TimeToX(m.StartSec);
                if (mx < TrackHeaderWidth || mx > Bounds.Width)
                    continue;
                var selected = Editor.Selection.SelectedMarkerId == m.Id;
                context.DrawRectangle(
                    MarkerBrush(m.Color),
                    selected ? new Pen(Brushes.White, 1.5) : null,
                    new Rect(mx - 4, 2, 8, RulerHeight - 6));
            }
        }

        /// <summary>Draws track markers as colored diamonds at the bottom of the lane.</summary>
        private void DrawTrackMarkers(DrawingContext context, Track track, double top, double height)
        {
            if (track.Markers.Count == 0)
                return;

            foreach (var m in track.Markers)
            {
                var mx = TrackHeaderWidth + Viewport.TimeToX(m.StartSec);
                if (mx < TrackHeaderWidth || mx > Bounds.Width)
                    continue;

                var cy = top + height - 8;
                var selected = Editor?.Selection.SelectedMarkerId == m.Id;
                var diamond = new StreamGeometry();
                using (var gc = diamond.Open())
                {
                    gc.BeginFigure(new Point(mx, cy - 6), true);
                    gc.LineTo(new Point(mx + 5, cy));
                    gc.LineTo(new Point(mx, cy + 6));
                    gc.LineTo(new Point(mx - 5, cy));
                    gc.EndFigure(true);
                }
                context.DrawGeometry(MarkerBrush(m.Color),
                    selected ? new Pen(Brushes.White, 1.5) : null, diamond);
            }
        }

        /// <summary>
        /// Tints the portion of a clip body covered by a transition. Drawn once per side:
        /// the left clip gets [cut − D, cut], the right clip [cut, cut + D].
        /// </summary>
        private void DrawTransitionSpan(DrawingContext context, Rect rect, CutTransition t, bool isLeftClip, double px)
        {
            var cutX = TrackHeaderWidth + Viewport.TimeToX(t.CutSec);
            var d = t.DurationSec * px;
            var bandX = Math.Max(isLeftClip ? cutX - d : cutX, rect.X);
            var bandRight = Math.Min(isLeftClip ? cutX : cutX + d, rect.Right);
            if (bandRight - bandX < 0.5)
                return;
            var selected = Editor?.Selection.SelectedTransitionKey == t.Key;
            context.DrawRectangle(selected ? TransitionBandSelectedBrush : TransitionBandBrush, null,
                new Rect(bandX, rect.Y, bandRight - bandX, rect.Height));
        }

        /// <summary>Draws the transition badge (icon pill) centered on the cut, on the left clip.</summary>
        private void DrawTransitionBadge(DrawingContext context, CutTransition t, double bodyTop, double bodyBottom)
        {
            var cutX = TrackHeaderWidth + Viewport.TimeToX(t.CutSec);
            if (cutX < TrackHeaderWidth || cutX > Bounds.Width)
                return;

            var cy = (bodyTop + bodyBottom) / 2;
            var rect = new Rect(cutX - 13, cy - 9, 26, 18);
            var selected = Editor?.Selection.SelectedTransitionKey == t.Key;
            context.DrawRectangle(TransitionBadgeBrush,
                selected ? new Pen(Brushes.White, 1.5) : null,
                new RoundedRect(rect, 5));
            IconService.DrawStroked(context, "blend", rect.Inflate(-5), Brushes.White, 1.6);
        }

        /// <summary>Lays out one "folder bookmark" tab per effect on a clip's label strip.</summary>
        private static List<(Rect Rect, string EffectId, string Name)> EffectBookmarkLayout(Clip clip, Rect labelRect)
        {
            var list = new List<(Rect, string, string)>();
            if (clip.Effects.Count == 0)
                return list;

            var x = labelRect.X + 4;
            foreach (var effect in clip.Effects)
            {
                var name = EffectCatalog.Find(effect.TypeId)?.DisplayName ?? effect.TypeId;
                var text = new FormattedText(name, System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, Typeface.Default, 10, Brushes.White);
                var w = Math.Min(90, text.Width + 12);
                list.Add((new Rect(x, labelRect.Y + 1, w, labelRect.Height - 2), effect.Id, name));
                x += w + 3;
            }
            return list;
        }

        /// <summary>Draws folder-bookmark tabs for a clip's effects. Returns the total width so
        /// the clip name can be drawn to the right of them.</summary>
        private static double DrawEffectBookmarks(DrawingContext context, Rect labelRect, Clip clip)
        {
            var tabs = EffectBookmarkLayout(clip, labelRect);
            if (tabs.Count == 0)
                return 0;

            foreach (var (rect, _, name) in tabs)
            {
                context.DrawRectangle(new SolidColorBrush(Color.Parse("#2f3540")), null, new RoundedRect(rect, 3));
                var text = new FormattedText(name, System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, Typeface.Default, 10, new SolidColorBrush(Color.Parse("#8fb4ff")));
                context.DrawText(text, new Point(rect.X + 5, rect.Y + (rect.Height - text.Height) / 2));
            }
            return tabs[^1].Rect.Right - labelRect.X;
        }

        /// <summary>Returns the effect bookmark tab under the pointer, or null.</summary>
        private (string ClipId, string EffectId)? HitTestEffectBookmark(Point pos)
        {
            if (Editor is null || pos.X <= TrackHeaderWidth)
                return null;

            for (var i = 0; i < Editor.Document.Tracks.Count; i++)
            {
                var track = Editor.Document.Tracks[i];
                var top = TimelineGeometry.TrackTop(i) + RulerHeight;
                foreach (var clip in track.Clips)
                {
                    // a clip's label strip sits at its top inset; bookmarks live there
                    var clipTop = top + 10;
                    var labelRect = new Rect(
                        TrackHeaderWidth + Viewport.TimeToX(clip.StartSec + GetRippleOffsetSec(clip.Id)),
                        clipTop, clip.DurSec * Viewport.PixelsPerSecond, TimelineGeometry.ClipLabelHeight);
                    foreach (var (rect, effectId, _) in EffectBookmarkLayout(clip, labelRect))
                        if (rect.Contains(pos))
                            return (clip.Id, effectId);
                }
            }
            return null;
        }

        /// <summary>Draws a diamond per effect keyframe, positioned on the clip body.</summary>
        private void DrawKeyframeDiamonds(DrawingContext context, Clip clip, Rect bodyRect, double px)
        {
            // clip automation keyframes (opacity/volume/crop) run along the top edge of the body
            if (clip.Keyframes.Count > 0)
            {
                var topColor = new SolidColorBrush(Color.Parse("#4da3ff"));
                foreach (var (_, track) in clip.Keyframes)
                {
                    foreach (var kf in track)
                    {
                        var x = TrackHeaderWidth + Viewport.TimeToX(clip.StartSec + kf.TimeSec);
                        if (x < TrackHeaderWidth || x > Bounds.Width)
                            continue;
                        DrawDiamond(context, x, bodyRect.Y + 6, topColor);
                    }
                }
            }

            // effect-parameter keyframes run along the bottom edge of the body
            foreach (var effect in clip.Effects)
            {
                foreach (var (_, track) in effect.Keyframes)
                {
                    foreach (var kf in track)
                    {
                        var x = TrackHeaderWidth + Viewport.TimeToX(clip.StartSec + kf.TimeSec);
                        if (x < TrackHeaderWidth || x > Bounds.Width)
                            continue;
                        DrawDiamond(context, x, bodyRect.Bottom - 6, new SolidColorBrush(Color.Parse("#eab308")));
                    }
                }
            }
        }

        private static void DrawDiamond(DrawingContext context, double x, double cy, IBrush brush)
        {
            var geo = new StreamGeometry();
            using (var gc = geo.Open())
            {
                gc.BeginFigure(new Point(x, cy - 4), true);
                gc.LineTo(new Point(x + 4, cy));
                gc.LineTo(new Point(x, cy + 4));
                gc.LineTo(new Point(x - 4, cy));
                gc.EndFigure(true);
            }
            context.DrawGeometry(brush, null, geo);
        }

        /// <summary>Returns the clip keyframe diamond under the pointer, or null.</summary>
        private (Clip Clip, double TimeSec)? HitTestKeyframe(Point pos)
        {
            if (Editor is null || pos.X <= TrackHeaderWidth)
                return null;
            const double grabPx = 5;
            for (var i = 0; i < Editor.Document.Tracks.Count; i++)
            {
                var track = Editor.Document.Tracks[i];
                if (track.Kind != TrackKind.Video)
                    continue;
                var top = TimelineGeometry.TrackTop(i) + RulerHeight;
                if (pos.Y < top || pos.Y >= TimelineGeometry.TrackBottom(i) + RulerHeight)
                    continue;
                // automation diamonds render along the top edge of the body, effect diamonds along the bottom
                var bodyTop = top + 10 + TimelineGeometry.ClipLabelHeight;
                var kfBottom = top + 10 + TimelineGeometry.ClipTotalHeight;
                var inTopBand = pos.Y >= bodyTop - 2 && pos.Y <= bodyTop + 14;
                var inBottomBand = pos.Y >= kfBottom - 14 && pos.Y <= kfBottom + 2;
                foreach (var clip in track.Clips)
                {
                    if (inTopBand)
                        foreach (var (_, trackKf) in clip.Keyframes)
                            foreach (var kf in trackKf)
                            {
                                var x = TrackHeaderWidth + Viewport.TimeToX(clip.StartSec + kf.TimeSec);
                                if (Math.Abs(pos.X - x) <= grabPx)
                                    return (clip, clip.StartSec + kf.TimeSec);
                            }
                    if (inBottomBand)
                        foreach (var effect in clip.Effects)
                            foreach (var (_, trackKf) in effect.Keyframes)
                                foreach (var kf in trackKf)
                                {
                                    var x = TrackHeaderWidth + Viewport.TimeToX(clip.StartSec + kf.TimeSec);
                                    if (Math.Abs(pos.X - x) <= grabPx)
                                        return (clip, clip.StartSec + kf.TimeSec);
                                }
                }
            }
            return null;
        }

        /// <summary>Returns the transition whose cut badge is near the pointer, or null.</summary>
        private CutTransition? HitTestTransition(Point pos)
        {            if (Editor is null)
                return null;

            for (var i = 0; i < Editor.Document.Tracks.Count; i++)
            {
                var track = Editor.Document.Tracks[i];
                var top = TimelineGeometry.TrackTop(i) + RulerHeight;
                if (pos.Y < top || pos.Y >= TimelineGeometry.TrackBottom(i) + RulerHeight)
                    continue;
                // the badge sits at the clip body's vertical center; only grab within that band so
                // markers / keyframes near the cut stay clickable
                var centerY = top + 10 + TimelineGeometry.ClipLabelHeight + TimelineGeometry.ClipHeight / 2;
                if (pos.Y < centerY - 16 || pos.Y > centerY + 16)
                    continue;
                foreach (var t in Editor.EnumerateTransitions(track))
                {
                    var cutX = TrackHeaderWidth + Viewport.TimeToX(t.CutSec);
                    if (Math.Abs(pos.X - cutX) <= 15)
                        return t;
                }
            }
            return null;
        }

        private enum DragMode { None, Move, ResizeStart, ResizeEnd, FadeIn, FadeOut }

        private DragMode _dragMode = DragMode.None;
        private enum EdgeKind { None, Left, Right, FadeIn, FadeOut }
        private EdgeKind _hoverEdge;

        private const double TrimEdgePx = 6;
        private const double FadeZonePx = 14;

        private void DrawAudioWaveform(DrawingContext context, Rect rect, AudioClip clip, MediaAsset asset)
        {
            var peaks = asset.WaveformPeaks;
            if (peaks is null || peaks.Length == 0 || asset.DurationSec <= 0)
            {
                context.DrawRectangle(AudioBrush, null, rect);
                return;
            }

            var pen = new Pen(AudioWaveformBrush, 1);
            var centerY = rect.Center.Y;
            var halfH = rect.Height / 2;

            var srcIn = Math.Max(0, clip.SrcInSec);
            var srcOut = clip.SrcOutSec;
            var srcSpan = Math.Max(0.0001, srcOut - srcIn);
            var samplesPerSec = peaks.Length / asset.DurationSec;

            var (visLeft, visRight) = VisibleXRange(rect);
            if (visRight <= visLeft)
                return;

            using (context.PushClip(rect))
            {
                var x = Math.Ceiling(visLeft);
                for (; x <= visRight; x += 1)
                {
                    // source time this pixel column corresponds to
                    var t = (x - rect.X) / rect.Width;
                    var srcTime = srcIn + t * srcSpan;
                    var idx = (int)(srcTime * samplesPerSec);
                    if (idx < 0) idx = 0;
                    if (idx >= peaks.Length) idx = peaks.Length - 1;

                    var amp = Math.Clamp(peaks[idx], 0f, 1f);
                    var top = centerY - amp * halfH;
                    var bottom = centerY + amp * halfH;

                    context.DrawLine(pen, new Point(x, top), new Point(x, bottom));
                }
            }
        }

        // header button hover: (track id, column) or null
        private (string TrackId, int Column)? _hoverHeaderButton;

        // header button columns: [0] = mute/visibility, [1] = delete
        private const int HeaderToggleColumn = 0;
        private const int HeaderDeleteColumn = 1;

        private Rect HeaderButtonRect(Track track, int column)
        {
            // use the track's list position (matching render), not a possibly-stale Index
            var listIndex = Editor?.Document.Tracks.IndexOf(track) ?? track.Index;
            var top = TimelineGeometry.TrackTop(listIndex) + RulerHeight;
            var center = top + TimelineGeometry.TrackHeight / 2;
            var x = HeaderPaddingLeft + column * (HeaderButtonSize + HeaderButtonGap);
            return new Rect(x, center - HeaderButtonSize / 2, HeaderButtonSize, HeaderButtonSize);
        }

        private static double HeaderLabelX()
        {
            return HeaderPaddingLeft + 2 * (HeaderButtonSize + HeaderButtonGap) + 2;
        }

        private static string ToggleIconKey(Track track)
        {
            return track.Kind switch
            {
                TrackKind.Video => track.Visible ? "eye" : "eye-off",
                _ => track.Muted ? "volume-x" : "volume",
            };
        }

        private void DrawHeaderIcon(DrawingContext context, string key, Rect rect, bool dimmed)
        {
            var brush = dimmed ? HeaderIconDimBrush : TrackHeaderTextBrush;
            IconService.DrawStroked(context, key, rect.Inflate(-4), brush, 1.8);
        }

        private void DrawHeaderToggle(DrawingContext context, Track track, Rect rect)
        {
            var dimmed = track.Kind == TrackKind.Video ? !track.Visible : track.Muted;
            DrawHeaderIcon(context, ToggleIconKey(track), rect, dimmed);
        }

        private void DrawHeaderDelete(DrawingContext context, Track track, Rect rect)
        {
            var hovered = _hoverHeaderButton is (string tid, int col) && tid == track.Id && col == HeaderDeleteColumn;
            if (hovered)
                context.DrawRectangle(DeleteHoverBrush, null, new RoundedRect(rect, 3));
            DrawHeaderIcon(context, "trash", rect, false);
        }

        private void DrawHeaderAdd(DrawingContext context, Rect rect)
        {
            DrawHeaderIcon(context, "plus", rect, false);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (Editor is null)
                return;

            Focus();
            var pos = e.GetPosition(this);

            // right-click: context menu (add marker / enable-disable / delete marker)
            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            {
                OpenMarkerContextMenu(pos);
                e.Handled = true;
                return;
            }

            // middle-button pan (resolved through gesture registry)
            if (e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed
                && (Gestures?.Resolve(new GesturePattern { Button = Fig.Core.Input.MouseButton.Middle, Ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) })
                    == TimelineGesture.Pan))
            {
                _panning = true;
                _panStartX = pos.X;
                _panStartScroll = Viewport.ScrollTime;
                e.Handled = true;
                return;
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
                _dragTransitionMaxSec = Math.Max(0.05, Math.Min(transitionHit.Left.DurSec, transitionHit.Right.DurSec));
                _draggingTransition = true;
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            // keyframe diamond hit-test: seek the playhead to it
            if (HitTestKeyframe(pos) is { } keyframe)
            {
                _selectedClipId = keyframe.Clip.Id;
                _selectedTrackId = null;
                Editor.Selection.SelectOnly(keyframe.Clip.Id);
                SetPlayhead(Editor.SnapTime(Math.Max(0, keyframe.TimeSec)));
                e.Handled = true;
                return;
            }

            // marker hit-test: takes priority over playhead drag (ruler) and clip drag.
            // double-click on a marker seeks the playhead to it.
            // effect bookmark click: select the clip + its effect in the properties panel
            if (HitTestEffectBookmark(pos) is { } bookmark)
            {
                _selectedClipId = bookmark.ClipId;
                _selectedTrackId = null;
                _editorVm?.Properties.SelectEffect(bookmark.ClipId, bookmark.EffectId);
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            var markerHit = HitTestMarker(pos);
            if (markerHit is not null)
            {
                if (e.ClickCount >= 2)
                    SetPlayhead(Editor.SnapTime(Math.Max(0, AbsoluteMarkerTime(markerHit))));
                Editor.Selection.SelectMarker(markerHit.Marker.Id);
                _dragMarkerId = markerHit.Marker.Id;
                _dragMarkerStartSec = AbsoluteMarkerTime(markerHit);
                _draggingMarker = true;
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            // playhead drag: click in ruler area
            if (pos.Y <= RulerHeight)
            {
                _draggingPlayhead = true;
                SetPlayhead(Editor.SnapTime(XToTime(pos.X)));
                e.Handled = true;
                return;
            }

            if (pos.X <= TrackHeaderWidth)
            {
                var track = HitTestTrack(pos.Y);
                if (track is not null)
                {
                    var toggleRect = HeaderButtonRect(track, HeaderToggleColumn);
                    var delRect = HeaderButtonRect(track, HeaderDeleteColumn);
                    if (toggleRect.Contains(pos))
                    {
                        // video -> visibility toggle; audio -> mute toggle
                        if (track.Kind == TrackKind.Video)
                            track.Visible = !track.Visible;
                        else
                            track.Muted = !track.Muted;
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
                menu.Open(this);
                e.Handled = true;
                return;
            }

            // clip hit-testing: press on a clip starts move/resize drag
            var time = Viewport.XToTime(pos.X - TrackHeaderWidth);
            var clipTrack = HitTestTrack(pos.Y);
            if (clipTrack is not null)
            {
                var clip = HitTestClip(clipTrack, time, pos.X);
                if (clip is not null)
                {
                    _selectedClipId = clip.Id;
                    _selectedTrackId = null;
                    BeginClipDrag(clip, clipTrack, time, pos.X);
                    InvalidateVisual();
                    e.Handled = true;
                    return;
                }
            }

            // empty click -> clear selection
            _selectedClipId = null;
            _selectedTrackId = null;
            InvalidateVisual();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            if (Editor is null)
                return;

            var pos = e.GetPosition(this);

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

            if (_draggingMarker && _dragMarkerId is not null)
            {
                var loc = Editor.FindMarker(_dragMarkerId);
                if (loc is null)
                {
                    _draggingMarker = false;
                    return;
                }
                var raw = loc.Clip is not null ? XToTime(pos.X) - loc.Clip.StartSec : XToTime(pos.X);
                Editor.MoveMarker(_dragMarkerId, Editor.SnapTime(Math.Max(0, raw)));
                InvalidateVisual();
                return;
            }

            if (_draggingPlayhead)
            {
                SetPlayhead(Math.Max(0, Editor.SnapTime(XToTime(pos.X))));
                return;
            }

            if (_draggingClip && _dragClip is not null)
            {
                var pointerTime = Math.Max(0, Viewport.XToTime(pos.X - TrackHeaderWidth));
                var deltaTime = pointerTime - _dragPointerTime;

                switch (_dragMode)
                {
                    case DragMode.Move:
                        // snap the primary clip's new start, then move the group by the same delta
                        var rawStart = Math.Max(0, _dragOriginals[_dragClip.Id] + deltaTime);
                        var snappedStart = Editor.SnapTimeMagnetic(rawStart);
                        var delta = snappedStart - _dragOriginals[_dragClip.Id];

                        // track under the cursor: allow moving between tracks (validated)
                        var hoverTrack = HitTestTrack(pos.Y);
                        if (hoverTrack is not null && hoverTrack.Id != _dragOriginalTrack?.Id
                            && Editor.MoveClipToTrack(_dragClip.Id, hoverTrack.Id))
                        {
                            _dragOriginalTrack = hoverTrack;
                        }
                        else
                        {
                            // same-track move: reject if it would overlap another clip
                            var groupIds = Editor.LinkGroup(_dragClip.Id).Select(g => g.Id).ToHashSet();
                            var currentTrack = Editor.FindClipTrackId(_dragClip.Id);
                            var wouldOverlap = currentTrack is not null && Editor.WouldOverlapGroup(
                                currentTrack, groupIds, rawStart, _dragOriginalDurs[_dragClip.Id]);
                            if (!wouldOverlap)
                            {
                                foreach (var c in Editor.LinkGroup(_dragClip.Id))
                                    c.StartSec = Math.Max(0, _dragOriginals[c.Id] + delta);
                            }
                        }
                        break;
                    case DragMode.ResizeStart:
                        ApplyLiveResizeStart(deltaTime);
                        break;
                    case DragMode.ResizeEnd:
                        ApplyLiveResizeEnd(deltaTime);
                        break;
                    case DragMode.FadeIn:
                    {
                        var fade = Math.Max(0, pointerTime - _dragClip.StartSec);
                        Editor.SetFadeIn(_dragClip.Id, fade);
                        break;
                    }
                    case DragMode.FadeOut:
                    {
                        var end = _dragClip.StartSec + _dragClip.DurSec;
                        var fade = Math.Max(0, end - pointerTime);
                        Editor.SetFadeOut(_dragClip.Id, fade);
                        break;
                    }
                }
                InvalidateVisual();
                return;
            }

            // idle: reflect resize / fade affordance when hovering a clip edge
            var overEdge = HoverClipEdge(pos);
            Cursor = overEdge switch
            {
                EdgeKind.Left or EdgeKind.Right or EdgeKind.FadeIn or EdgeKind.FadeOut
                    => new Cursor(StandardCursorType.SizeWestEast),
                _ => Cursor.Default,
            };
            var hoverChanged = _hoverEdge != overEdge;
            _hoverEdge = overEdge;

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

        /// <summary>Returns the header button under the cursor (track id + column), or null.</summary>
        private (string TrackId, int Column)? HoverHeaderButton(Point pos)
        {
            if (Editor is null || pos.X > TrackHeaderWidth)
                return null;
            var track = HitTestTrack(pos.Y);
            if (track is null)
                return null;
            if (HeaderButtonRect(track, HeaderToggleColumn).Contains(pos))
                return (track.Id, HeaderToggleColumn);
            if (HeaderButtonRect(track, HeaderDeleteColumn).Contains(pos))
                return (track.Id, HeaderDeleteColumn);
            return null;
        }

        private void ApplyLiveResizeStart(double deltaTime)
        {
            if (_dragClip is null)
                return;

            // the moving edge is the clip start; snap it to other clips' boundaries
            var rawStart = Math.Max(0, _dragOriginals[_dragClip.Id] + deltaTime);
            var snappedStart = Editor!.SnapTimeMagnetic(rawStart, _dragClip.Id);
            var snapDelta = snappedStart - _dragOriginals[_dragClip.Id];

            foreach (var c in Editor.LinkGroup(_dragClip.Id))
            {
                var orig = _dragOriginals[c.Id];
                var origEnd = orig + _dragOriginalDurs[c.Id];
                var memberStart = Math.Max(0, orig + snapDelta);
                var memberDur = Math.Max(0.1, origEnd - memberStart);

                c.StartSec = memberStart;
                c.DurSec = memberDur;

                if (c is VideoClip or AudioClip)
                {
                    var speed = c.Speed;
                    var srcOut = _dragSrcOuts[c.Id];
                    var newSrcIn = _dragSrcIns[c.Id] + (memberStart - orig) * speed;

                    // clamp: can't pull the in-point before the start of the source
                    newSrcIn = Math.Max(0, newSrcIn);
                    if (newSrcIn >= srcOut)
                        newSrcIn = Math.Max(0, srcOut - 0.1);

                    ClipFactory.SetSourceRange(c, newSrcIn, srcOut);

                    // timeline duration derives from the clamped source range
                    c.DurSec = Math.Max(0.1, (srcOut - newSrcIn) / speed);
                }
            }
        }

        private void ApplyLiveResizeEnd(double deltaTime)
        {
            if (_dragClip is null)
                return;

            // the moving edge is the clip end; snap it to other clips' boundaries
            var rawEnd = Math.Max(_dragOriginals[_dragClip.Id] + 0.1, _dragOriginals[_dragClip.Id] + _dragOriginalDurs[_dragClip.Id] + deltaTime);
            var snappedEnd = Editor!.SnapTimeMagnetic(rawEnd, _dragClip.Id);
            var snapDelta = snappedEnd - (_dragOriginals[_dragClip.Id] + _dragOriginalDurs[_dragClip.Id]);

            foreach (var c in Editor!.LinkGroup(_dragClip.Id))
            {
                var orig = _dragOriginals[c.Id];
                var origEnd = orig + _dragOriginalDurs[c.Id];

                // clamp against the source media's actual length
                var maxSrcOut = SourceDurationSec(c);
                var srcIn = _dragSrcIns[c.Id];

                var speed = c.Speed;
                var maxDur = maxSrcOut is double max && max > srcIn
                    ? (max - srcIn) / speed
                    : _dragOriginalDurs[c.Id];

                var newEnd = Math.Max(orig + 0.1, origEnd + snapDelta);
                var newDur = Math.Max(0.1, newEnd - orig);
                if (maxDur > 0.1)
                    newDur = Math.Min(newDur, maxDur);

                c.DurSec = newDur;

                if (c is VideoClip or AudioClip)
                {
                    var newSrcOut = srcIn + newDur * speed;
                    newSrcOut = maxSrcOut is double m ? Math.Min(newSrcOut, m) : newSrcOut;
                    ClipFactory.SetSourceRange(c, srcIn, newSrcOut);
                }
            }
        }

        /// <summary>Returns the source media's duration in seconds for a clip, or null if unknown.</summary>
        private double? SourceDurationSec(Clip clip)
        {
            if (MediaById is null)
                return null;
            var sourceId = clip switch
            {
                VideoClip v => v.SourceId,
                AudioClip a => a.SourceId,
                _ => null,
            };
            if (sourceId is null || !MediaById.TryGetValue(sourceId, out var asset))
                return null;
            return asset.DurationSec > 0 ? asset.DurationSec : null;
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _hoverEdge = EdgeKind.None;
            _hoverHeaderButton = null;
            Cursor = Cursor.Default;
            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)        {
            base.OnPointerReleased(e);

            if (_draggingClip && _dragClip is not null)
            {
                _draggingClip = false;
                InvalidateVisual();
            }
            _draggingMarker = false;
            _dragMarkerId = null;
            _draggingTransition = false;
            _dragTransitionKey = null;
            _draggingPlayhead = false;
            _panning = false;
        }

        private void BeginClipDrag(Clip clip, Track track, double time, double pointerX)
        {
            // outer edge → trim; inner zone → fade; else move
            var px = Viewport.PixelsPerSecond;
            var leftX = TrackHeaderWidth + Viewport.TimeToX(clip.StartSec);
            var rightX = leftX + clip.DurSec * px;

            _dragClip = clip;
            _dragOriginalTrack = track;
            _dragStartSec = clip.StartSec;
            _dragOriginalDurSec = clip.DurSec;   // <-- capture fixed baseline
            _dragSrcInSec = clip.SourceIn;
            _dragSrcOutSec = clip.SourceOut;
            _dragPointerTime = Viewport.XToTime(pointerX - TrackHeaderWidth);

            // snapshot the whole link group so linked clips move/resize in lockstep
            _dragOriginals.Clear();
            _dragOriginalDurs.Clear();
            _dragSrcIns.Clear();
            _dragSrcOuts.Clear();
            foreach (var c in Editor!.LinkGroup(clip.Id))
            {
                _dragOriginals[c.Id] = c.StartSec;
                _dragOriginalDurs[c.Id] = c.DurSec;
                _dragSrcIns[c.Id] = c.SourceIn;
                _dragSrcOuts[c.Id] = c.SourceOut;
            }

            var edge = ClassifyClipEdge(pointerX, leftX, rightX, clip.DurSec * px);
            if (edge == EdgeKind.None)
            {
                if (clip.FadeInSec > 1e-6)
                {
                    var tipX = leftX + clip.FadeInSec * px;
                    if (Math.Abs(pointerX - tipX) <= FadeZonePx * 0.5)
                        edge = EdgeKind.FadeIn;
                }
                if (edge == EdgeKind.None && clip.FadeOutSec > 1e-6)
                {
                    var tipX = rightX - clip.FadeOutSec * px;
                    if (Math.Abs(pointerX - tipX) <= FadeZonePx * 0.5)
                        edge = EdgeKind.FadeOut;
                }
            }
            _dragMode = edge switch
            {
                EdgeKind.Left => DragMode.ResizeStart,
                EdgeKind.Right => DragMode.ResizeEnd,
                EdgeKind.FadeIn => DragMode.FadeIn,
                EdgeKind.FadeOut => DragMode.FadeOut,
                _ => DragMode.Move,
            };
            _draggingClip = true;
        }

        /// <summary>
        /// Outer TrimEdgePx = trim; next FadeZonePx inward = fade (video-style opacity handles).
        /// </summary>
        private static EdgeKind ClassifyClipEdge(double pointerX, double leftX, double rightX, double clipWidthPx)
        {
            // too narrow for fade zones — trim only
            var canFade = clipWidthPx > (TrimEdgePx + FadeZonePx) * 2 + 4;

            if (Math.Abs(pointerX - leftX) <= TrimEdgePx)
                return EdgeKind.Left;
            if (Math.Abs(pointerX - rightX) <= TrimEdgePx)
                return EdgeKind.Right;

            if (canFade)
            {
                if (pointerX > leftX + TrimEdgePx && pointerX <= leftX + TrimEdgePx + FadeZonePx)
                    return EdgeKind.FadeIn;
                if (pointerX < rightX - TrimEdgePx && pointerX >= rightX - TrimEdgePx - FadeZonePx)
                    return EdgeKind.FadeOut;
            }

            // already have a fade: allow grabbing the ramp tip even outside the fixed zone
            // (caller may pass clip for that — handled in HoverClipEdge with clip fades)
            return EdgeKind.None;
        }

        private Track? HitTestTrack(double y)
        {
            if (Editor is null)
                return null;
            // iterate by list position so hit-testing matches rendering, regardless of stale Index values
            for (var i = 0; i < Editor.Document.Tracks.Count; i++)
            {
                var track = Editor.Document.Tracks[i];
                if (y >= TimelineGeometry.TrackTop(i) + RulerHeight
                    && y < TimelineGeometry.TrackBottom(i) + RulerHeight)
                    return track;
            }
            return null;
        }

        private Clip? HitTestClip(Track track, double time, double pointerX)
        {
            return track.Clips.FirstOrDefault(c =>
                time >= c.StartSec && time < c.StartSec + c.DurSec);
        }

        /// <summary>
        /// Returns the marker under the pointer: clip markers (highest priority), then
        /// track markers, then timeline markers in the ruler. Null when nothing is hit.
        /// </summary>
        private MarkerLocation? HitTestMarker(Point pos)
        {
            if (Editor is null || pos.X <= TrackHeaderWidth)
                return null;
            const double grabPx = 8;

            var track = HitTestTrack(pos.Y);
            if (track is not null)
            {
                var trackIdx = Editor.Document.Tracks.IndexOf(track);
                var top = TimelineGeometry.TrackTop(trackIdx) + RulerHeight;
                var clipTop = top + 10;

                // clip markers only grab within the tab + label strip, so the body (transition
                // badge, keyframes, trim edges) stays clickable
                if (pos.Y >= clipTop - 9 && pos.Y <= clipTop + TimelineGeometry.ClipLabelHeight)
                {
                    var time = XToTime(pos.X);
                    var clip = HitTestClip(track, time, pos.X);
                    if (clip is not null && clip.Markers.Count > 0)
                    {
                        var px = Viewport.PixelsPerSecond;
                        var clipLeft = TrackHeaderWidth + Viewport.TimeToX(clip.StartSec + GetRippleOffsetSec(clip.Id));
                        foreach (var m in clip.Markers)
                        {
                            if (m.StartSec < 0 || m.StartSec > clip.DurSec)
                                continue;
                            if (Math.Abs(pos.X - (clipLeft + m.StartSec * px)) <= grabPx)
                                return new MarkerLocation(m, clip, track, Editor.Document);
                        }
                    }
                }

                // track markers are diamonds at the lane bottom edge
                var trackBottom = top + TimelineGeometry.TrackHeight;
                if (pos.Y >= trackBottom - 16 && pos.Y <= trackBottom)
                {
                    foreach (var m in track.Markers)
                    {
                        if (Math.Abs(pos.X - (TrackHeaderWidth + Viewport.TimeToX(m.StartSec))) <= grabPx)
                            return new MarkerLocation(m, null, track, Editor.Document);
                    }
                }
            }

            if (pos.Y <= RulerHeight)
            {
                foreach (var m in Editor.Document.Markers)
                {
                    if (Math.Abs(pos.X - (TrackHeaderWidth + Viewport.TimeToX(m.StartSec))) <= grabPx)
                        return new MarkerLocation(m, null, null, Editor.Document);
                }
            }

            return null;
        }

        /// <summary>Right-click menu: add marker, enable/disable a clip, remove a transition or marker.</summary>
        private void OpenMarkerContextMenu(Point pos)
        {
            if (Editor is null)
                return;

            var time = XToTime(pos.X);
            var track = HitTestTrack(pos.Y);
            var clip = track is not null ? HitTestClip(track, time, pos.X) : null;
            var marker = HitTestMarker(pos);
            var transition = HitTestTransition(pos);

            if (transition is not null)
            {
                Editor.Selection.SelectTransition(transition.Key);
                InvalidateVisual();

                var transitionMenu = new ContextMenu();
                var add = new MenuItem { Header = "Add Marker at Playhead" };
                add.Click += (_, _) => _editorVm?.AddMarkerAtPlayheadCommand.Execute(null);
                transitionMenu.Items.Add(add);

                var remove = new MenuItem { Header = "Remove Transition" };
                remove.Click += (_, _) => _editorVm?.RemoveSelectedTransitionCommand.Execute(null);
                transitionMenu.Items.Add(remove);
                transitionMenu.Open(this);
                return;
            }

            if (clip is not null)
            {
                _selectedClipId = clip.Id;
                _selectedTrackId = null;
            }
            if (marker is not null)
                Editor.Selection.SelectMarker(marker.Marker.Id);
            InvalidateVisual();

            var menu = new ContextMenu();

            var addMarker = new MenuItem { Header = "Add Marker at Playhead" };
            addMarker.Click += (_, _) => _editorVm?.AddMarkerAtPlayheadCommand.Execute(null);
            menu.Items.Add(addMarker);

            if (clip is not null)
            {
                var toggle = new MenuItem { Header = clip.Enabled ? "Disable Clip" : "Enable Clip" };
                toggle.Click += (_, _) => _editorVm?.ToggleClipEnabledSelectedCommand.Execute(null);
                menu.Items.Add(toggle);
            }

            if (marker is not null)
            {
                var del = new MenuItem { Header = "Delete Marker" };
                del.Click += (_, _) => _editorVm?.DeleteSelectedMarkerCommand.Execute(null);
                menu.Items.Add(del);
            }

            menu.Open(this);
        }

        /// <summary>True when a clip has a neighbor touching it on the left/right in the same track.</summary>
        private static (bool HasPrev, bool HasNext) HasAdjacent(Track track, Clip clip)
        {
            const double eps = 1e-6;
            var hasPrev = false;
            var hasNext = false;
            foreach (var other in track.Clips)
            {
                if (other.Id == clip.Id)
                    continue;
                if (Math.Abs(other.StartSec + other.DurSec - clip.StartSec) < eps)
                    hasPrev = true;
                if (Math.Abs(other.StartSec - (clip.StartSec + clip.DurSec)) < eps)
                    hasNext = true;
            }
            return (hasPrev, hasNext);
        }

        /// <summary>Returns whether the pointer is over a clip's trim or fade edge.</summary>
        private EdgeKind HoverClipEdge(Point pos)
        {
            if (Editor is null || pos.X <= TrackHeaderWidth)
                return EdgeKind.None;

            var px = Viewport.PixelsPerSecond;
            var time = Viewport.XToTime(pos.X - TrackHeaderWidth);
            var track = HitTestTrack(pos.Y);
            if (track is null)
                return EdgeKind.None;

            foreach (var clip in track.Clips)
            {
                if (!(time >= clip.StartSec && time < clip.StartSec + clip.DurSec))
                    continue;
                var leftX = TrackHeaderWidth + Viewport.TimeToX(clip.StartSec);
                var rightX = leftX + clip.DurSec * px;
                var kind = ClassifyClipEdge(pos.X, leftX, rightX, clip.DurSec * px);
                if (kind != EdgeKind.None)
                    return kind;

                // grab existing fade ramp tip even when outside the default inner zone
                if (clip.FadeInSec > 1e-6)
                {
                    var tipX = leftX + clip.FadeInSec * px;
                    if (Math.Abs(pos.X - tipX) <= FadeZonePx * 0.5)
                        return EdgeKind.FadeIn;
                }
                if (clip.FadeOutSec > 1e-6)
                {
                    var tipX = rightX - clip.FadeOutSec * px;
                    if (Math.Abs(pos.X - tipX) <= FadeZonePx * 0.5)
                        return EdgeKind.FadeOut;
                }
            }
            return EdgeKind.None;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (Editor is null)
                return;

            // M = add a marker at the playhead (selected clip / active track / timeline)
            if (e.Key == Key.M && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                _editorVm?.AddMarkerAtPlayheadCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Delete || e.Key == Key.Back)
            {
                if (Editor.Selection.SelectedTransitionKey is not null)
                {
                    _editorVm?.RemoveSelectedTransitionCommand.Execute(null);
                    InvalidateVisual();
                    e.Handled = true;
                }
                else if (Editor.Selection.SelectedMarkerId is not null)
                {
                    _editorVm?.DeleteSelectedMarkerCommand.Execute(null);
                    InvalidateVisual();
                    e.Handled = true;
                }
                else if (Editor.Selection.Count > 0)
                {
                    // Delete = lift selected (+ linked) clips only. Never ripple or touch
                    // unselected clips that share the same timeline position.
                    Editor.LiftSelected();
                    InvalidateVisual();
                    e.Handled = true;
                }
                else if (_selectedTrackId is not null)
                {
                    Editor.RemoveTrack(_selectedTrackId);
                    _selectedTrackId = null;
                    InvalidateVisual();
                    e.Handled = true;
                }
            }
        }

        private Rect AddTrackButtonRect()
        {
            if (Editor is null)
                return default;
            var y = TimelineGeometry.TrackBottom(Editor.Document.Tracks.Count - 1) + RulerHeight + 4;
            return new Rect(8, y, HeaderButtonSize, HeaderButtonSize);
        }

        // ---- rendering ----

        public override void Render(DrawingContext context)
        {
            context.DrawRectangle(SurfaceBackground, null, new Rect(0, 0, Bounds.Width, Bounds.Height));

            if (Editor is null)
                return;

            var px = Viewport.PixelsPerSecond;
            var scroll = Viewport.ScrollTime;

            DrawRuler(context, px, scroll);
            DrawTimelineMarkers(context);

            // clip everything below the ruler + right of the header so clips never
            // draw over the header column when zoomed/scrolled
            var clipArea = new Rect(TrackHeaderWidth, RulerHeight,
                Math.Max(0, Bounds.Width - TrackHeaderWidth), Math.Max(0, Bounds.Height - RulerHeight));

            for (var i = 0; i < Editor.Document.Tracks.Count; i++)
            {
                var track = Editor.Document.Tracks[i];
                var top = TimelineGeometry.TrackTop(i) + RulerHeight;
                var height = TimelineGeometry.TrackHeight;

                var isTrackSelected = track.Id == _selectedTrackId;
                var rowRect = new Rect(0, top, Bounds.Width, height);
                var laneBrush = i % 2 == 0 ? TrackLaneBrush : TrackLaneAltBrush;
                context.DrawRectangle(laneBrush, null, rowRect);
                if (isTrackSelected)
                {
                    context.DrawRectangle(SelectedLaneTintBrush, null, rowRect);
                    context.DrawRectangle(SelectionBrush, null, new Rect(0, top, TrackAccentBarWidth, height));
                }
                context.DrawLine(new Pen(BorderBrush, 1), new Point(TrackHeaderWidth, top), new Point(TrackHeaderWidth, top + height));
                context.DrawLine(new Pen(BorderBrush, 1), new Point(0, top + height), new Point(Bounds.Width, top + height));

                var trackLabel = track.Name ?? track.Kind.ToString();
                var trackText = new FormattedText(
                    trackLabel,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    Typeface.Default,
                    11,
                    TrackHeaderTextBrush);
                context.DrawText(trackText, new Point(HeaderLabelX(), top + (height - trackText.Height) / 2));

                DrawHeaderToggle(context, track, HeaderButtonRect(track, HeaderToggleColumn));
                DrawHeaderDelete(context, track, HeaderButtonRect(track, HeaderDeleteColumn));

                // clip area, offset by header + viewport scroll
                using (context.PushClip(clipArea))
                {
                    DrawTrackMarkers(context, track, top, height);
                    var trackTransitions = Editor.EnumerateTransitions(track).ToList();

                    foreach (var clip in track.Clips)
                    {
                        var x = TrackHeaderWidth + Viewport.TimeToX(clip.StartSec + GetRippleOffsetSec(clip.Id));
                        var w = clip.DurSec * px;
                        if (x + w < TrackHeaderWidth || x > Bounds.Width)
                            continue;   // culled: not visible

                        var (fill, border) = clip.Kind switch
                        {
                            ClipKind.Video => (VideoBrush, VideoBorder),
                            ClipKind.Audio => (AudioBrush, AudioBorder),
                            ClipKind.Text => (TextClipBrush, TextBorder),
                            _ => (VideoBrush, VideoBorder),
                        };

                        // clip widget = label strip (name) on top + body (filmstrip/waveform).
                        // top inset leaves room for marker tabs inside the lane.
                        var clipTop = top + 10;
                        var totalHeight = TimelineGeometry.ClipTotalHeight;
                        var labelHeight = TimelineGeometry.ClipLabelHeight;
                        var bodyHeight = TimelineGeometry.ClipHeight;

                        // square the corners on edges shared with an adjacent clip so split
                        // halves butt cleanly instead of showing overlapping rounded corners
                        var (hasPrev, hasNext) = HasAdjacent(track, clip);
                        var topLeft = hasPrev ? 0 : ClipCornerRadius;
                        var topRight = hasNext ? 0 : ClipCornerRadius;
                        var bottomLeft = hasPrev ? 0 : ClipCornerRadius;
                        var bottomRight = hasNext ? 0 : ClipCornerRadius;
                        var corner = new CornerRadius(topLeft, topRight, bottomRight, bottomLeft);

                        var widgetRect = new Rect(x, clipTop, w, totalHeight);

                        // only the first clip of a contiguous run casts a shadow, so split
                        // halves don't stack shadows and read as overlapping
                        if (!hasPrev)
                        {
                            var shadowRect = new RoundedRect(widgetRect.Translate(new Vector(0, 1)), corner);
                            context.DrawRectangle(ClipShadow, null, shadowRect);
                        }

                        var isClipSelected = _selectedClipId is not null
                            && Editor.Selection.IsSelected(clip.Id);
                        var isEffectDropTarget = _dropEffectClipId == clip.Id;
                        var outline = isClipSelected || isEffectDropTarget ? SelectionBrush : border;
                        var outlineWidth = isEffectDropTarget ? 3 : (isClipSelected ? 2 : 1);

                        // --- name strip above the clip body ---
                        var labelRect = new Rect(x, clipTop, w, labelHeight);
                        var labelBrush = isClipSelected
                            ? new SolidColorBrush(Color.FromArgb(80, 0x4d, 0xa3, 0xff))
                            : new SolidColorBrush(Color.Parse("#1c1c1e"));
                        context.DrawRectangle(labelBrush, null, new RoundedRect(labelRect, new CornerRadius(topLeft, topRight, 0, 0)));

                        // effect folder-bookmarks first; the clip name draws to the right of them
                        var bookmarkWidth = DrawEffectBookmarks(context, labelRect, clip);

                        var clipName = ClipName(clip);
                        if (clipName is not null)
                        {
                            var labelText = new FormattedText(
                                clipName,
                                System.Globalization.CultureInfo.CurrentCulture,
                                FlowDirection.LeftToRight,
                                Typeface.Default,
                                10,
                                TrackHeaderTextBrush);
                            context.DrawText(labelText, new Point(x + 4 + bookmarkWidth, labelRect.Y + (labelHeight - labelText.Height) / 2));
                        }

                        // --- clip body ---
                        var rect = new Rect(x, clipTop + labelHeight, w, bodyHeight);
                        var bodyRounded = new RoundedRect(rect, new CornerRadius(0, 0, bottomRight, bottomLeft));

                        // draw filmstrip as video clip background
                        if (clip is VideoClip vc && MediaById is not null
                            && MediaById.TryGetValue(vc.SourceId, out var asset)
                            && asset?.Filmstrip is string stripPath
                            && GetBitmap(stripPath) is Bitmap strip)
                        {
                            DrawFilmstrip(context, rect, vc, asset, strip);
                            context.DrawRectangle(Brushes.Transparent, new Pen(outline, outlineWidth), bodyRounded);
                        }
                        // draw waveform as audio clip background
                        else if (clip is AudioClip ac && MediaById is not null
                            && MediaById.TryGetValue(ac.SourceId, out var audioAsset)
                            && audioAsset is not null)
                        {
                            DrawAudioWaveform(context, rect, ac, audioAsset);
                            context.DrawRectangle(Brushes.Transparent, new Pen(outline, outlineWidth), bodyRounded);
                        }
                        else
                        {
                            context.DrawRectangle(fill, new Pen(outline, outlineWidth), bodyRounded);
                        }

                        if (isEffectDropTarget)
                        {
                            context.DrawRectangle(
                                new SolidColorBrush(Color.FromArgb(50, 0x4d, 0xa3, 0xff)),
                                null, new RoundedRect(widgetRect, corner));
                        }

                        DrawFadeRamps(context, rect, clip);

                        foreach (var t in trackTransitions)
                        {
                            if (t.RightClipId == clip.Id)
                                DrawTransitionSpan(context, rect, t, isLeftClip: false, px);
                            if (t.LeftClipId == clip.Id)
                            {
                                DrawTransitionSpan(context, rect, t, isLeftClip: true, px);
                                DrawTransitionBadge(context, t, rect.Y, rect.Bottom);
                            }
                        }

                        DrawClipMarkers(context, x, clipTop, clip);
                        DrawKeyframeDiamonds(context, clip, rect, px);

                        if (!clip.Enabled)
                        {
                            context.DrawRectangle(
                                new SolidColorBrush(Color.FromArgb(120, 0x10, 0x10, 0x12)),
                                null, new RoundedRect(widgetRect, corner));
                        }

                        // resize-handle indicator on the edge under the cursor (or both edges when selected)
                        var hoverLeft = _hoverEdge == EdgeKind.Left;
                        var hoverRight = _hoverEdge == EdgeKind.Right;
                        var hoverFadeIn = _hoverEdge == EdgeKind.FadeIn;
                        var hoverFadeOut = _hoverEdge == EdgeKind.FadeOut;
                        if (isClipSelected || hoverLeft)
                            DrawResizeHandle(context, x, rect.Y, rect.Height);
                        if (isClipSelected || hoverRight)
                            DrawResizeHandle(context, x + w, rect.Y, rect.Height);
                        if (hoverFadeIn || (isClipSelected && clip.FadeInSec > 1e-6))
                            DrawResizeHandle(context, x + Math.Min(clip.FadeInSec, clip.DurSec) * px, rect.Y, rect.Height);
                        if (hoverFadeOut || (isClipSelected && clip.FadeOutSec > 1e-6))
                            DrawResizeHandle(context, x + w - Math.Min(clip.FadeOutSec, clip.DurSec) * px, rect.Y, rect.Height);
                    }   // foreach clip
                }   // PushClip(clipArea)
            }   // for tracks

            // add-track button at bottom of header column
            if (Editor.Document.Tracks.Count > 0)
            {
                var addRect = AddTrackButtonRect();
                DrawHeaderAdd(context, addRect);
            }

            // drop preview: highlight target track + ghost clip rect
            if (_dropPreviewTrack is not null && _dropPreviewTime >= 0 && _dropPreviewAsset is not null)
            {
                var trackTop = TimelineGeometry.TrackTop(_dropPreviewTrack.Index) + RulerHeight;
                context.DrawRectangle(new SolidColorBrush(Color.Parse("#2244aa44")), null,
                    new Rect(0, trackTop, Bounds.Width, TimelineGeometry.TrackHeight));

                var ghostX = TrackHeaderWidth + Viewport.TimeToX(_dropPreviewTime);
                var ghostW = _dropPreviewAsset.DurationSec * px;
                var ghostRect = new Rect(ghostX, trackTop + 10, ghostW, TimelineGeometry.ClipTotalHeight);
                context.DrawRectangle(new SolidColorBrush(Color.Parse("#88ffffff")), null,
                    new RoundedRect(ghostRect, ClipCornerRadius));
                context.DrawRectangle(Brushes.Transparent, new Pen(SelectionBrush, 2),
                    new RoundedRect(ghostRect, ClipCornerRadius));
            }

            // drop indicator line (media into empty space)
            if (_dropTimeSec >= 0 && _dropPreviewTrack is null)
            {
                var x = TrackHeaderWidth + Viewport.TimeToX(_dropTimeSec);
                context.DrawLine(new Pen(Brushes.OrangeRed, 2), new Point(x, RulerHeight), new Point(x, Bounds.Height));
            }

            // transition drop: highlight cut between abutting clips
            if (_dropTransitionCutSec >= 0 && _dropTransitionTrack is not null)
            {
                var listIndex = Editor.Document.Tracks.IndexOf(_dropTransitionTrack);
                if (listIndex >= 0)
                {
                    var trackTop = TimelineGeometry.TrackTop(listIndex) + RulerHeight;
                    var x = TrackHeaderWidth + Viewport.TimeToX(_dropTransitionCutSec);
                    var marker = new SolidColorBrush(Color.FromArgb(230, 0xea, 0xb3, 0x08));
                    context.DrawLine(new Pen(marker, 3), new Point(x, trackTop + 10),
                        new Point(x, trackTop + 10 + TimelineGeometry.ClipTotalHeight));
                    var midY = trackTop + 10 + TimelineGeometry.ClipTotalHeight / 2;
                    var diamond = new StreamGeometry();
                    using (var gc = diamond.Open())
                    {
                        gc.BeginFigure(new Point(x, midY - 6), true);
                        gc.LineTo(new Point(x + 5, midY));
                        gc.LineTo(new Point(x, midY + 6));
                        gc.LineTo(new Point(x - 5, midY));
                        gc.EndFigure(true);
                    }
                    context.DrawGeometry(marker, null, diamond);
                }
            }

            // playhead
            var playheadX = TrackHeaderWidth + Viewport.TimeToX(PlayheadTimeSec);
            if (playheadX >= TrackHeaderWidth)
            {
                var playheadPen = new Pen(EditorTheme.PlayheadBrush, 1);
                context.DrawLine(playheadPen, new Point(playheadX, 0), new Point(playheadX, Bounds.Height));
                var cap = new StreamGeometry();
                using (var gc = cap.Open())
                {
                    gc.BeginFigure(new Point(playheadX - 5, 0), true);
                    gc.LineTo(new Point(playheadX + 5, 0));
                    gc.LineTo(new Point(playheadX, 8));
                    gc.EndFigure(true);
                }
                context.DrawGeometry(EditorTheme.PlayheadBrush, null, cap);
            }
        }

        private void DrawRuler(DrawingContext context, double px, double scroll)
        {
            context.DrawRectangle(RulerBackground, null, new Rect(0, 0, Bounds.Width, RulerHeight));
            context.DrawLine(new Pen(BorderBrush, 1), new Point(0, RulerHeight), new Point(Bounds.Width, RulerHeight));

            var interval = RulerCalculator.PickInterval(px);
            var visibleEnd = Viewport.VisibleEndTime(Bounds.Width - TrackHeaderWidth);

            foreach (var tick in RulerCalculator.GetTicks(scroll, visibleEnd, interval))
            {
                var x = TrackHeaderWidth + Viewport.TimeToX(tick.Time);
                if (x < TrackHeaderWidth || x > Bounds.Width)
                    continue;

                var tickHeight = tick.IsMajor ? 10.0 : 5.0;
                context.DrawLine(new Pen(RulerTickBrush, 1), new Point(x, RulerHeight - tickHeight), new Point(x, RulerHeight));

                if (tick.IsMajor)
                {
                    var label = RulerCalculator.Format(tick.Time, interval);
                    var text = new FormattedText(
                        label,
                        System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        Typeface.Default,
                        10,
                        RulerTextBrush);
                    context.DrawText(text, new Point(x + 3, 2));
                }
            }
        }
    }
}
