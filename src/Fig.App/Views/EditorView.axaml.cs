using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Fig.Core.Media;

namespace Fig.App.Views;

public partial class EditorView : UserControl
{
    public static readonly DataFormat<MediaAsset> MediaFormat =
        DataFormat<MediaAsset>.CreateInProcessFormat<MediaAsset>("fig.media");

    private const double DragThresholdPx = 8.0;
    private bool _dragArmed;
    private Point _pressPoint;
    private PointerPressedEventArgs? _pressEvent;

    public EditorView()
    {
        InitializeComponent();
    }

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

    private void ExitMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (VisualRoot is Window window)
            window.Close();
    }
}
