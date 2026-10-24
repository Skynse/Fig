using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Fig.Core.Timeline;

namespace Fig.App.Views
{
    /// <summary>
    /// How clips get selected: click, Ctrl/Shift-click to add or remove, drag a box on empty space, Ctrl+A.
    /// A clip's linked partners (its audio or video) come along unless linked selection is off or Alt is held.
    /// </summary>
    public partial class TimelineView
    {
        private static readonly IBrush MarqueeFill = new SolidColorBrush(Color.FromArgb(40, 0x5f, 0x9c, 0xf8));
        private static readonly Pen MarqueePen = new(new SolidColorBrush(Color.FromArgb(220, 0x5f, 0x9c, 0xf8)), 1);

        private const double MarqueeStartPx = 4;

        private bool _marqueeArmed;
        private bool _marqueeActive;
        private bool _marqueeAdditive;
        private Clip? _marqueeClickClip;   // region select started on this clip: a plain click still selects it
        private Point _marqueeFrom;
        private Point _marqueeTo;

        /// <summary>A plain click on a clip that was already part of a multi-selection collapses it on release.</summary>
        private string? _collapseToClipId;
        private double _pressedPx;

        private bool LinkedSelectionOn => _editorVm?.LinkedSelection ?? true;

        /// <summary>The clips that act together with <paramref name="clip"/>: its linked partners, if linking applies.</summary>
        private IReadOnlyList<Clip> WithLinked(Clip clip, KeyModifiers modifiers = KeyModifiers.None)
        {
            if (Editor is null)
                return new[] { clip };
            return LinkedSelectionOn && !modifiers.HasFlag(KeyModifiers.Alt) ? Editor.LinkGroup(clip.Id) : new[] { clip };
        }

        /// <summary>
        /// Applies a click on a clip to the selection.
        /// None: select it (and its partners). Ctrl: add it, or take it out if it is already selected.
        /// Shift: add it. Clicking something already in a multi-selection keeps the selection so it can be dragged.
        /// </summary>
        private void ClickSelectClip(Clip clip, KeyModifiers modifiers)
        {
            if (Editor is null)
                return;
            var ids = WithLinked(clip, modifiers).Select(c => c.Id).ToList();
            var current = Editor.Selection.SelectedClipIds;
            _collapseToClipId = null;

            if (modifiers.HasFlag(KeyModifiers.Control))
            {
                var allIn = ids.All(Editor.Selection.IsSelected);
                Editor.Selection.SetClips(allIn ? current.Except(ids) : current.Union(ids));
            }
            else if (modifiers.HasFlag(KeyModifiers.Shift))
            {
                Editor.Selection.SetClips(current.Union(ids));
            }
            else if (Editor.Selection.IsSelected(clip.Id) && current.Count > 1)
            {
                _collapseToClipId = clip.Id;     // a click without a drag narrows the selection to this clip
            }
            else
            {
                Editor.Selection.SelectClips(ids);
            }
            _selectedTrackId = null;
        }

        /// <summary>Ctrl+A: every clip, effect bar and marker at once.</summary>
        private void SelectAll()
        {
            if (Editor is null)
                return;
            Editor.SelectAll();
            InvalidateVisual();
        }

        private void ClearSelection()
        {
            if (Editor is null)
                return;
            Editor.Selection.Clear();
            InvalidateVisual();
        }

        // ---- box selection ---------------------------------------------------------------

        private void ArmMarquee(Point at, KeyModifiers modifiers)
        {
            _marqueeArmed = true;
            _marqueeActive = false;
            _marqueeFrom = at;
            _marqueeTo = at;
            _marqueeAdditive = modifiers.HasFlag(KeyModifiers.Shift) || modifiers.HasFlag(KeyModifiers.Control);
        }

        /// <summary>Returns true while a box selection is in progress and consumed the move.</summary>
        private bool UpdateMarquee(Point at)
        {
            if (!_marqueeArmed)
                return false;
            _marqueeTo = at;
            if (!_marqueeActive && (Math.Abs(at.X - _marqueeFrom.X) > MarqueeStartPx || Math.Abs(at.Y - _marqueeFrom.Y) > MarqueeStartPx))
                _marqueeActive = true;
            if (_marqueeActive)
                InvalidateVisual();
            return true;
        }

        /// <summary>Ends a box selection (or an empty click). Selects what the box touches.</summary>
        private void FinishMarquee(KeyModifiers modifiers)
        {
            if (!_marqueeArmed || Editor is null)
                return;
            var wasBox = _marqueeActive;
            _marqueeArmed = false;
            _marqueeActive = false;
            var clickedClip = _marqueeClickClip;
            _marqueeClickClip = null;

            if (!wasBox && clickedClip is null && !_marqueeAdditive)
                SelectPressedGap();
            _pressGap = null;

            if (!wasBox && clickedClip is not null)
            {
                ClickSelectClip(clickedClip, modifiers);
                InvalidateVisual();
                return;
            }

            if (!wasBox)
            {
                // a plain click on empty space: deselect (unless extending)
                if (!_marqueeAdditive)
                {
                    Editor.Selection.Clear();
                    _selectedTrackId = null;
                }
                InvalidateVisual();
                return;
            }

            // exactly what the box touches, clips and effect bars alike: a region does not pull in clips from
            // other lanes through links
            var box = MarqueeBox();
            var keys = EffectsInBox(box).Select(b => new SelectionKey(SelectionGroup.Effect, b.EffectId, b.OwnerId))
                .Concat(ClipsInBox(box).Select(c => c.Key));
            Editor.Selection.Replace(_marqueeAdditive ? Editor.Selection.Items.Concat(keys) : keys);
            _selectedTrackId = null;
            InvalidateVisual();
        }

        private Rect MarqueeBox() => new(
            Math.Min(_marqueeFrom.X, _marqueeTo.X), Math.Min(_marqueeFrom.Y, _marqueeTo.Y),
            Math.Abs(_marqueeTo.X - _marqueeFrom.X), Math.Abs(_marqueeTo.Y - _marqueeFrom.Y));

        /// <summary>The clips whose drawn rectangle the box touches (not whole lanes, not linked partners).</summary>
        private List<Clip> ClipsInBox(Rect box)
        {
            var hit = new List<Clip>();
            if (Editor is null)
                return hit;
            for (var t = 0; t < Editor.Document.Tracks.Count; t++)
            {
                var track = Editor.Document.Tracks[t];
                var top = TrackTop(t) + TimelineGeometry.ClipInset;
                var bottom = top + ClipTotal(track);
                if (box.Bottom < top || box.Top > bottom)
                    continue;
                foreach (var clip in track.Clips)
                {
                    var left = TrackHeaderWidth + Viewport.TimeToX(clip.StartSec);
                    var right = left + clip.DurSec * Viewport.PixelsPerSecond;
                    if (right >= box.Left && left <= box.Right)
                        hit.Add(clip);
                }
            }
            return hit;
        }

        /// <summary>The selection box, with the clips it would pick up outlined while you drag.</summary>
        private void DrawMarquee(DrawingContext context)
        {
            if (!_marqueeActive || Editor is null)
                return;
            var box = MarqueeBox();
            var outline = PenOf(Color.FromArgb(235, 0x5f, 0x9c, 0xf8), 2);
            foreach (var bar in EffectsInBox(box))
                context.DrawRectangle(null, outline, bar.Rect);
            foreach (var clip in ClipsInBox(box))
            {
                var index = Editor.Document.Tracks.FindIndex(t => t.Clips.Contains(clip));
                if (index < 0)
                    continue;
                var track = Editor.Document.Tracks[index];
                var rect = new Rect(TrackHeaderWidth + Viewport.TimeToX(clip.StartSec),
                    TrackTop(index) + TimelineGeometry.ClipInset,
                    clip.DurSec * Viewport.PixelsPerSecond, ClipTotal(track));
                context.DrawRectangle(null, outline, rect.Intersect(new Rect(TrackHeaderWidth, 0, Bounds.Width, Bounds.Height)));
            }
            context.DrawRectangle(MarqueeFill, MarqueePen, box);
        }
    }
}
