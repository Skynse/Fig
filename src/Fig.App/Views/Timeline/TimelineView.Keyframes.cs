using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Fig.App.Services;
using Fig.Core.Timeline;

namespace Fig.App.Views
{
    /// <summary>
    /// Every keyframe diamond on the timeline works the same, whatever it belongs to: a clip's own (opacity, volume,
    /// crop, text size...) on the clip, an effect's on its bar, a speed curve's on the speed line. Click to pick it
    /// (and jump there), drag to move it, Ctrl-drag to copy it, right-click to jump, duplicate, change its easing or
    /// delete it; Delete removes a picked one. A speed point also drags up and down for its speed. All of it goes
    /// through the editor's one set of keyframe edits.
    /// </summary>
    public partial class TimelineView
    {
        /// <summary>A diamond under the pointer. <paramref name="ValueDrag"/>: dragging up and down changes its value (speed points).</summary>
        private readonly record struct DiamondHit(KeyframeRef Keyframe, bool ValueDrag);

        /// <summary>A diamond being dragged: the one picked, where it is now (host seconds), and whether this is a copy.</summary>
        private sealed record DiamondDrag(KeyframeRef Picked, double CurrentSec, string Gesture, bool ValueDrag, bool Copy,
            double PressY, bool Moved = false);

        private DiamondDrag? _diamondDrag;
        private KeyframeRef? _hoverDiamond;
        private int _diamondGestureSeq;

        private static readonly IBrush DiamondGhostBrush = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255));

        /// <summary>The diamond under <paramref name="pos"/>, of any kind.</summary>
        private DiamondHit? HitTestDiamond(Point pos)
        {
            if (Editor is null)
                return null;
            if (HitTestSpeedPoint(pos) is { } speed && ClipTime.Curve(speed.Clip) is { } curve)
                return new DiamondHit(new KeyframeRef(KeyframeHostRef.OfSpeed(speed.Clip.Id), curve[speed.Index].TimeSec), true);
            if (HitTestRack(pos) is { Part: RackPart.Keyframe, Item.Effect: { } fx } rack)
                return new DiamondHit(new KeyframeRef(KeyframeHostRef.OfEffect(rack.Clip.Id, fx.Id), rack.KeyframeSec), false);
            if (HitTestKeyframe(pos) is { } own)
                return new DiamondHit(new KeyframeRef(KeyframeHostRef.OfClip(own.Clip.Id), own.TimeSec - own.Clip.StartSec), false);
            return null;
        }

        /// <summary>True when the diamond of <paramref name="host"/> at <paramref name="timeSec"/> is picked, held or hovered.</summary>
        private bool IsDiamondHot(KeyframeHostRef host, double timeSec)
        {
            static bool Is(KeyframeRef? k, KeyframeHostRef host, double t) => k is { } r && r.Host == host && Math.Abs(r.TimeSec - t) < 1e-4;
            return _diamondDrag is { Copy: false } d && d.Picked.Host == host && Math.Abs(d.CurrentSec - timeSec) < 1e-4
                   || Is(Editor?.Selection.Keyframe, host, timeSec) || Is(_hoverDiamond, host, timeSec);
        }

        /// <summary>Picks a diamond: it, and the clip or effect bar it sits on, become the selection (the shared pick).</summary>
        private void PickDiamond(KeyframeRef keyframe)
        {
            if (keyframe.Host.EffectId is null)
            {
                _selectedClipId = keyframe.Host.OwnerId;
                _selectedTrackId = null;
            }
            if (_editorVm is not null)
                _editorVm.PickKeyframe(keyframe, seek: false);
            else if (Editor is not null && keyframe.Host.EffectId is null)
                Editor.Selection.SelectKeyframe(new SelectionKey(SelectionGroup.Clip, keyframe.Host.OwnerId), keyframe);
        }

        /// <summary>A left press on a diamond: picks it, jumps there, and gets ready to drag it. True when one was there.</summary>
        private bool HandleDiamondPress(PointerPressedEventArgs e, Point pos)
        {
            if (Editor is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || HitTestDiamond(pos) is not { } hit)
                return false;
            var keyframe = hit.Keyframe;
            if (Editor.FindKeyframeHost(keyframe.Host) is not { } host)
                return false;

            // double-click a speed point: back to 1x
            if (hit.ValueDrag && e.ClickCount >= 2)
            {
                Editor.SetKeyframeValue(keyframe.Host, AutomationKeys.Speed, keyframe.TimeSec, 1);
                InvalidateVisual();
                return true;
            }

            PickDiamond(keyframe);
            SetPlayhead(Editor.SnapTime(host.OriginSec + keyframe.TimeSec));
            var copy = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            _diamondDrag = new DiamondDrag(keyframe, keyframe.TimeSec, $"key-{++_diamondGestureSeq}", hit.ValueDrag && !copy, copy, pos.Y);
            e.Pointer.Capture(this);
            InvalidateVisual();
            return true;
        }

        /// <summary>Moves (or, for a copy, aims) the held diamond. True while a diamond is held.</summary>
        private bool UpdateDiamondDrag(Point pos)
        {
            if (_diamondDrag is not { } drag || Editor is null)
                return false;
            if (Editor.FindKeyframeHost(drag.Picked.Host) is not { } host)
            {
                _diamondDrag = null;
                return true;
            }

            var to = Math.Clamp(Snap(XToTime(pos.X), includePlayhead: false) - host.OriginSec, 0, host.LimitSec);
            var current = drag.CurrentSec;
            var taken = host.Times.Any(t => Math.Abs(t - to) < 1e-3 && (drag.Copy || Math.Abs(t - current) > 1e-6));
            if (!taken && Math.Abs(to - current) > 1e-6)
            {
                if (!drag.Copy)
                {
                    Editor.MoveKeyframes(drag.Picked with { TimeSec = current }, to, drag.Gesture);
                    Editor.Selection.MoveKeyframe(to);
                    if (drag.Picked.Host.EffectId is not null)
                        _editorVm?.Properties.SelectKeyframe(to);
                }
                current = to;
            }

            // a speed point also follows the pointer up and down
            if (drag.ValueDrag && Editor.FindClipForView(drag.Picked.Host.OwnerId) is { } clip && SpeedBand(clip) is { } band)
            {
                var speed = YToSpeed(band, pos.Y);
                if (Math.Abs(Math.Log(speed)) < 0.06)
                    speed = 1;   // a little magnet at 1x
                Editor.SetKeyframeValue(drag.Picked.Host, AutomationKeys.Speed, current, speed, drag.Gesture);
            }

            _diamondDrag = drag with { CurrentSec = current, Moved = drag.Moved || Math.Abs(current - drag.Picked.TimeSec) > 1e-6 || drag.ValueDrag };
            Cursor = new Cursor(drag.Copy ? StandardCursorType.DragCopy : drag.ValueDrag ? StandardCursorType.SizeAll : StandardCursorType.SizeWestEast);
            InvalidateVisual();
            return true;
        }

        private bool EndDiamondDrag(PointerReleasedEventArgs e)
        {
            if (_diamondDrag is not { } drag)
                return false;
            _diamondDrag = null;
            if (e.Pointer.Captured == this)
                e.Pointer.Capture(null);
            ClearSnapGuide();
            Cursor = Cursor.Default;
            if (drag.Copy && drag.Moved && Editor is not null)
            {
                Editor.DuplicateKeyframes(drag.Picked, drag.CurrentSec);
                PickDiamond(drag.Picked with { TimeSec = drag.CurrentSec });
            }
            // follow it, so the inspector shows its values (and its diamonds light)
            if (drag.Moved && Editor?.FindKeyframeHost(drag.Picked.Host) is { } host)
                SetPlayhead(host.OriginSec + drag.CurrentSec);
            _editorVm?.Properties.RefreshAutomation();
            InvalidateVisual();
            return true;
        }

        /// <summary>While Ctrl-dragging a copy: where the copy will land, at the height it was picked up.</summary>
        private void DrawDiamondCopyPreview(DrawingContext context)
        {
            if (_diamondDrag is not { Copy: true, Moved: true } drag || Editor?.FindKeyframeHost(drag.Picked.Host) is not { } host)
                return;
            var x = TrackHeaderWidth + Viewport.TimeToX(host.OriginSec + drag.CurrentSec);
            context.DrawEllipse(DiamondGhostBrush, PenOf(Brushes.White, 1), new Point(x, drag.PressY), 5, 5);
        }

        /// <summary>Hovering a diamond: highlight it and say what it does. True when over one.</summary>
        private bool UpdateDiamondHover(Point pos)
        {
            var hit = HitTestDiamond(pos);
            if (hit?.Keyframe != _hoverDiamond)
            {
                _hoverDiamond = hit?.Keyframe;
                InvalidateVisual();
            }
            if (hit is not { } over)
                return false;
            Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(this, over.ValueDrag
                ? "Keyframe · drag to move (up for faster, down for slower) · Ctrl-drag to copy · double-click for 1× · right-click for more"
                : "Keyframe · drag to move · Ctrl-drag to copy · right-click to change how it moves or delete it");
            return true;
        }

        /// <summary>Right-click on a diamond, of any kind: the one keyframe menu. True when one was there.</summary>
        private bool TryOpenDiamondMenu(Point pos)
        {
            if (Editor is null || _editorVm is null || HitTestDiamond(pos) is not { } hit)
                return false;
            PickDiamond(hit.Keyframe);
            InvalidateVisual();
            ShowMenu(Fig.App.Controls.KeyframeMenu.Build(_editorVm, hit.Keyframe, InvalidateVisual));
            return true;
        }
    }
}
