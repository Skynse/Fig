using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Fig.App.ViewModels;

/// <summary>A single toast message shown briefly in the top-right corner.</summary>
public partial class ToastItem : ViewModelBase
{
    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private bool _visible;
}

public partial class ToastViewModel : ViewModelBase
{
    private readonly object _lock = new();

    public ObservableCollection<ToastItem> Toasts { get; } = new();

    /// <summary>Shows a toast in the top-right that auto-dismisses after <paramref name="durationMs"/>.</summary>
    public void Show(string message, int durationMs = 2200)
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Show(message, durationMs));
            return;
        }

        var item = new ToastItem { Message = message };
        Toasts.Add(item);

        item.Visible = true;
        _ = Task.Delay(durationMs).ContinueWith(_ =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => item.Visible = false);
            _ = Task.Delay(250).ContinueWith(__ =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Toasts.Remove(item));
            });
        });
    }
}
