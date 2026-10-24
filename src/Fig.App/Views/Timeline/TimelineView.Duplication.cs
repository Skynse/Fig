using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Fig.Core.Timeline;
using Fig.App.Services;

namespace Fig.App.Views;

public partial class TimelineView
{
    /// <summary>A Ctrl-drag copy of clips or an effect bar (keyframes copy through the shared diamond drag).</summary>
    private sealed record CopyDrag(IEffectOwner Clip, Point Press, string[] ClipIds, EffectInstance? Effect = null,
        bool Active = false);
    private CopyDrag? _copyDrag;
    private IPointer? _copyPointer;
    private Point _copyPosition;

    private bool TryArmCopyDrag(PointerPressedEventArgs e, Point pos)
    {
        if (Editor is null || !(e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            || e.KeyModifiers.HasFlag(KeyModifiers.Shift) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return false;
        if (HitTestRack(pos) is { Item.Effect: { } effect, Part: RackPart.Body } rack)
            _copyDrag = new CopyDrag(rack.Clip, pos, Array.Empty<string>(), effect);
        else if (ClipAt(pos, out _) is { } clip)
        {
            var selected = Editor.Selection.IsSelected(clip.Id) ? Editor.Selection.SelectedClipIds
                .Select(Editor.FindClipForView).Where(c => c is not null).Cast<Clip>() : new[] { clip };
            var members = selected.SelectMany(c => WithLinked(c, e.KeyModifiers)).DistinctBy(c => c.Id);
            _copyDrag = new CopyDrag(clip, pos, members.Select(c => c.Id).ToArray());
        }
        else return false;
        _copyPosition = pos;
        _copyPointer = e.Pointer;
        e.Pointer.Capture(this);
        return true;
    }

    private bool UpdateCopyDrag(Point pos)
    {
        if (_copyDrag is not { } drag) return false;
        _copyPosition = pos;
        if (!drag.Active && (Math.Abs(pos.X - drag.Press.X) >= MarqueeStartPx || Math.Abs(pos.Y - drag.Press.Y) >= MarqueeStartPx))
            _copyDrag = drag with { Active = true };
        Cursor = new Cursor(StandardCursorType.DragCopy);
        InvalidateVisual();
        return true;
    }

    private IEffectOwner? CopyTarget(Point pos)
    {
        var track = HitTestTrack(pos.Y);
        return track;
    }

    private bool FinishCopyDrag(PointerReleasedEventArgs e)
    {
        if (_copyDrag is not { } drag || Editor is null) return false;
        var pos = e.GetPosition(this);
        CancelCopyDrag();
        if (!drag.Active)
        {
            if (drag.Effect is null) ClickSelectClip((Clip)drag.Clip, _modifiersAtPress | KeyModifiers.Control);
            else
            {
                // Ctrl-click on a bar adds it to the selection, or takes it out
                Editor.Selection.ToggleEffect(drag.Clip.Id, drag.Effect.Id);
                InvalidateVisual();
            }
            return true;
        }
        double delta = (pos.X - drag.Press.X) / Viewport.PixelsPerSecond;
        if (drag.Effect is null)
        {
            var track = HitTestTrack(pos.Y);
            var copies = track is null ? Array.Empty<Clip>() : Editor.DuplicateClipsAt(drag.ClipIds,
                drag.Clip.Id, Snap(Math.Max(0, drag.Clip.StartSec + delta)), track.Id);
            if (copies.Count == 0) _editorVm?.Notify?.Invoke("Drop the copy into free space on a matching track");
        }
        else if (CopyTarget(pos) is { } target)
        {
            double localDelta = Editor.SnapTime(drag.Clip.StartSec + delta - target.StartSec);
            var effect = Editor.DuplicateEffectAt(drag.Clip.Id, drag.Effect.Id, target.Id, localDelta);
            if (effect is not null) _editorVm?.Properties.SelectEffect(target.Id, effect.Id);
            else _editorVm?.Notify?.Invoke("Drop the effect onto a compatible clip");
        }
        else _editorVm?.Notify?.Invoke("Drop the effect onto a clip");
        InvalidateMeasure();
        InvalidateVisual();
        return true;
    }

    private void CancelCopyDrag()
    {
        _copyDrag = null;
        var pointer = _copyPointer;
        _copyPointer = null;
        if (pointer?.Captured == this) pointer.Capture(null);
        ClearSnapGuide();
        Cursor = Cursor.Default;
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_copyDrag is not null) CancelCopyDrag();
    }

    private void DrawCopyPreview(DrawingContext context)
    {
        if (_copyDrag is not { Active: true } drag || Editor is null) return;
        var track = HitTestTrack(_copyPosition.Y);
        if (track is null) return;
        double delta = (_copyPosition.X - drag.Press.X) / Viewport.PixelsPerSecond;
        double start = Math.Max(0, drag.Clip.StartSec + delta);
        double duration = drag.Clip.DurSec;
        if (drag.Effect is not null)
        {
            var target = CopyTarget(_copyPosition);
            if (target is null) return;
            start = target.StartSec;
            duration = target.DurSec;
        }
        double top = TrackTop(Editor.Document.Tracks.IndexOf(track)) + TimelineGeometry.ClipInset;
        var rect = new Rect(TrackHeaderWidth + Viewport.TimeToX(start), top,
            Math.Max(5, duration * Viewport.PixelsPerSecond), ClipTotal(track));
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(90, 95, 156, 248)), PenOf(Brushes.White, 1.5), rect, 3, 3);
        var text = new FormattedText(drag.Effect is null ? $"Copy {drag.ClipIds.Length} clip(s)" : "Copy effect",
            System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Brushes.White);
        context.DrawText(text, new Point(rect.X + 8, rect.Y + 5));
    }
}
