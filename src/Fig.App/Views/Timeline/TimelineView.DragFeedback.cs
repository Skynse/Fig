using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using Fig.App.Services;
using Fig.Core.Timeline;

namespace Fig.App.Views
{
    /// <summary>
    /// What a drag shows and does near the edges. Moving clips leaves a faint outline where they came from, and when
    /// they can't go where the pointer is (another clip is there) a red ghost shows that spot while they wait at
    /// the last place they fit. Any drag held near an edge of the timeline scrolls it that way, faster the closer
    /// the pointer is, and the drag follows as if the pointer had moved.
    /// </summary>
    public partial class TimelineView
    {
        private const double AutoScrollZonePx = 48;
        private const double AutoScrollMaxPxPerTick = 22;

        private static readonly IBrush GhostOriginFill = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
        private static readonly Pen GhostOriginPen = new(new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), 1, DashStyle.Dash);
        private static readonly IBrush GhostBlockedFill = new SolidColorBrush(Color.FromArgb(70, 0xe5, 0x48, 0x4d));
        private static readonly Pen GhostBlockedPen = new(new SolidColorBrush(Color.FromArgb(220, 0xe5, 0x48, 0x4d)), 1.5);

        // where the pointer would put the dragged clips (seconds from where they started), and whether they can go there
        private double _ghostDelta;
        private bool _ghostBlocked;
        // the track the blocked clips were headed for, when it isn't their own
        private string? _ghostTrackId;
        private readonly Dictionary<string, string> _dragOriginTracks = new();

        private DispatcherTimer? _autoScrollTimer;
        private Point _lastDragPos;
        private bool _externalDragOver;
        private Point _externalDragPos;

        private void RememberDragOrigins()
        {
            _dragOriginTracks.Clear();
            foreach (var c in _dragMembers)
                if (Editor?.Document.Tracks.FirstOrDefault(t => t.Clips.Contains(c)) is { } track)
                    _dragOriginTracks[c.Id] = track.Id;
            _ghostDelta = 0;
            _ghostBlocked = false;
        }

        private Rect? ClipSlot(string trackId, double start, double dur)
        {
            if (Editor is null)
                return null;
            var index = Editor.Document.Tracks.FindIndex(t => t.Id == trackId);
            if (index < 0)
                return null;
            var track = Editor.Document.Tracks[index];
            return new Rect(TrackHeaderWidth + Viewport.TimeToX(start), TrackTop(index) + TimelineGeometry.ClipInset,
                Math.Max(3, dur * Viewport.PixelsPerSecond), ClipTotal(track));
        }

        /// <summary>Draws the outline where moved clips came from, and the blocked ghost.</summary>
        private void DrawMoveGhost(DrawingContext context)
        {
            if (!_draggingClip || _dragMode != DragMode.Move || Editor is null)
                return;
            var area = new Rect(TrackHeaderWidth, RulerHeight, Math.Max(0, Bounds.Width - TrackHeaderWidth), Math.Max(0, Bounds.Height - RulerHeight));
            using (context.PushClip(area))
            {
                foreach (var clip in _dragMembers)
                {
                    if (!_dragOriginals.TryGetValue(clip.Id, out var origin) || !_dragOriginTracks.TryGetValue(clip.Id, out var originTrack))
                        continue;
                    var current = Editor.Document.Tracks.FirstOrDefault(t => t.Clips.Contains(clip));
                    var moved = Math.Abs(clip.StartSec - origin) > 1e-6 || current?.Id != originTrack;
                    if (moved && ClipSlot(originTrack, origin, clip.DurSec) is { } from)
                        context.DrawRectangle(GhostOriginFill, GhostOriginPen, new RoundedRect(from, ClipCornerRadius));
                    // clips of the kind that was heading to another track show there; a linked partner on its own
                    var ghostTrack = _ghostTrackId is { } wantedTrack && current is not null
                        && Editor.Document.Tracks.FirstOrDefault(t => t.Id == wantedTrack)?.Kind == current.Kind
                            ? wantedTrack : current?.Id;
                    if (_ghostBlocked && ghostTrack is not null
                        && ClipSlot(ghostTrack, Math.Max(0, origin + _ghostDelta), clip.DurSec) is { } wanted)
                        context.DrawRectangle(GhostBlockedFill, GhostBlockedPen, new RoundedRect(wanted, ClipCornerRadius));
                }
            }
        }

        /// <summary>Starts the edge-scroll timer; it stops itself once nothing is being dragged.</summary>
        private void EnsureAutoScroll()
        {
            if (_autoScrollTimer is null)
            {
                _autoScrollTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) => AutoScrollTick());
            }
            if (!_autoScrollTimer.IsEnabled)
                _autoScrollTimer.Start();
        }

        private bool DragInProgress
            => _draggingClip || _rackDrag is not null || _diamondDrag is not null
               || _copyDrag is { Active: true } || _trackDrag is { Active: true } || _draggingMarker || _draggingPlayhead || _marqueeActive;

        private void AutoScrollTick()
        {
            if (Editor is null || (!DragInProgress && !_externalDragOver))
            {
                _autoScrollTimer?.Stop();
                return;
            }
            var pos = _externalDragOver ? _externalDragPos : _lastDragPos;

            // across: the clip area's left and right edges scroll the time
            double dx = 0;
            if (pos.X < TrackHeaderWidth + AutoScrollZonePx && pos.X > TrackHeaderWidth - AutoScrollZonePx)
                dx = -Pressure(TrackHeaderWidth + AutoScrollZonePx - pos.X);
            else if (pos.X > Bounds.Width - AutoScrollZonePx)
                dx = Pressure(pos.X - (Bounds.Width - AutoScrollZonePx));
            if (dx < 0 && Viewport.ScrollTime <= 0)
                dx = 0;

            // up and down: the visible part of the tracks (the timeline sits in a scroll viewer)
            double dy = 0;
            if (_scroller is { } scroller)
            {
                var top = scroller.Offset.Y + RulerHeight;
                var bottom = scroller.Offset.Y + scroller.Viewport.Height;
                if (pos.Y < top + AutoScrollZonePx && scroller.Offset.Y > 0)
                    dy = -Pressure(top + AutoScrollZonePx - pos.Y);
                else if (pos.Y > bottom - AutoScrollZonePx && scroller.Offset.Y < scroller.Extent.Height - scroller.Viewport.Height)
                    dy = Pressure(pos.Y - (bottom - AutoScrollZonePx));
                if (dy != 0)
                {
                    var max = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
                    var y = Math.Clamp(scroller.Offset.Y + dy, 0, max);
                    dy = y - scroller.Offset.Y;
                    scroller.Offset = new Vector(scroller.Offset.X, y);
                }
            }

            if (dx == 0 && dy == 0)
                return;
            if (dx != 0)
                Viewport.SetScrollTime(Math.Max(0, Viewport.ScrollTime + dx / Math.Max(1e-6, Viewport.PixelsPerSecond)));

            // the pointer hasn't moved on screen, so on the content it has moved by the scroll
            if (_externalDragOver)
                _externalDragPos = new Point(pos.X, pos.Y + dy);
            else
            {
                _lastDragPos = new Point(pos.X, pos.Y + dy);
                PointerMovedAt(_lastDragPos, leftPressed: true);
            }
            InvalidateVisual();
        }

        /// <summary>Scroll pixels per tick for a pointer <paramref name="depth"/> pixels into the edge zone.</summary>
        private static double Pressure(double depth)
            => AutoScrollMaxPxPerTick * Math.Pow(Math.Clamp(depth / AutoScrollZonePx, 0, 1), 1.6);
    }
}
