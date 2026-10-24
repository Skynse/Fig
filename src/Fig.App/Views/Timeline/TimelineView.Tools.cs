using System;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Fig.Core.Timeline;

namespace Fig.App.Views
{
    /// <summary>
    /// The blade tool and gaps. With the blade (B) a line follows the pointer over a clip and a click cuts it there
    /// (Shift: every track). A click in the empty stretch between two clips selects that gap; Delete closes it.
    /// </summary>
    public partial class TimelineView
    {
        private static readonly IBrush GapFill = new SolidColorBrush(Color.FromArgb(46, 0x5f, 0x9c, 0xf8));
        private static readonly Pen GapPen = new(new SolidColorBrush(Color.FromArgb(200, 0x5f, 0x9c, 0xf8)), 1, DashStyle.Dash);
        private static readonly Pen BladePen = new(new SolidColorBrush(Color.FromArgb(240, 0xff, 0x5a, 0x5a)), 1.5);

        private bool BladeActive => _editorVm?.BladeTool == true;

        private KeyModifiers _pointerModifiers;

        // where the blade would cut: the time, and the clip (null with Shift: every track)
        private (double Time, Clip? Clip)? _bladeHover;

        // the selected gap, and the gap under a press that may become it
        private (string TrackId, double Start, double End)? _selectedGap;
        private (string TrackId, double Start, double End)? _pressGap;

        /// <summary>The blade follows the pointer. True while the blade is out (it owns the hover).</summary>
        private bool UpdateBladeHover(Point pos, KeyModifiers modifiers)
        {
            if (!BladeActive)
            {
                if (_bladeHover is not null)
                {
                    _bladeHover = null;
                    InvalidateVisual();
                }
                return false;
            }
            var clip = ClipAt(pos, out _);
            var all = modifiers.HasFlag(KeyModifiers.Shift);
            _bladeHover = clip is null && !all ? null : (Snap(Math.Max(0, XToTime(pos.X))), all ? null : clip);
            Cursor = new Cursor(StandardCursorType.Cross);
            InvalidateVisual();
            return true;
        }

        /// <summary>A press with the blade out: cuts the clip under it (Shift: every track) where the line is. True when it acted.</summary>
        private bool BladePress(Point pos, KeyModifiers modifiers)
        {
            if (!BladeActive || Editor is null)
                return false;
            var at = Snap(Math.Max(0, XToTime(pos.X)));
            if (modifiers.HasFlag(KeyModifiers.Shift))
            {
                Editor.SplitAllAtPlayhead(at);
            }
            else if (ClipAt(pos, out _) is { } clip && clip.StartSec < at - 1e-6 && at < clip.StartSec + clip.DurSec - 1e-6)
            {
                Editor.Cut(clip.Id, Editor.SnapTime(at));
            }
            ClearSnapGuide();
            InvalidateVisual();
            return true;
        }

        private void DrawBlade(DrawingContext context)
        {
            if (_bladeHover is not { } blade || !BladeActive || Editor is null)
                return;
            var x = Math.Round(TrackHeaderWidth + Viewport.TimeToX(blade.Time)) + 0.5;
            if (blade.Clip is { } clip && Editor.Document.Tracks.FirstOrDefault(t => t.Clips.Contains(clip)) is { } track)
            {
                var index = Editor.Document.Tracks.IndexOf(track);
                var top = TrackTop(index) + TimelineGeometry.ClipInset;
                context.DrawLine(BladePen, new Point(x, top), new Point(x, top + ClipTotal(track)));
                return;
            }
            context.DrawLine(BladePen, new Point(x, RulerHeight), new Point(x, Bounds.Height));
        }

        /// <summary>A press on empty space: remember the gap under it, which a click (not a box drag) selects.</summary>
        private void NotePressGap(Track? track, double time)
            => _pressGap = track is not null && Editor?.GapAt(track.Id, time) is { } gap ? (track.Id, gap.Start, gap.End) : null;

        /// <summary>A click on empty space ended: select the gap it was in, if any.</summary>
        private void SelectPressedGap()
        {
            _selectedGap = _pressGap;
            _pressGap = null;
        }

        /// <summary>Delete with a gap selected closes it. True when there was one.</summary>
        private bool DeleteSelectedGap()
        {
            if (_selectedGap is not { } gap || Editor is null)
                return false;
            _selectedGap = null;
            var closed = _editorVm?.CloseGap(gap.TrackId, (gap.Start + gap.End) / 2) ?? Editor.CloseGap(gap.TrackId, (gap.Start + gap.End) / 2);
            InvalidateVisual();
            return closed;
        }

        private void DrawSelectedGap(DrawingContext context)
        {
            if (_selectedGap is not { } gap || Editor is null)
                return;
            var index = Editor.Document.Tracks.FindIndex(t => t.Id == gap.TrackId);
            if (index < 0)
                return;
            var track = Editor.Document.Tracks[index];
            var x = TrackHeaderWidth + Viewport.TimeToX(gap.Start);
            var rect = new Rect(x, TrackTop(index) + TimelineGeometry.ClipInset,
                Math.Max(2, (gap.End - gap.Start) * Viewport.PixelsPerSecond), ClipTotal(track));
            context.DrawRectangle(GapFill, GapPen, new RoundedRect(rect, ClipCornerRadius));
        }
    }
}
