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
    /// Drawing the timeline: lanes, clips, transitions, markers, the ruler and the playhead.
    /// </summary>
    public partial class TimelineView
    {
        // ---- rendering ----

        public override void Render(DrawingContext context)
        {
            context.DrawRectangle(SurfaceBackground, null, new Rect(0, 0, Bounds.Width, Bounds.Height));

            if (Editor is null)
                return;

            using var laneHeights = RememberLaneHeights();
            var px = Viewport.PixelsPerSecond;
            var scroll = Viewport.ScrollTime;

            // clip everything below the ruler + right of the header so clips never
            // draw over the header column when zoomed/scrolled
            var clipArea = new Rect(TrackHeaderWidth, RulerHeight,
                Math.Max(0, Bounds.Width - TrackHeaderWidth), Math.Max(0, Bounds.Height - RulerHeight));

            for (var i = 0; i < Editor.Document.Tracks.Count; i++)
            {
                var track = Editor.Document.Tracks[i];
                var top = TrackTop(i);
                var height = TrackHeight(track);

                var isTrackSelected = track.Id == _selectedTrackId;
                var rowRect = new Rect(0, top, Bounds.Width, height);
                var laneBrush = i % 2 == 0 ? TrackLaneBrush : TrackLaneAltBrush;
                context.DrawRectangle(laneBrush, null, rowRect);
                context.DrawRectangle(TrackHeaderBrush, null, new Rect(0, top, TrackHeaderWidth, height));
                if (isTrackSelected)
                {
                    context.DrawRectangle(SelectedLaneTintBrush, null, rowRect);
                    context.DrawRectangle(SelectionBrush, null, new Rect(0, top, TrackAccentBarWidth, height));
                }
                context.DrawLine(PenOf(BorderBrush, 1), new Point(TrackHeaderWidth, top), new Point(TrackHeaderWidth, top + height));
                context.DrawLine(PenOf(BorderBrush, 1), new Point(0, top + height), new Point(Bounds.Width, top + height));
                DrawTrackGrid(context, top, height);

                var trackLabel = track.Name ?? track.Kind.ToString();
                var trackText = TextOf(trackLabel, 11, TrackHeaderTextBrush);
                context.DrawText(trackText, new Point(HeaderLabelX(), top + (height - trackText.Height) / 2));

                DrawHeaderToggle(context, track, HeaderButtonRect(track, HeaderToggleColumn));
                if (track.Kind == TrackKind.Video)
                    DrawHeaderIcon(context, track.Muted ? "volume-x" : "volume", HeaderButtonRect(track, HeaderMuteColumn), track.Muted);
                DrawRackToggle(context, track);
                var deleteHovered = _hoverHeaderButton is (string tid, int col)
                    && tid == track.Id && col == DeleteColumn(track);
                if (isTrackSelected || deleteHovered)
                    DrawHeaderDelete(context, track, HeaderButtonRect(track, DeleteColumn(track)));

                // clip area, offset by header + viewport scroll
                using (context.PushClip(clipArea))
                {
                    var trackTransitions = Editor.EnumerateTransitions(track).ToList();
                    var edges = ClipEdges.Of(track);

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
                            ClipKind.Image => (ImageClipBrush, ImageBorder),
                            _ => (VideoBrush, VideoBorder),
                        };

                        // clip widget = label strip (name) on top + body (filmstrip/waveform).
                        // top inset leaves room for marker tabs inside the lane.
                        var clipTop = top + TimelineGeometry.ClipInset;
                        var totalHeight = ClipTotal(track);
                        // a text clip has no name strip: its words are written inside the bar instead
                        var labelHeight = clip is TextClip ? 0 : LabelHeight(track);
                        var bodyHeight = totalHeight - labelHeight;

                        // zoomed out, a clip a few pixels wide shows nothing but its colour: no shadow, corners,
                        // picture, waveform or keyframes, which on a long edit are tens of thousands of draws
                        if (w < ClipDetailWidth)
                        {
                            var sliverSelected = _selectedClipId is not null && Editor.Selection.IsSelected(clip.Id);
                            context.DrawRectangle(sliverSelected ? ClipSelectionBrush : fill, null,
                                new Rect(x, clipTop, Math.Max(1, w - SplitGap), totalHeight));
                            continue;
                        }

                        // every clip is rounded; clips that butt together (split halves) leave a hairline
                        // between them so each reads as its own rounded bar
                        var (hasPrev, hasNext) = edges.Adjacent(clip);
                        if (hasPrev)
                        {
                            x += SplitGap;
                            w -= SplitGap;
                        }
                        if (hasNext)
                            w -= SplitGap;
                        w = Math.Max(1, w);
                        var radius = Math.Min(ClipCornerRadius, w / 2);
                        var (topLeft, topRight, bottomLeft, bottomRight) = (radius, radius, radius, radius);
                        var corner = new CornerRadius(radius);

                        var widgetRect = new Rect(x, clipTop, w, totalHeight);

                        context.DrawRectangle(ClipShadow, null, new RoundedRect(widgetRect.Translate(new Vector(0, 1)), corner));

                        var isClipSelected = _selectedClipId is not null
                            && Editor.Selection.IsSelected(clip.Id);
                        var isEffectDropTarget = _dropEffectClipId == clip.Id;
                        // the body's own edge; a selected clip gets one outline round all of it, drawn last
                        var outline = isEffectDropTarget ? SelectionBrush : border;
                        var outlineWidth = isEffectDropTarget ? 3 : 1;

                        // --- name strip above the clip body ---
                        if (labelHeight > 0)
                        {
                            var labelRect = new Rect(x, clipTop, w, labelHeight);
                            var labelBrush = fill;
                            context.DrawRectangle(labelBrush, null, new RoundedRect(labelRect, new CornerRadius(topLeft, topRight, 0, 0)));

                            using (context.PushClip(labelRect))
                            {
                                var clipName = ClipName(clip);
                                if (clipName is not null && w >= MinLabelWidth)
                                {
                                    var labelText = TextOf(clipName, 10, Brushes.White);
                                    context.DrawText(labelText, new Point(x + 4 + (w >= 54 && BuildRack(clip).Count > 0 ? 26 : 0),
                                        labelRect.Y + (labelHeight - labelText.Height) / 2));
                                }
                            }
                        }

                        // --- clip body ---
                        var rect = new Rect(x, clipTop + labelHeight, w, bodyHeight);
                        // under a name strip the body's top is square, so the two read as one rounded bar
                        var bodyRounded = new RoundedRect(rect, labelHeight > 0
                            ? new CornerRadius(0, 0, bottomRight, bottomLeft)
                            : corner);

                        // a video playing its own sound shows it as a strip under the picture
                        var soundHeight = clip is VideoClip { PlaysAudio: true } ? SoundStrip : 0;

                        // draw filmstrip as video clip background
                        if (clip is VideoClip vc && MediaById is not null
                            && MediaById.TryGetValue(vc.SourceId, out var asset)
                            && asset?.Filmstrip is string stripPath
                            && GetBitmap(stripPath) is Bitmap strip)
                        {
                            DrawFilmstrip(context, new Rect(rect.X, rect.Y, rect.Width, rect.Height - soundHeight), vc, asset, strip);
                            context.DrawRectangle(Brushes.Transparent, PenOf(outline, outlineWidth), bodyRounded);
                        }
                        // draw waveform as audio clip background
                        else if (clip is AudioClip ac && MediaById is not null
                            && MediaById.TryGetValue(ac.SourceId, out var audioAsset)
                            && audioAsset is not null)
                        {
                            DrawAudioWaveform(context, rect, ac, audioAsset);
                            context.DrawRectangle(Brushes.Transparent, PenOf(outline, outlineWidth), bodyRounded);
                        }
                        // a still repeats its thumbnail along the bar
                        else if (clip is ImageClip image && MediaById is not null
                            && MediaById.TryGetValue(image.SourceId, out var imageAsset)
                            && imageAsset?.Thumbnail is string thumbPath
                            && GetBitmap(thumbPath) is Bitmap thumb && thumb.PixelSize.Height > 0)
                        {
                            context.DrawRectangle(fill, null, bodyRounded);
                            using (context.PushClip(bodyRounded))
                            {
                                var tileW = rect.Height * thumb.PixelSize.Width / thumb.PixelSize.Height;
                                for (var tx = rect.X; tx < rect.Right && tileW > 1; tx += tileW)
                                    context.DrawImage(thumb, new Rect(0, 0, thumb.PixelSize.Width, thumb.PixelSize.Height),
                                        new Rect(tx, rect.Y, tileW, rect.Height));
                            }
                            context.DrawRectangle(Brushes.Transparent, PenOf(outline, outlineWidth), bodyRounded);
                        }
                        else
                        {
                            context.DrawRectangle(fill, PenOf(outline, outlineWidth), bodyRounded);
                        }

                        if (clip is TextClip textClip)
                            DrawTextClipWords(context, rect, textClip);

                        if (soundHeight > 0 && clip is VideoClip sounding && MediaById is not null
                            && MediaById.TryGetValue(sounding.SourceId, out var soundAsset))
                        {
                            var soundRect = new Rect(rect.X, rect.Bottom - soundHeight, rect.Width, soundHeight);
                            using (context.PushClip(bodyRounded))
                                DrawAudioWaveform(context, soundRect, sounding, soundAsset);
                            context.DrawRectangle(Brushes.Transparent, PenOf(outline, outlineWidth), bodyRounded);
                        }

                        if (isEffectDropTarget)
                        {
                            context.DrawRectangle(
                                new SolidColorBrush(Color.FromArgb(50, 0x4d, 0xa3, 0xff)),
                                null, new RoundedRect(widgetRect, corner));
                        }

                        // the picture's fades over the picture, a video's own sound's over its strip
                        var pictureRect = new Rect(rect.X, rect.Y, rect.Width, rect.Height - soundHeight);
                        var soundStripRect = new Rect(rect.X, rect.Bottom - soundHeight, rect.Width, soundHeight);
                        DrawFadeRamps(context, pictureRect, clip.FadeInSec, clip.FadeOutSec);
                        if (clip is VideoClip { PlaysAudio: true } faded)
                            DrawFadeRamps(context, soundStripRect, faded.AudioFadeInSec, faded.AudioFadeOutSec);
                        DrawSpeedCurve(context, clip, pictureRect);

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


                        DrawKeyframeDiamonds(context, clip, rect, px);
                        DrawRack(context, clip, x, w, clipTop, track);

                        if (!clip.Enabled)
                        {
                            context.DrawRectangle(
                                new SolidColorBrush(Color.FromArgb(120, 0x10, 0x10, 0x12)),
                                null, new RoundedRect(widgetRect, corner));
                        }

                        // selected: the whole clip, name strip and all, inside its rounded edge
                        if (isClipSelected)
                            context.DrawRectangle(null, PenOf(ClipSelectionBrush, 2),
                                new RoundedRect(widgetRect.Deflate(1), new CornerRadius(Math.Max(0, radius - 1))));

                        // resize-handle indicator on the edge under the cursor (or both edges when selected)
                        var hoverLeft = _hoverEdge == EdgeKind.Left;
                        var hoverRight = _hoverEdge == EdgeKind.Right;
                        var hoverFadeIn = _hoverEdge == EdgeKind.FadeIn;
                        var hoverFadeOut = _hoverEdge == EdgeKind.FadeOut;
                        if (isClipSelected || hoverLeft)
                            DrawResizeHandle(context, x, rect.Y, rect.Height);
                        if (isClipSelected || hoverRight)
                            DrawResizeHandle(context, x + w, rect.Y, rect.Height);
                        foreach (var of in soundHeight > 0 ? new[] { FadeOf.Picture, FadeOf.Sound } : new[] { FadeOf.Picture })
                        {
                            var (fadeIn, fadeOut) = ClipFade.Get(clip, of);
                            var band = of == FadeOf.Sound ? soundStripRect : pictureRect;
                            var hovered = _hoverFadeOf == of;
                            if ((hoverFadeIn && hovered) || (isClipSelected && fadeIn > 1e-6))
                                DrawResizeHandle(context, x + Math.Min(fadeIn, clip.DurSec) * px, band.Y, band.Height);
                            if ((hoverFadeOut && hovered) || (isClipSelected && fadeOut > 1e-6))
                                DrawResizeHandle(context, x + w - Math.Min(fadeOut, clip.DurSec) * px, band.Y, band.Height);
                        }
                    }   // foreach clip
                    DrawRack(context, track, TrackHeaderWidth + Viewport.TimeToX(0),
                        track.DurSec * Viewport.PixelsPerSecond, top + TimelineGeometry.ClipInset, track);
                }   // PushClip(clipArea)
            }   // for tracks

            DrawMarkersOverTracks(context, clipArea, px);

            // add-track button at bottom of header column
            if (Editor.Document.Tracks.Count > 0)
            {
                var addRect = AddTrackButtonRect();
                DrawHeaderAdd(context, addRect);
            }

            // drop preview: highlight target track + ghost clip rect
            if (_dropPreviewTrack is not null && _dropPreviewTime >= 0 && _dropPreviewAsset is not null)
            {
                var trackIndex = Editor.Document.Tracks.IndexOf(_dropPreviewTrack);
                var trackTop = TrackTop(Math.Max(0, trackIndex));
                var trackHeight = TrackHeight(_dropPreviewTrack);
                context.DrawRectangle(new SolidColorBrush(Color.Parse("#2244aa44")), null,
                    new Rect(0, trackTop, Bounds.Width, trackHeight));

                var ghostX = TrackHeaderWidth + Viewport.TimeToX(_dropPreviewTime);
                var ghostW = _dropPreviewAsset.DurationSec * px;
                var ghostHeight = Math.Min(ClipTotal(_dropPreviewTrack), trackHeight - 8);
                var ghostRect = new Rect(ghostX, trackTop + TimelineGeometry.ClipInset, ghostW, ghostHeight);
                context.DrawRectangle(new SolidColorBrush(Color.Parse("#88ffffff")), null,
                    new RoundedRect(ghostRect, ClipCornerRadius));
                context.DrawRectangle(Brushes.Transparent, PenOf(SelectionBrush, 2),
                    new RoundedRect(ghostRect, ClipCornerRadius));
            }

            if (_dropPreviewTrack is not null && _dropPreviewAsset is null && _dropPreviewTime >= 0)
            {
                var trackTop = TrackTop(Editor.Document.Tracks.IndexOf(_dropPreviewTrack));
                var height = TrackHeight(_dropPreviewTrack);
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(35, 199, 154, 61)), null,
                    new Rect(TrackHeaderWidth, trackTop, Math.Max(0, Bounds.Width - TrackHeaderWidth), height));
                var ghostX = TrackHeaderWidth + Viewport.TimeToX(_dropPreviewTime);
                var ghostW = Math.Max(4, _dropEffectDuration * px);
                var ghost = _dropLayer
                    ? new Rect(ghostX, trackTop + TimelineGeometry.ClipInset, ghostW,
                        Math.Min(ClipTotal(_dropPreviewTrack), height - 8))
                    : new Rect(ghostX, trackTop + TimelineGeometry.ClipInset + ClipTotal(_dropPreviewTrack) + 4,
                        ghostW, 24);
                context.DrawRectangle(_dropLayer ? TextClipBrush : new SolidColorBrush(Color.FromArgb(140, 199, 154, 61)),
                    PenOf(Brushes.White, 1), ghost, 3, 3);
            }

            // drop indicator line (media into empty space)
            if (_dropTimeSec >= 0 && _dropPreviewTrack is null)
            {
                var x = TrackHeaderWidth + Viewport.TimeToX(_dropTimeSec);
                context.DrawLine(PenOf(Brushes.OrangeRed, 2), new Point(x, RulerHeight), new Point(x, Bounds.Height));
            }

            // transition drop: highlight cut between abutting clips
            if (_dropTransitionCutSec >= 0 && _dropTransitionTrack is not null)
            {
                var listIndex = Editor.Document.Tracks.IndexOf(_dropTransitionTrack);
                if (listIndex >= 0)
                {
                    var trackTop = TrackTop(listIndex);
                    var x = TrackHeaderWidth + Viewport.TimeToX(_dropTransitionCutSec);
                    var marker = new SolidColorBrush(Color.FromArgb(230, 0xea, 0xb3, 0x08));
                    var cutTotal = ClipTotal(_dropTransitionTrack);
                    context.DrawLine(PenOf(marker, 3), new Point(x, trackTop + TimelineGeometry.ClipInset),
                        new Point(x, trackTop + TimelineGeometry.ClipInset + cutTotal));
                    var midY = trackTop + TimelineGeometry.ClipInset + cutTotal / 2;
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

            //DrawMarkerGuides(context);

            // the ruler and marker lane are pinned to the top of the pane and cover the tracks scrolling under them
            var pinned = StickyOffset;
            using (context.PushTransform(Matrix.CreateTranslation(0, pinned)))
            {
                DrawRuler(context, px, scroll);
                DrawTimelineMarkers(context);
            }

            // playhead
            DrawEmptyHint(context);
            DrawSnapGuide(context);
            DrawMarquee(context);
            DrawCopyPreview(context);
            DrawDiamondCopyPreview(context);
            DrawMoveGhost(context);
            DrawInsertMark(context);
            DrawSelectedGap(context);
            DrawBlade(context);
            DrawTrackDrop(context);

            var playheadX = TrackHeaderWidth + Viewport.TimeToX(PlayheadTimeSec);
            if (playheadX >= TrackHeaderWidth)
            {
                var playheadPen = PenOf(EditorTheme.PlayheadBrush, 1);
                // the line starts at the cap's lower tip, not behind it
                context.DrawLine(playheadPen, new Point(playheadX, pinned + MarkerLaneHeight + PlayheadCapHeight),
                    new Point(playheadX, Bounds.Height));
                var cap = new StreamGeometry();
                using (var gc = cap.Open())
                {
                    // the cap rides with the pinned ruler
                    gc.BeginFigure(new Point(playheadX - 5, pinned + MarkerLaneHeight), true);
                    gc.LineTo(new Point(playheadX + 5, pinned + MarkerLaneHeight));
                    gc.LineTo(new Point(playheadX, pinned + MarkerLaneHeight + PlayheadCapHeight));
                    gc.EndFigure(true);
                }
                context.DrawGeometry(EditorTheme.PlayheadBrush, null, cap);
            }
        }
    }
}
