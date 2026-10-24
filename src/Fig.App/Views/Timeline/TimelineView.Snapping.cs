using System;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Fig.Core.Timeline;

namespace Fig.App.Views
{
    /// <summary>
    /// Snapping as the user sees it: how far things reach in pixels, the visible grid, the guide line that
    /// shows what you caught, and Alt to move freely. The rules themselves live in
    /// <see cref="TimelineEditor.SnapMagnetic"/>.
    /// </summary>
    public partial class TimelineView
    {
        /// <summary>How close (screen pixels) something must be to snap to it.</summary>
        /// <summary>How close (in pixels) something has to be to snap; a preference.</summary>
        private double SnapReachPx => _editorVm?.Preferences.Current.Editing.SnapReachPx ?? 9;

        private static readonly IBrush SnapGuideBrush = new SolidColorBrush(Color.FromArgb(230, 0xff, 0xd6, 0x0a));

        private double? _snapGuideSec;
        private bool _freeMove;

        /// <summary>Holding Alt moves things freely (frame by frame) instead of catching on edges and the grid.</summary>
        private void NoteModifiers(KeyModifiers modifiers) => _freeMove = modifiers.HasFlag(KeyModifiers.Alt);

        /// <summary>Keeps the editor's snap reach and grid matched to the current zoom and playhead.</summary>
        private void UpdateSnapScale()
        {
            if (Editor is null)
                return;
            var pps = Math.Max(1, Viewport.PixelsPerSecond);
            Editor.SnapWindowSec = SnapReachPx / pps;
            Editor.SnapGridSec = RulerCalculator.PickScale(pps, Editor.Document.Rate.Fps).MinorSec;   // the ruler's small ticks
            Editor.SnapPlayheadSec = PlayheadTimeSec;
        }

        /// <summary>
        /// The one place the timeline snaps a time. Remembers what it caught so a guide line can be drawn
        /// while dragging.
        /// </summary>
        private double Snap(double sec, string? excludeClipId = null, string? excludeMarkerId = null, bool includePlayhead = true,
            IReadOnlySet<string>? excludeClips = null)
        {
            if (Editor is null)
                return sec;
            if (_freeMove)
            {
                _snapGuideSec = null;
                return Editor.SnapTime(sec);
            }

            UpdateSnapScale();
            var result = Editor.SnapMagnetic(sec, excludeClipId, excludeMarkerId, includePlayhead, excludeClips);
            _snapGuideSec = result.Caught && result.Kind != SnapKind.Grid ? result.Time : null;
            return result.Time;
        }

        private void ClearSnapGuide()
        {
            if (_snapGuideSec is not null)
            {
                _snapGuideSec = null;
                InvalidateVisual();
            }
        }

        /// <summary>A vertical line where the thing being dragged caught on a clip edge, marker or the playhead.</summary>
        private void DrawSnapGuide(DrawingContext context)
        {
            if (_snapGuideSec is not { } sec)
                return;
            var x = TrackHeaderWidth + Viewport.TimeToX(sec);
            if (x < TrackHeaderWidth || x > Bounds.Width)
                return;
            context.DrawLine(PenOf(SnapGuideBrush, 1.5), new Point(x, MarkerLaneHeight), new Point(x, Bounds.Height));
        }
    }
}
