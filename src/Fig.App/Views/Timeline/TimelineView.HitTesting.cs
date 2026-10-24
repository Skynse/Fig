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
    /// <summary>
    /// What is under the pointer: tracks, clips, their edges and fade handles, keyframes, transitions.
    /// </summary>
    public partial class TimelineView
    {
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
                var top = TrackTop(i);
                if (pos.Y < top || pos.Y >= TrackBottom(i))
                    continue;
                // a clip's own diamonds sit along the top edge of its body (effect keyframes live on the effect bars);
                // a text clip has no name strip, so its body starts at the top of the clip (as it is drawn)
                foreach (var clip in track.Clips)
                {
                    var bodyTop = top + TimelineGeometry.ClipInset + (clip is TextClip ? 0 : LabelHeight(track));
                    if (pos.Y >= bodyTop - 2 && pos.Y <= bodyTop + 14)
                        foreach (var (key, trackKf) in clip.Keyframes)
                            foreach (var kf in key == AutomationKeys.Speed ? [] : trackKf)
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
                var top = TrackTop(i);
                if (pos.Y < top || pos.Y >= TrackBottom(i))
                    continue;
                // the badge sits at the clip body's vertical center; only grab within that band so
                // markers / keyframes near the cut stay clickable
                var centerY = top + TimelineGeometry.ClipInset + LabelHeight(track) + BodyHeight(track) / 2;
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

        /// <summary>
        /// What a press at <paramref name="pos"/> on <paramref name="clip"/> grabs. The outer TrimEdgePx of each end
        /// trims, at any height. A picture's fades live in its top half only (the next FadeZonePx inward, or an
        /// existing fade's tip), so the lower half of an edge always trims and a trim never catches a fade by
        /// accident. A video's sound strip has fades of its own (<paramref name="of"/> is then Sound), grabbed the
        /// same way anywhere on the strip.
        /// </summary>
        private EdgeKind EdgeAt(Clip clip, Track track, Point pos, out FadeOf of)
        {
            of = FadeOf.Picture;
            var px = Viewport.PixelsPerSecond;
            var leftX = TrackHeaderWidth + Viewport.TimeToX(clip.StartSec);
            var rightX = leftX + clip.DurSec * px;
            if (Math.Abs(pos.X - leftX) <= TrimEdgePx)
                return EdgeKind.Left;
            if (Math.Abs(pos.X - rightX) <= TrimEdgePx)
                return EdgeKind.Right;

            var top = TrackTop(Editor!.Document.Tracks.IndexOf(track)) + TimelineGeometry.ClipInset;
            var bottom = top + ClipTotal(track);
            if (clip is VideoClip { PlaysAudio: true } && pos.Y >= bottom - SoundStrip)
                of = FadeOf.Sound;
            else if (pos.Y > top + (ClipTotal(track) - SoundStripHeight(clip)) / 2)
                return EdgeKind.None;
            var (fadeIn, fadeOut) = ClipFade.Get(clip, of);

            // an existing fade's tip, wherever it is
            if (fadeIn > 1e-6 && Math.Abs(pos.X - (leftX + fadeIn * px)) <= FadeZonePx * 0.5)
                return EdgeKind.FadeIn;
            if (fadeOut > 1e-6 && Math.Abs(pos.X - (rightX - fadeOut * px)) <= FadeZonePx * 0.5)
                return EdgeKind.FadeOut;

            // too narrow for fade zones: trim only
            if (clip.DurSec * px <= (TrimEdgePx + FadeZonePx) * 2 + 4)
                return EdgeKind.None;
            if (pos.X > leftX + TrimEdgePx && pos.X <= leftX + TrimEdgePx + FadeZonePx)
                return EdgeKind.FadeIn;
            if (pos.X < rightX - TrimEdgePx && pos.X >= rightX - TrimEdgePx - FadeZonePx)
                return EdgeKind.FadeOut;
            return EdgeKind.None;
        }

        /// <summary>Height of the sound strip at the bottom of a clip (0 when it has none).</summary>
        private double SoundStripHeight(Clip clip)
            => clip is VideoClip { PlaysAudio: true } ? SoundStrip : 0;

        private Track? HitTestTrack(double y)
        {
            if (Editor is null)
                return null;
            // iterate by list position so hit-testing matches rendering, regardless of stale Index values
            for (var i = 0; i < Editor.Document.Tracks.Count; i++)
            {
                var track = Editor.Document.Tracks[i];
                if (y >= TrackTop(i) && y < TrackBottom(i))
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
        /// The clip actually drawn under a point. Anywhere else in a lane (above or below the clip, in the gaps)
        /// is empty space: a clip does not own the whole column of its lane.
        /// </summary>
        private Clip? ClipAt(Point pos, out Track? track)
        {
            track = HitTestTrack(pos.Y);
            if (Editor is null || track is null || pos.X <= TrackHeaderWidth)
                return null;
            var clip = HitTestClip(track, XToTime(pos.X), pos.X);
            if (clip is null)
                return null;
            var top = TrackTop(Editor.Document.Tracks.IndexOf(track)) + TimelineGeometry.ClipInset;
            return pos.Y >= top && pos.Y <= top + ClipTotal(track) ? clip : null;
        }

        private enum RangeGrab { None, Start, End, Body }

        /// <summary>True when a clip has a neighbor touching it on the left/right in the same track.</summary>
        /// <summary>Where a track's clips start and end, so finding a clip's neighbours doesn't scan the whole track.</summary>
        private sealed class ClipEdges
        {
            private readonly HashSet<long> _starts = new();
            private readonly HashSet<long> _ends = new();

            // times are frame-snapped; a tenth of a millisecond tells cuts apart and lets touching edges meet
            private static long Key(double sec) => (long)Math.Round(sec * 1e4);

            public static ClipEdges Of(Track track)
            {
                var edges = new ClipEdges();
                foreach (var clip in track.Clips)
                {
                    edges._starts.Add(Key(clip.StartSec));
                    edges._ends.Add(Key(clip.StartSec + clip.DurSec));
                }
                return edges;
            }

            /// <summary>Whether another clip ends where this one starts, and starts where it ends.</summary>
            public (bool HasPrev, bool HasNext) Adjacent(Clip clip)
                => (_ends.Contains(Key(clip.StartSec)), _starts.Contains(Key(clip.StartSec + clip.DurSec)));
        }

        /// <summary>Returns whether the pointer is over a clip's trim or fade edge.</summary>
        private EdgeKind HoverClipEdge(Point pos, out FadeOf of)
        {
            of = FadeOf.Picture;
            return ClipAt(pos, out var track) is { } clip && track is not null ? EdgeAt(clip, track, pos, out of) : EdgeKind.None;
        }
    }
}
