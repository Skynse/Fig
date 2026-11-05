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
    /// Trimming a clip live while its edge is dragged.
    /// </summary>
    public partial class TimelineView
    {
        private void ApplyLiveResizeStart(double deltaTime)
        {
            if (_dragClip is null)
                return;

            // the moving edge is the clip start; snap it to other clips' boundaries
            var rawStart = Math.Max(0, _dragOriginals[_dragClip.Id] + deltaTime);
            var snappedStart = Snap(rawStart, _dragClip.Id, excludeClips: _rollSnapExclude);
            var snapDelta = snappedStart - _dragOriginals[_dragClip.Id];

            foreach (var c in _dragMembers)
            {
                var orig = _dragOriginals[c.Id];
                var origEnd = orig + _dragOriginalDurs[c.Id];
                var memberStart = Math.Max(0, orig + snapDelta);
                var memberDur = Math.Max(0.1, origEnd - memberStart);

                if (c.HasSourceRange && _dragCurves.TryGetValue(c.Id, out var curve))
                {
                    // the curve stays where it is on the timeline; the in point is wherever it was at the new start
                    var before = new VideoClip { SrcInSec = _dragSrcIns[c.Id], SrcOutSec = _dragSrcOuts[c.Id] };
                    before.Keyframes[AutomationKeys.Speed] = curve;
                    var delta = Math.Max(memberStart - orig, ClipTime.LocalAt(before, 0));   // not before the file starts
                    delta = Math.Min(delta, _dragOriginalDurs[c.Id] - 0.1);
                    c.StartSec = orig + delta;
                    c.DurSec = origEnd - c.StartSec;
                    ClipTime.TrimStart(c, curve, _dragSrcIns[c.Id], delta);
                    ClipTime.SyncSourceOut(c);
                    continue;
                }

                // a caption's words stay where they are said
                ClipTime.MoveStartEdge(c, memberStart);
                c.DurSec = memberDur;

                if (c.HasSourceRange)
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
            var snappedEnd = Snap(rawEnd, _dragClip.Id, excludeClips: _rollSnapExclude);
            var snapDelta = snappedEnd - (_dragOriginals[_dragClip.Id] + _dragOriginalDurs[_dragClip.Id]);

            foreach (var c in _dragMembers)
            {
                var orig = _dragOriginals[c.Id];
                var origEnd = orig + _dragOriginalDurs[c.Id];

                // clamp against the source media's actual length
                var maxSrcOut = SourceDurationSec(c);
                var srcIn = _dragSrcIns[c.Id];

                var speed = c.Speed;
                // a clip without a source range (text, a still) can be made any length
                var hasCurve = _dragCurves.ContainsKey(c.Id);
                var maxDur = !c.HasSourceRange ? double.MaxValue
                    : maxSrcOut is double max && max > srcIn
                        ? hasCurve ? ClipTime.LocalAt(c, max) : (max - srcIn) / speed
                        : _dragOriginalDurs[c.Id];

                var newEnd = Math.Max(orig + 0.1, origEnd + snapDelta);
                var newDur = Math.Max(0.1, newEnd - orig);
                if (maxDur > 0.1)
                    newDur = Math.Min(newDur, maxDur);

                c.DurSec = newDur;

                if (c.HasSourceRange)
                {
                    var newSrcOut = hasCurve ? ClipTime.SourceAt(c, newDur) : srcIn + newDur * speed;
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
            if (!clip.HasSourceRange || clip.MediaId is not { } sourceId || !MediaById.TryGetValue(sourceId, out var asset))
                return null;
            return asset.DurationSec > 0 ? asset.DurationSec : null;
        }
    }
}
