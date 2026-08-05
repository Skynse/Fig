using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Fig.App.ViewModels;

namespace Fig.App.Views;

public partial class PreviewView : UserControl
{
    private PreviewViewModel? _vm;

    public PreviewView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_vm is not null)
            _vm.FrameReady -= OnFrameReady;
        _vm = DataContext as PreviewViewModel;
        if (_vm is not null)
            _vm.FrameReady += OnFrameReady;
    }

    private void OnFrameReady(int width, int height, byte[] bgra)
    {
        Surface.Present(width, height, bgra);
    }
}
