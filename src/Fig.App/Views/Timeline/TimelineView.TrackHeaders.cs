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
using Fig.App.Services;
using Fig.App.ViewModels;
using Fig.Core.Input;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.App.Views
{
    /// <summary>The track header column: names, show/hide and mute toggles, delete and add-track buttons.</summary>
    public partial class TimelineView
    {
        private Rect HeaderButtonRect(Track track, int column)
        {
            // use the track's list position (matching render), not a possibly-stale Index
            var listIndex = Editor?.Document.Tracks.IndexOf(track) ?? track.Index;
            var top = TrackTop(listIndex);
            var center = top + TrackHeight(track) / 2;
            var x = HeaderPaddingLeft + column * (HeaderButtonSize + HeaderButtonGap);
            return new Rect(x, center - HeaderButtonSize / 2, HeaderButtonSize, HeaderButtonSize);
        }

        /// <summary>Where the name starts: after the toggles and the delete button (three columns: video has two toggles).</summary>
        private static double HeaderLabelX()
        {
            return HeaderPaddingLeft + 3 * (HeaderButtonSize + HeaderButtonGap) + 2;
        }

        private static string ToggleIconKey(Track track)
        {
            return track.Kind switch
            {
                TrackKind.Video => track.Visible ? "eye" : "eye-off",
                _ => track.Muted ? "volume-x" : "volume",
            };
        }

        private void DrawHeaderIcon(DrawingContext context, string key, Rect rect, bool dimmed)
        {
            var brush = dimmed ? HeaderIconDimBrush : TrackHeaderTextBrush;
            IconService.DrawStroked(context, key, rect.Inflate(-5), brush, 1.5);
        }

        private void DrawHeaderToggle(DrawingContext context, Track track, Rect rect)
        {
            var dimmed = track.Kind == TrackKind.Video ? !track.Visible : track.Muted;
            DrawHeaderIcon(context, ToggleIconKey(track), rect, dimmed);
        }

        private void DrawHeaderDelete(DrawingContext context, Track track, Rect rect)
        {
            var hovered = _hoverHeaderButton is (string tid, int col) && tid == track.Id && col == DeleteColumn(track);
            if (hovered)
                context.DrawRectangle(DeleteHoverBrush, null, new RoundedRect(rect, 3));
            DrawHeaderIcon(context, "trash", rect, false);
        }

        private void DrawHeaderAdd(DrawingContext context, Rect rect)
        {
            DrawHeaderIcon(context, "plus", rect, false);
        }

        private Rect AddTrackButtonRect()
        {
            if (Editor is null)
                return default;
            // under the last track, or right under the ruler on a timeline with none yet
            var count = Editor.Document.Tracks.Count;
            var y = (count == 0 ? TrackTop(0) : TrackBottom(count - 1)) + 4;
            return new Rect(8, y, HeaderButtonSize, HeaderButtonSize);
        }

        // header button hover: (track id, column) or null
        private (string TrackId, int Column)? _hoverHeaderButton;

        /// <summary>Returns the header button under the cursor (track id + column), or null.</summary>
        private (string TrackId, int Column)? HoverHeaderButton(Point pos)
        {
            if (Editor is null || pos.X > TrackHeaderWidth)
                return null;
            var track = HitTestTrack(pos.Y);
            if (track is null)
                return null;
            if (HeaderButtonRect(track, HeaderToggleColumn).Contains(pos))
                return (track.Id, HeaderToggleColumn);
            if (track.Kind == TrackKind.Video && HeaderButtonRect(track, HeaderMuteColumn).Contains(pos))
                return (track.Id, HeaderMuteColumn);
            if (HeaderButtonRect(track, DeleteColumn(track)).Contains(pos))
                return (track.Id, DeleteColumn(track));
            if (TrackHasRack(track) && RackToggleRect(track).Contains(pos))
                return (track.Id, HeaderRackColumn);
            return null;
        }
    }
}
