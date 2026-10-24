using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Fig.App.Services;
using Fig.App.ViewModels;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.App.Views;

public partial class EditorView : UserControl
{
    public static DataFormat<MediaAsset> MediaFormat => DragFormats.Media;
    public static DataFormat<EffectCatalogEntry> EffectFormat => DragFormats.Effect;
    public static DataFormat<EffectPreset> PresetFormat => DragFormats.Preset;
    public static DataFormat<TransitionCatalogEntry> TransitionFormat => DragFormats.Transition;

    private const double DragThresholdPx = 8.0;
    private bool _dragArmed;
    private Point _pressPoint;
    private PointerPressedEventArgs? _pressEvent;
    private EditorViewModel? _boundVm;
    private EditorTransportKeys? _transportKeys;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _transportKeys?.Dispose();
        _transportKeys = new EditorTransportKeys(this,
            () => (DataContext as EditorViewModel)?.TogglePlaybackCommand, () => Timeline.Focus());
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _transportKeys?.Dispose();
        _transportKeys = null;
        base.OnDetachedFromVisualTree(e);
    }

    public EditorView()
    {
        InitializeComponent();
        MarkerPanelRoot.AddHandler(PointerPressedEvent, MarkerPanel_PointerPressed, RoutingStrategies.Tunnel);
        MarkerPanelRoot.AddHandler(KeyDownEvent, MarkerPanel_KeyDown, RoutingStrategies.Tunnel);
        Focusable = true;
        DataContextChanged += OnDataContextChanged;
        Timeline.PlayheadChanged += OnTimelinePlayheadChanged;
        Timeline.Viewport.Changed += UpdateZoomText;
        ZoomSlider.ValueChanged += OnZoomSliderChanged;
        UpdateZoomText();
        PreviewHost.ContextMenu = BuildPreviewMenu();
    }

    private void MarkerPanel_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_boundVm is not { } vm || e.Source is not Visual visual)
            return;
        var button = visual.GetSelfAndVisualAncestors().OfType<Button>()
            .FirstOrDefault(control => control.Classes.Contains("marker-row"));
        if (button?.DataContext is not MarkerRowViewModel row)
            return;
        var point = e.GetCurrentPoint(MarkerPanelRoot);
        if (point.Properties.IsRightButtonPressed)
        {
            if (!vm.Editor.Selection.IsMarkerSelected(row.Id))
                vm.Editor.Selection.SelectMarker(row.Id);
            return;
        }
        if (!point.Properties.IsLeftButtonPressed)
            return;
        bool toggle = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool extend = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        vm.Editor.SelectMarker(row.Id, toggle, extend, vm.Markers.VisibleMarkerIds);
        if (!toggle && !extend)
            vm.SelectMarker(row.Id);
        MarkerPanelRoot.Focus();
        e.Handled = true;
    }

    private void MarkerPanel_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_boundVm is not { } vm || e.Source is not Visual visual
            || visual.GetSelfAndVisualAncestors().OfType<TextBox>().Any())
            return;
        if (e.Key == Key.A && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            vm.Markers.SelectAllCommand.Execute(null);
            e.Handled = true;
        }
        // Without a marker selection these keys belong to the editor (delete clips, clear clip selection).
        else if (e.Key is Key.Delete or Key.Back && vm.Editor.Selection.SelectedMarkerIds.Count > 0)
        {
            vm.DeleteSelectedMarkerCommand.Execute(null);
            MarkerPanelRoot.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && vm.Editor.Selection.SelectedMarkerIds.Count > 0)
        {
            vm.Editor.Selection.Clear();
            e.Handled = true;
        }
    }

    /// <summary>Right-click the preview: the things you reach for while looking at it.</summary>
    private ContextMenu BuildPreviewMenu()
    {
        var menu = new ContextMenu();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            if (_boundVm is not { } vm)
                return;

            MenuItem Make(string header, Action run, bool enabled = true)
            {
                var item = new MenuItem { Header = header, IsEnabled = enabled };
                item.Click += (_, _) => run();
                return item;
            }

            menu.Items.Add(Make(vm.IsPlaying ? "Pause" : "Play", () => vm.TogglePlaybackCommand.Execute(null)));
            menu.Items.Add(Make("Go to start", () => vm.JumpToStartCommand.Execute(null)));
            menu.Items.Add(Make("Go to end", () => vm.JumpToEndCommand.Execute(null)));
            menu.Items.Add(new Separator());
            if (vm.Properties.HasPreviewControls)
                menu.Items.Add(Make(vm.Properties.ShowPreviewControls ? "Hide controls on the preview" : "Show controls on the preview",
                    () => vm.Properties.ShowPreviewControls = !vm.Properties.ShowPreviewControls));
        };
        return menu;
    }

    private ExportJobsWindow? _jobsWindow;

    /// <summary>The Jobs button toggles a small window with every export's progress.</summary>
    private void OnEditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // the timeline's playhead follows the project playhead however it was moved (keyframe
        // buttons, marker jumps, …), not only when the audio engine reports back
        if (e.PropertyName == nameof(EditorViewModel.PlayheadTimeSec) && _boundVm is { } playing)
        {
            if (Math.Abs(Timeline.PlayheadTimeSec - playing.PlayheadTimeSec) > 1e-6)
                Timeline.SetPlayheadFromPlayback(playing.PlayheadTimeSec);
            return;
        }

        if (e.PropertyName != nameof(EditorViewModel.IsJobsOpen) || _boundVm is not { } vm)
            return;

        if (vm.IsJobsOpen && _jobsWindow is null && TopLevel.GetTopLevel(this) is Window owner)
        {
            _jobsWindow = new ExportJobsWindow { DataContext = vm };
            _jobsWindow.Closed += (_, _) =>
            {
                _jobsWindow = null;
                vm.IsJobsOpen = false;
            };
            _jobsWindow.Show(owner);
        }
        else if (!vm.IsJobsOpen && _jobsWindow is not null)
        {
            var window = _jobsWindow;
            _jobsWindow = null;
            window.Close();
        }
    }

    private bool _syncingZoomSlider;

    private void UpdateZoomText()
    {
        var pps = Timeline.Viewport.PixelsPerSecond;
        ZoomText.Text = $"{pps / TimelineViewport.DefaultPixelsPerSecond * 100:0}%";
        // the slider runs on a log scale, so each step zooms by the same factor
        _syncingZoomSlider = true;
        ZoomSlider.Value = ZoomSlider.Maximum * Math.Log(pps / TimelineViewport.MinPixelsPerSecond)
                           / Math.Log(TimelineViewport.MaxPixelsPerSecond / TimelineViewport.MinPixelsPerSecond);
        _syncingZoomSlider = false;
    }

    private void OnZoomSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingZoomSlider)
            return;
        var pps = TimelineViewport.MinPixelsPerSecond
                  * Math.Pow(TimelineViewport.MaxPixelsPerSecond / TimelineViewport.MinPixelsPerSecond, e.NewValue / ZoomSlider.Maximum);
        Timeline.ZoomToPixelsPerSecond(pps);
    }

    // ---- workspace layout -------------------------------------------------

    private ColumnDefinition LeftCol => Workspace.ColumnDefinitions[0];
    private ColumnDefinition CenterCol => Workspace.ColumnDefinitions[2];
    private ColumnDefinition RightCol => Workspace.ColumnDefinitions[4];
    private RowDefinition PreviewRow => Workspace.RowDefinitions[0];
    private RowDefinition TimelineRow => Workspace.RowDefinitions[2];

    private const double CollapsedRailWidth = 46;
    private const double CollapsedStripWidth = 44;

    /// <summary>Applies the saved arrangement: proportional columns/rows, collapsed panels.</summary>
    private void ApplyLayout()
    {
        if (_boundVm is not { } vm)
            return;
        var layout = vm.Layout;

        var left = layout.IsLeftVisible ? layout.LeftFraction : 0;
        var right = layout.IsRightVisible ? layout.RightFraction : 0;

        if (layout.IsLeftVisible)
        {
            LeftCol.MinWidth = 220;
            LeftCol.Width = new GridLength(layout.LeftFraction, GridUnitType.Star);
        }
        else
        {
            LeftCol.MinWidth = 0;
            LeftCol.Width = new GridLength(CollapsedRailWidth);
        }

        if (layout.IsRightVisible)
        {
            RightCol.MinWidth = 260;
            RightCol.Width = new GridLength(layout.RightFraction, GridUnitType.Star);
        }
        else
        {
            RightCol.MinWidth = 0;
            RightCol.Width = new GridLength(CollapsedStripWidth);
        }

        CenterCol.Width = new GridLength(Math.Max(0.2, 1 - left - right), GridUnitType.Star);
        PreviewRow.Height = new GridLength(1 - layout.TimelineFraction, GridUnitType.Star);
        TimelineRow.Height = new GridLength(layout.TimelineFraction, GridUnitType.Star);
    }

    /// <summary>After a splitter drag, remember the new proportions.</summary>
    private void Splitter_DragCompleted(object? sender, Avalonia.Input.VectorEventArgs e)
    {
        if (_boundVm is not { } vm)
            return;
        var layout = vm.Layout;
        var width = LeftCol.ActualWidth + CenterCol.ActualWidth + RightCol.ActualWidth;
        var height = PreviewRow.ActualHeight + TimelineRow.ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        layout.SetFractions(
            layout.IsLeftVisible ? LeftCol.ActualWidth / width : null,
            layout.IsRightVisible ? RightCol.ActualWidth / width : null,
            TimelineRow.ActualHeight / height);
    }

    private static readonly FilePickerFileType GraphFileType = new("Fig effect")
    {
        Patterns = new[] { "*.figgraph.json", "*.json" },
    };

    private async void ImportGraphEffect_Click(object? sender, RoutedEventArgs e)
    {
        if (_boundVm is null || TopLevel.GetTopLevel(this) is not { } top)
            return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import effect",
            AllowMultiple = false,
            FileTypeFilter = new[] { GraphFileType },
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            return;
        try
        {
            var graph = _boundVm.EffectGraphs.Import(path);
            _boundVm.Notify?.Invoke($"Imported \"{graph.Name}\"");
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.Text.Json.JsonException or System.IO.InvalidDataException)
        {
            _boundVm.Notify?.Invoke("That file is not a valid effect.");
        }
    }

    private async void ExportGraphEffect_Click(object? sender, RoutedEventArgs e)
    {
        if (_boundVm is null || (sender as Control)?.DataContext is not EffectCatalogEntry { IsCustom: true } entry
            || TopLevel.GetTopLevel(this) is not { } top)
            return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export effect",
            SuggestedFileName = entry.DisplayName + ".figgraph.json",
            FileTypeChoices = new[] { GraphFileType },
        });
        if (file?.TryGetLocalPath() is not { } path)
            return;
        try
        {
            _boundVm.EffectGraphs.Export(entry.TypeId[EffectGraph.TypeIdPrefix.Length..], path);
            _boundVm.Notify?.Invoke($"Exported \"{entry.DisplayName}\"");
        }
        catch (System.IO.IOException)
        {
            _boundVm.Notify?.Invoke("Could not write that file.");
        }
    }

    private async void BackToProjects_Click(object? sender, RoutedEventArgs e)
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window?.DataContext is ViewModels.AppViewModel app)
            await app.CloseProjectAsync(window);
    }

    /// <summary>Enter in a field applies the value (moves focus away, which commits the binding).</summary>
    private void CommitOnEnter_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        Focus();
        e.Handled = true;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_boundVm is not null)
        {
            _boundVm.PropertyChanged -= OnEditorPropertyChanged;
            _boundVm.Layout.Changed -= ApplyLayout;
            _boundVm.PlaybackAssigned -= OnPlaybackAssigned;
            _boundVm.TimelineZoomRequested -= OnTimelineZoomRequested;
            _boundVm = null;
            OnPlaybackAssigned(null);
        }

        if (DataContext is not EditorViewModel vm)
            return;

        _boundVm = vm;
        vm.PropertyChanged += OnEditorPropertyChanged;
        vm.Layout.Changed += ApplyLayout;
        ApplyLayout();
        vm.PlaybackAssigned += OnPlaybackAssigned;
        vm.TimelineZoomRequested += OnTimelineZoomRequested;
        if (vm.Playback is not null)
            WirePlayback(vm.Playback);
    }

    private PlaybackEngine? _wiredPlayback;

    private void OnPlaybackAssigned(PlaybackEngine? playback)
    {
        if (_wiredPlayback == playback)
            return;
        if (_wiredPlayback is not null)
            _wiredPlayback.PositionChanged -= OnPlaybackPositionChanged;
        _wiredPlayback = playback;
        if (playback is not null)
            playback.PositionChanged += OnPlaybackPositionChanged;
    }

    private void OnTimelinePlayheadChanged(double sec) => _boundVm?.SeekFromUser(sec);

    private void OnPlaybackPositionChanged(double sec) => Timeline.SetPlayheadFromPlayback(sec);

    private void WirePlayback(PlaybackEngine playback) => OnPlaybackAssigned(playback);

    private void TimelineTab_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: TimelineTab tab } && DataContext is EditorViewModel editor)
            editor.RenameTimelineCommand.Execute(tab);
    }

    private void MediaCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && sender is Control { DataContext: MediaAsset asset })
        {
            if (e.ClickCount == 2 && DataContext is EditorViewModel editor)
            {
                _dragArmed = false;
                editor.AddMediaToTimelineCommand.Execute(asset);
                return;
            }
            _dragArmed = true;
            _pressPoint = e.GetPosition(this);
            _pressEvent = e;
        }
    }

    private void MediaCard_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragArmed || sender is not Control card || card.DataContext is not MediaAsset asset || _pressEvent is null)
            return;

        var delta = e.GetPosition(this) - _pressPoint;
        var dist = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
        if (dist < DragThresholdPx)
            return;

        _dragArmed = false;

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(MediaFormat, asset));

        DragDrop.DoDragDropAsync(_pressEvent, transfer, DragDropEffects.Copy);
    }

    private void MediaCard_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragArmed = false;
    }

    private void CatalogCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (sender is not Control { DataContext: EffectCatalogEntry or TransitionCatalogEntry or EffectPreset or TextPreset })
            return;

        _dragArmed = true;
        _pressPoint = e.GetPosition(this);
        _pressEvent = e;
    }

    private void CatalogCard_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragArmed || sender is not Control card || _pressEvent is null)
            return;

        var delta = e.GetPosition(this) - _pressPoint;
        var dist = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
        if (dist < DragThresholdPx)
            return;

        _dragArmed = false;

        var transfer = new DataTransfer();
        switch (card.DataContext)
        {
            case EffectCatalogEntry effect:
                transfer.Add(DataTransferItem.Create(EffectFormat, effect));
                break;
            case TransitionCatalogEntry transition:
                transfer.Add(DataTransferItem.Create(TransitionFormat, transition));
                break;
            case EffectPreset preset:
                transfer.Add(DataTransferItem.Create(PresetFormat, preset));
                break;
            case TextPreset title:
                transfer.Add(DataTransferItem.Create(DragFormats.Text, title));
                break;
            default:
                return;
        }

        DragDrop.DoDragDropAsync(_pressEvent, transfer, DragDropEffects.Copy);
    }

    private void CatalogCard_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // a click (no drag) on a title card adds it at the playhead
        if (_dragArmed && sender is Control { DataContext: TextPreset title } && DataContext is EditorViewModel vm)
            vm.AddTextCommand.Execute(title);
        _dragArmed = false;
    }

    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Formats.Contains(DataFormat.File))
            e.DragEffects = DragDropEffects.Copy;
    }

    private async void OnFileDrop(object? sender, DragEventArgs e)
    {
        var vm = _boundVm ?? DataContext as EditorViewModel;
        if (vm is null)
            return;
        var files = e.DataTransfer.TryGetFiles();
        if (files is null)
            return;
        // This is the editor's single routed file-drop owner. Stop the event here so a
        // nested drop surface cannot cause the same files to be imported again.
        e.Handled = true;
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path is not null)
                await vm.ImportFileAsync(path);
        }
    }

    private void OnTimelineZoomRequested(TimelineZoom zoom)
    {
        switch (zoom)
        {
            case TimelineZoom.In: Timeline.ZoomInAtPlayhead(); break;
            case TimelineZoom.Out: Timeline.ZoomOutAtPlayhead(); break;
            case TimelineZoom.TallerTracks: Timeline.TallerTracks(); break;
            case TimelineZoom.ShorterTracks: Timeline.ShorterTracks(); break;
            case TimelineZoom.FitTracks: Timeline.FitTracks(); break;
            default: Timeline.ZoomToFitSequence(_boundVm?.SequenceEndSec ?? 60); break;
        }
    }

    private void TimelineZoomIn_Click(object? sender, RoutedEventArgs e) => OnTimelineZoomRequested(TimelineZoom.In);

    private void TimelineZoomOut_Click(object? sender, RoutedEventArgs e) => OnTimelineZoomRequested(TimelineZoom.Out);

    private void TimelineZoomFit_Click(object? sender, RoutedEventArgs e) => OnTimelineZoomRequested(TimelineZoom.FitSequence);

    private void TallerTracks_Click(object? sender, RoutedEventArgs e) => OnTimelineZoomRequested(TimelineZoom.TallerTracks);

    private void ShorterTracks_Click(object? sender, RoutedEventArgs e) => OnTimelineZoomRequested(TimelineZoom.ShorterTracks);

    private void FitTracks_Click(object? sender, RoutedEventArgs e) => OnTimelineZoomRequested(TimelineZoom.FitTracks);
}
