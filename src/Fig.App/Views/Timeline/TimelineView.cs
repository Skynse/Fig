using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Fig.Core.Input;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.App.Views
{
    public class TimelineView : Control
    {
        public static readonly DataFormat<MediaAsset> MediaFormat =
            DataFormat<MediaAsset>.CreateInProcessFormat<MediaAsset>("fig.media");

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

        // playhead
        public double PlayheadTimeSec { get; private set; }
        private bool _draggingPlayhead;

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

        private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.Parse("#4da3ff"));

        // Base palette
        private static readonly IBrush SurfaceBackground = new SolidColorBrush(Color.Parse("#1e1e1e"));
        private static readonly IBrush TrackBackground = new SolidColorBrush(Color.Parse("#252526"));
        private static readonly IBrush TrackBackgroundAlt = new SolidColorBrush(Color.Parse("#2a2a2b"));
        private static readonly IBrush RulerBackground = new SolidColorBrush(Color.Parse("#1a1a1a"));
        private static readonly IBrush BorderBrush = new SolidColorBrush(Color.Parse("#333333"));
        private static readonly IBrush RulerTickBrush = new SolidColorBrush(Color.Parse("#4a4a4a"));
        private static readonly IBrush RulerTextBrush = new SolidColorBrush(Color.Parse("#8a8a8a"));

        // Clip fills
        private static readonly IBrush VideoBrush = new SolidColorBrush(Color.Parse("#3b82f6"));
        private static readonly IBrush AudioBrush = new SolidColorBrush(Color.Parse("#22c55e"));
        private static readonly IBrush AudioWaveformBrush = new SolidColorBrush(Color.Parse("#86efac"));
        private static readonly IBrush TextClipBrush = new SolidColorBrush(Color.Parse("#eab308"));

        private static readonly IBrush VideoBorder = new SolidColorBrush(Color.Parse("#1d4ed8"));
        private static readonly IBrush AudioBorder = new SolidColorBrush(Color.Parse("#15803d"));
        private static readonly IBrush TextBorder = new SolidColorBrush(Color.Parse("#a16207"));

        private static readonly IBrush ClipShadow = new SolidColorBrush(Color.Parse("#00000060"));
        private static readonly IBrush TrackHeaderBrush = new SolidColorBrush(Color.Parse("#222222"));
        private static readonly IBrush TrackHeaderTextBrush = new SolidColorBrush(Color.Parse("#9a9a9a"));

        private const double RulerHeight = 24;
        private const double ClipCornerRadius = 4;
        private const double TrackHeaderWidth = 120;
        private const double ZoomFactor = 1.25;
        private const double HeaderButtonSize = 18;
        private const double HeaderButtonGap = 4;

        private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#3d3d3e"));
        private static readonly IBrush MutedTextBrush = new SolidColorBrush(Color.Parse("#6a6a6a"));
        private static readonly IBrush DeleteBrush = new SolidColorBrush(Color.Parse("#4a2020"));

        public TimelineView()
        {
            ClipToBounds = true;
            Focusable = true;
            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);
            AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
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
                InvalidateVisual();
            }
            else if (change.Property == MediaByIdProperty)
            {
                InvalidateVisual();
            }
        }

        private void OnTimelineChanged() => InvalidateVisual();

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

        private void OnDragOver(object? sender, DragEventArgs e)
        {
            if (e.DataTransfer.Contains(MediaFormat) && Editor is not null)
            {
                e.DragEffects = DragDropEffects.Copy;
                var pos = e.GetPosition(this);
                _dropPreviewTime = Editor.SnapTime(XToTime(pos.X));
                _dropPreviewAsset = e.DataTransfer.TryGetValue(MediaFormat);
                _dropPreviewTrack = ResolveDropTrack(pos.Y);
                _dropTimeSec = _dropPreviewTime;
                InvalidateVisual();
            }
            else
            {
                e.DragEffects = DragDropEffects.None;
            }
        }

        private void OnDragLeave(object? sender, DragEventArgs e)
        {
            _dropTimeSec = -1;
            _dropPreviewTime = -1;
            _dropPreviewTrack = null;
            _dropPreviewAsset = null;
            InvalidateVisual();
        }

        private void OnDrop(object? sender, DragEventArgs e)
        {
            if (Editor is null || !e.DataTransfer.Contains(MediaFormat))
                return;

            var asset = e.DataTransfer.TryGetValue(MediaFormat);
            if (asset is null)
                return;

            var snapped = Editor.SnapTime(XToTime(e));
            var targetTrack = ResolveDropTrack(e.GetPosition(this).Y);

            // creates the clip (plus a linked audio clip + audio track if the asset has audio)
            Editor.AddMediaLinked(asset, targetTrack.Id, snapped);

            _dropTimeSec = -1;
            _dropPreviewTime = -1;
            _dropPreviewTrack = null;
            _dropPreviewAsset = null;
            InvalidateVisual();
        }

        /// <summary>Returns the track under the cursor, or creates a new one of the asset's kind when dropping on empty space.</summary>
        private Track ResolveDropTrack(double y)
        {
            var track = HitTestTrack(y);
            if (track is not null)
                return track;

            // empty space below tracks (or no tracks): create a matching track
            var kind = _dropPreviewAsset?.Kind == MediaKind.Audio ? TrackKind.Audio : TrackKind.Video;
            return Editor!.EnsureTrack(kind);
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

            // each tile scaled to the clip height, preserving the source aspect ratio
            var destH = rect.Height;
            var destW = frameW * (destH / frameH);

            using (context.PushClip(rect))
            {
                var x = rect.X;
                while (x < rect.X + rect.Width)
                {
                    var clipLocalSec = (x - rect.X) / (rect.Width / clip.DurSec);
                    var srcTimeSec = clip.SrcInSec + clipLocalSec * speed;

                    var frameIndex = (int)(srcTimeSec / asset.FilmstripFrameIntervalSec);
                    frameIndex = Math.Clamp(frameIndex, 0, asset.FilmstripFrameCount - 1);

                    var srcRect = new Rect(frameIndex * frameW, 0, frameW, frameH);
                    var destRect = new Rect(x, rect.Y, destW, destH);

                    context.DrawImage(strip, srcRect, destRect);

                    x += destW;
                }
            }
        }

        /// <summary>
        /// Renders the audio waveform directly from the decoded peak samples, one vertical
        /// mirrored line per pixel column across the clip body. No stretching: the source
        /// range (SrcIn..SrcOut) is mapped linearly to the visible width, so zooming in
        /// just spreads the same samples wider (more detail, never distorted).
        /// </summary>
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

            using (context.PushClip(rect))
            {
                var lastX = -1.0;
                for (var x = rect.X; x <= rect.X + rect.Width; x += 1)
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
                    lastX = x;
                }
            }
        }

        private enum ButtonKind { Mute, Delete }
        private enum DragMode { None, Move, ResizeStart, ResizeEnd }

        private DragMode _dragMode = DragMode.None;

        private Rect HeaderButtonRect(Track track, ButtonKind kind)
        {
            var top = TimelineGeometry.TrackTop(track.Index) + RulerHeight;
            var center = (top + TimelineGeometry.TrackHeight) / 2;
            var x = kind == ButtonKind.Mute
                ? TrackHeaderWidth - HeaderButtonSize * 2 - HeaderButtonGap
                : TrackHeaderWidth - HeaderButtonSize;
            return new Rect(x, center - HeaderButtonSize / 2, HeaderButtonSize, HeaderButtonSize);
        }

        private void DrawHeaderButton(DrawingContext context, string label, IBrush bg, Rect rect, bool isActive)
        {
            context.DrawRectangle(bg, null, new RoundedRect(rect, 3));
            var text = new FormattedText(
                label,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                10,
                isActive ? Brushes.White : TrackHeaderTextBrush);
            context.DrawText(text, new Point(rect.X + (rect.Width - text.Width) / 2, rect.Y + (rect.Height - text.Height) / 2));
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (Editor is null)
                return;

            Focus();
            var pos = e.GetPosition(this);

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

            // playhead drag: click in ruler area
            if (pos.Y <= RulerHeight)
            {
                _draggingPlayhead = true;
                PlayheadTimeSec = Editor.SnapTime(XToTime(pos.X));
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            if (pos.X <= TrackHeaderWidth)
            {
                var track = HitTestTrack(pos.Y);
                if (track is not null)
                {
                    var muteRect = HeaderButtonRect(track, ButtonKind.Mute);
                    var delRect = HeaderButtonRect(track, ButtonKind.Delete);
                    if (muteRect.Contains(pos))
                    {
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

            // add-track button at bottom of header column
            var addRect = AddTrackButtonRect();
            if (addRect.Contains(pos))
            {
                var kind = LastTrackKind();
                Editor.AddTrack(kind == TrackKind.Video ? TrackKind.Audio : TrackKind.Video);
                InvalidateVisual();
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

            if (_draggingPlayhead)
            {
                PlayheadTimeSec = Math.Max(0, Editor.SnapTime(XToTime(pos.X)));
                InvalidateVisual();
                return;
            }

            if (_draggingClip && _dragClip is not null)
            {
                var pointerTime = Math.Max(0, Viewport.XToTime(pos.X - TrackHeaderWidth));
                var deltaTime = pointerTime - _dragPointerTime;

                switch (_dragMode)
                {
                    case DragMode.Move:
                        foreach (var c in Editor.LinkGroup(_dragClip.Id))
                            c.StartSec = Math.Max(0, _dragOriginals[c.Id] + deltaTime);
                        break;
                    case DragMode.ResizeStart:
                        ApplyLiveResizeStart(deltaTime);
                        break;
                    case DragMode.ResizeEnd:
                        ApplyLiveResizeEnd(deltaTime);
                        break;
                }
                InvalidateVisual();
                return;
            }
        }

        private void ApplyLiveResizeStart(double deltaTime)
        {
            if (_dragClip is null)
                return;

            foreach (var c in Editor!.LinkGroup(_dragClip.Id))
            {
                var orig = _dragOriginals[c.Id];
                var origEnd = orig + _dragOriginalDurs[c.Id];
                var memberStart = Math.Max(0, orig + deltaTime);
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

                var newEnd = Math.Max(orig + 0.1, origEnd + deltaTime);
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

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            if (_draggingClip && _dragClip is not null)
            {
                _draggingClip = false;
                InvalidateVisual();
            }
            _draggingPlayhead = false;
            _panning = false;
        }

        private void BeginClipDrag(Clip clip, Track track, double time, double pointerX)
        {
            // near left edge -> resize start; near right edge -> resize end; else move
            var px = Viewport.PixelsPerSecond;
            var leftX = TrackHeaderWidth + Viewport.TimeToX(clip.StartSec);
            var rightX = leftX + clip.DurSec * px;
            var edgePx = 6;

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

            if (Math.Abs(pointerX - leftX) <= edgePx)
            {
                _dragMode = DragMode.ResizeStart;
                _draggingClip = true;
            }
            else if (Math.Abs(pointerX - rightX) <= edgePx)
            {
                _dragMode = DragMode.ResizeEnd;
                _draggingClip = true;
            }
            else
            {
                _dragMode = DragMode.Move;
                _draggingClip = true;
            }
        }

        private Track? HitTestTrack(double y)
        {
            if (Editor is null)
                return null;
            foreach (var track in Editor.Document.Tracks)
            {
                if (y >= TimelineGeometry.TrackTop(track.Index) + RulerHeight
                    && y < TimelineGeometry.TrackBottom(track.Index) + RulerHeight)
                    return track;
            }
            return null;
        }

        private Clip? HitTestClip(Track track, double time, double pointerX)
        {
            return track.Clips.FirstOrDefault(c =>
                time >= c.StartSec && time < c.StartSec + c.DurSec);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (Editor is null)
                return;

            if (e.Key == Key.Delete || e.Key == Key.Back)
            {
                if (_selectedClipId is not null)
                {
                    Editor.RippleDelete(_selectedClipId);
                    _selectedClipId = null;
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

        private TrackKind LastTrackKind()
        {
            if (Editor is null || Editor.Document.Tracks.Count == 0)
                return TrackKind.Video;
            return Editor.Document.Tracks[^1].Kind;
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

            // clip everything below the ruler + right of the header so clips never
            // draw over the header column when zoomed/scrolled
            var clipArea = new Rect(TrackHeaderWidth, RulerHeight,
                Math.Max(0, Bounds.Width - TrackHeaderWidth), Math.Max(0, Bounds.Height - RulerHeight));

            for (var i = 0; i < Editor.Document.Tracks.Count; i++)
            {
                var track = Editor.Document.Tracks[i];
                var top = TimelineGeometry.TrackTop(i) + RulerHeight;
                var height = TimelineGeometry.TrackHeight;

                // track header (left column)
                var isTrackSelected = track.Id == _selectedTrackId;
                context.DrawRectangle(isTrackSelected ? SelectionBrush : TrackHeaderBrush, null, new Rect(0, top, TrackHeaderWidth, height));
                context.DrawLine(new Pen(BorderBrush, 1), new Point(TrackHeaderWidth, top), new Point(TrackHeaderWidth, top + height));
                context.DrawLine(new Pen(BorderBrush, 1), new Point(0, top + height), new Point(Bounds.Width, top + height));

                var trackLabel = track.Name ?? track.Kind.ToString();
                var trackText = new FormattedText(
                    trackLabel,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    Typeface.Default,
                    12,
                    TrackHeaderTextBrush);
                context.DrawText(trackText, new Point(8, top + (height - trackText.Height) / 2));

                // mute + delete buttons in header
                DrawHeaderButton(context, "M", track.Muted ? MutedBrush : BorderBrush, HeaderButtonRect(track, ButtonKind.Mute), track.Muted);
                DrawHeaderButton(context, "x", DeleteBrush, HeaderButtonRect(track, ButtonKind.Delete), false);

                // clip area, offset by header + viewport scroll
                using (context.PushClip(clipArea))
                {
                    foreach (var clip in track.Clips)
                    {
                        var x = TrackHeaderWidth + Viewport.TimeToX(clip.StartSec);
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

                        // clip widget = label strip (name) on top + body (filmstrip/waveform)
                        var clipTop = top + 2;
                        var totalHeight = TimelineGeometry.ClipTotalHeight;
                        var labelHeight = TimelineGeometry.ClipLabelHeight;
                        var bodyHeight = TimelineGeometry.ClipHeight;

                        var widgetRect = new Rect(x, clipTop, w, totalHeight);
                        var rounded = new RoundedRect(widgetRect, ClipCornerRadius);

                        var shadowRect = new RoundedRect(widgetRect.Translate(new Vector(0, 1)), ClipCornerRadius);
                        context.DrawRectangle(ClipShadow, null, shadowRect);

                        var isClipSelected = _selectedClipId is not null
                            && Editor.Selection.IsSelected(clip.Id);
                        var outline = isClipSelected ? SelectionBrush : border;

                        // --- name strip above the clip body ---
                        var labelRect = new Rect(x, clipTop, w, labelHeight);
                        var labelBrush = isClipSelected
                            ? new SolidColorBrush(Color.FromArgb(80, 0x4d, 0xa3, 0xff))
                            : new SolidColorBrush(Color.Parse("#1c1c1e"));
                        context.DrawRectangle(labelBrush, null, new RoundedRect(labelRect, new CornerRadius(ClipCornerRadius, ClipCornerRadius, 0, 0)));

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
                            context.DrawText(labelText, new Point(x + 4, labelRect.Y + (labelHeight - labelText.Height) / 2));
                        }

                        // --- clip body ---
                        var rect = new Rect(x, clipTop + labelHeight, w, bodyHeight);
                        var bodyRounded = new RoundedRect(rect, new CornerRadius(0, 0, ClipCornerRadius, ClipCornerRadius));

                        // draw filmstrip as video clip background
                        if (clip is VideoClip vc && MediaById is not null
                            && MediaById.TryGetValue(vc.SourceId, out var asset)
                            && asset?.Filmstrip is string stripPath
                            && GetBitmap(stripPath) is Bitmap strip)
                        {
                            DrawFilmstrip(context, rect, vc, asset, strip);
                            context.DrawRectangle(Brushes.Transparent, new Pen(outline, isClipSelected ? 2 : 1), bodyRounded);
                        }
                        // draw waveform as audio clip background
                        else if (clip is AudioClip ac && MediaById is not null
                            && MediaById.TryGetValue(ac.SourceId, out var audioAsset)
                            && audioAsset is not null)
                        {
                            DrawAudioWaveform(context, rect, ac, audioAsset);
                            context.DrawRectangle(Brushes.Transparent, new Pen(outline, isClipSelected ? 2 : 1), bodyRounded);
                        }
                        else
                        {
                            context.DrawRectangle(fill, new Pen(outline, isClipSelected ? 2 : 1), bodyRounded);
                        }
                    }   // foreach clip
                }   // PushClip(clipArea)
            }   // for tracks

            // add-track button at bottom of header column
            if (Editor.Document.Tracks.Count > 0)
            {
                var addRect = AddTrackButtonRect();
                DrawHeaderButton(context, "+", BorderBrush, addRect, false);
            }

            // drop preview: highlight target track + ghost clip rect
            if (_dropPreviewTrack is not null && _dropPreviewTime >= 0 && _dropPreviewAsset is not null)
            {
                var trackTop = TimelineGeometry.TrackTop(_dropPreviewTrack.Index) + RulerHeight;
                context.DrawRectangle(new SolidColorBrush(Color.Parse("#2244aa44")), null,
                    new Rect(0, trackTop, Bounds.Width, TimelineGeometry.TrackHeight));

                var ghostX = TrackHeaderWidth + Viewport.TimeToX(_dropPreviewTime);
                var ghostW = _dropPreviewAsset.DurationSec * px;
                var ghostRect = new Rect(ghostX, trackTop + 2, ghostW, TimelineGeometry.ClipTotalHeight);
                context.DrawRectangle(new SolidColorBrush(Color.Parse("#88ffffff")), null,
                    new RoundedRect(ghostRect, ClipCornerRadius));
                context.DrawRectangle(Brushes.Transparent, new Pen(SelectionBrush, 2),
                    new RoundedRect(ghostRect, ClipCornerRadius));
            }

            // drop indicator line
            if (_dropTimeSec >= 0 && _dropPreviewTrack is null)
            {
                var x = TrackHeaderWidth + Viewport.TimeToX(_dropTimeSec);
                context.DrawLine(new Pen(Brushes.OrangeRed, 2), new Point(x, RulerHeight), new Point(x, Bounds.Height));
            }

            // playhead
            var playheadX = TrackHeaderWidth + Viewport.TimeToX(PlayheadTimeSec);
            if (playheadX >= TrackHeaderWidth)
            {
                context.DrawLine(new Pen(Brushes.Red, 1.5), new Point(playheadX, 0), new Point(playheadX, Bounds.Height));
                // handle in ruler
                context.DrawRectangle(Brushes.Red, null, new Rect(playheadX - 4, 0, 8, 10));
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
