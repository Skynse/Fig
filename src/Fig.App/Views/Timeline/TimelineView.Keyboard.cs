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
    /// Keys pressed over the timeline.
    /// </summary>
    public partial class TimelineView
    {
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (Editor is null)
                return;

            if (e.Key == Key.Escape && _copyDrag is not null)
            {
                CancelCopyDrag();
                e.Handled = true;
                return;
            }

            // M = add a marker at the playhead (selected clip / active track / timeline)
            if (e.Key == Key.M && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                _editorVm?.AddMarkerAtPlayheadCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.A && (e.KeyModifiers == KeyModifiers.Control || e.KeyModifiers == KeyModifiers.Meta))
            {
                // with only markers selected, Ctrl+A means every marker; otherwise everything on the timeline
                if (Editor.Selection.Items is { Count: > 0 } items && items.All(k => k.Group == SelectionGroup.Marker))
                    Editor.Selection.SelectMarkers(Editor.AllMarkers().Select(marker => marker.Marker.Id));
                else
                    SelectAll();
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                // Escape puts the blade away first
                if (_editorVm is { BladeTool: true } bladeVm)
                {
                    bladeVm.BladeTool = false;
                    _bladeHover = null;
                    Cursor = Cursor.Default;
                    InvalidateVisual();
                    e.Handled = true;
                    return;
                }
                _selectedGap = null;
                ClearSelection();
                e.Handled = true;
                return;
            }

            // N = turn magnetic snapping on/off
            if (e.Key == Key.N && e.KeyModifiers == KeyModifiers.None)
            {
                _editorVm?.ToggleMagneticSnapCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Delete || e.Key == Key.Back)
            {
                // a selected gap closes
                if (DeleteSelectedGap())
                {
                    e.Handled = true;
                    return;
                }
                // whatever is selected knows how to be deleted
                if (_editorVm?.DeleteSelection() == true || (_editorVm is null && Editor.DeleteSelection() is not null))
                {
                    InvalidateMeasure();
                    InvalidateVisual();
                    e.Handled = true;
                }
            }
        }
    }
}
