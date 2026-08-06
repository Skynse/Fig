using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Fig.App.Services;
using Fig.App.ViewModels;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.App.Views;

public partial class EditorView : UserControl
{
    public static DataFormat<MediaAsset> MediaFormat => DragFormats.Media;
    public static DataFormat<EffectCatalogEntry> EffectFormat => DragFormats.Effect;
    public static DataFormat<TransitionCatalogEntry> TransitionFormat => DragFormats.Transition;

    private const double DragThresholdPx = 8.0;
    private bool _dragArmed;
    private Point _pressPoint;
    private PointerPressedEventArgs? _pressEvent;
    private EditorViewModel? _boundVm;

    public EditorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, OnFileDragOver);
        DragDrop.AddDropHandler(this, OnFileDrop);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_boundVm is not null)
        {
            _boundVm.PlaybackAssigned -= OnPlaybackAssigned;
            _boundVm = null;
        }

        if (DataContext is not EditorViewModel vm)
            return;

        _boundVm = vm;
        Timeline.PlayheadChanged += sec => vm.SeekFromUser(sec);
        vm.PlaybackAssigned += OnPlaybackAssigned;
        if (vm.Playback is not null)
            WirePlayback(vm.Playback);
    }

    private PlaybackEngine? _wiredPlayback;

    private void OnPlaybackAssigned(PlaybackEngine? playback)
    {
        if (playback is null)
            return;
        if (_wiredPlayback == playback)
            return;
        if (_wiredPlayback is not null)
            _wiredPlayback.PositionChanged -= OnPlaybackPositionChanged;
        _wiredPlayback = playback;
        playback.PositionChanged += OnPlaybackPositionChanged;
    }

    private void OnPlaybackPositionChanged(double sec) => Timeline.SetPlayheadFromPlayback(sec);

    private void WirePlayback(PlaybackEngine playback) => OnPlaybackAssigned(playback);

    private void MediaCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && sender is Control { DataContext: MediaAsset })
        {
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
        if (sender is not Control { DataContext: EffectCatalogEntry or TransitionCatalogEntry })
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
            default:
                return;
        }

        DragDrop.DoDragDropAsync(_pressEvent, transfer, DragDropEffects.Copy);
    }

    private void CatalogCard_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragArmed = false;
    }

    private void ExitMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window window)
            window.Close();
    }

    private async void CloseProjectMenu_Click(object? sender, RoutedEventArgs e)
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window?.DataContext is ViewModels.AppViewModel app)
            await app.CloseProjectAsync(window);
    }

    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Formats.Contains(DataFormat.File))
            e.DragEffects = DragDropEffects.Copy;
    }

    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        var vm = _boundVm ?? DataContext as EditorViewModel;
        if (vm is null)
            return;
        var files = e.DataTransfer.TryGetFiles();
        if (files is null)
            return;
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path is not null)
                vm.ImportFile(path);
        }
    }
}
