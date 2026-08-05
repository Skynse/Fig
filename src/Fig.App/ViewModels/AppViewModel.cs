using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fig.Core.Input;
using Fig.Core.Project;

namespace Fig.App.ViewModels;

public partial class AppViewModel : ViewModelBase
{
    public HomeViewModel Home { get; }
    public EditorViewModel Editor { get; }
    public ToastViewModel Toasts { get; } = new();
    private readonly ProjectStore _store;
    public GestureRegistry Gestures { get; }

    [ObservableProperty]
    private object? _currentView;

    public AppViewModel(ProjectStore store, GestureRegistry gestures)
    {
        _store = store;
        Gestures = gestures;
        Home = new HomeViewModel(store);
        Editor = new EditorViewModel(gestures);
        Editor.Notify = msg => Toasts.Show(msg);

        Home.ProjectOpened += OpenProject;
        Home.ProjectCreated += OpenProject;

        CurrentView = Home;
    }

    private async void OpenProject(Project project)
    {
        var loading = new LoadingViewModel();
        loading.Update("Opening project...", project.Name);
        CurrentView = loading;

        Editor.LoadProject(project, _store);

        var report = await Editor.ValidateProjectAsync(status =>
        {
            // validate runs on a background thread; marshal progress back to the UI thread
            Dispatcher.UIThread.Post(() => loading.Update(status));
        });

        if (report.HadIssues)
            loading.Update("Project opened", Summary(report));

        CurrentView = Editor;

        // heavy previews (filmstrip / waveform) backfill in the background so open never hangs
        _ = Editor.BackfillMediaPreviewsAsync();

        // the editor needs room to breathe; maximize when we enter it
        var window = MainWindow;
        if (window is not null)
            window.WindowState = WindowState.Maximized;
    }

    private static Window? MainWindow =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } mw }
            ? mw
            : null;

    private static string Summary(ProjectValidationReport report)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (report.ArtifactsRepaired > 0)
            parts.Add($"{report.ArtifactsRepaired} preview(s) regenerated");
        if (report.OfflineAssets > 0)
            parts.Add($"{report.OfflineAssets} source(s) offline");
        if (report.FailedArtifacts > 0)
            parts.Add($"{report.FailedArtifacts} preview(s) failed");
        return parts.Count > 0 ? string.Join(", ", parts) : "No issues found";
    }

    public void GoHome()
    {
        Home.Refresh();
        CurrentView = Home;
        var window = MainWindow;
        if (window is not null)
            window.WindowState = WindowState.Normal;
    }

    /// <summary>Closes the open project, returning to the home screen. Prompts to save if there are unsaved changes.</summary>
    public async Task<bool> CloseProjectAsync(Window? owner)
    {
        var editor = CurrentView as EditorViewModel;
        if (editor is not null && editor.IsDirty)
        {
            if (owner is null)
                return false;

            var dialog = new Window
            {
                Title = "Unsaved changes",
                Width = 380,
                Height = 170,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
            panel.Children.Add(new TextBlock { Text = $"\"{editor.Project?.Name}\" has unsaved changes. Save before closing?", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            var save = new Button { Content = "Save", Width = 80 };
            var discard = new Button { Content = "Discard", Width = 80 };
            var cancel = new Button { Content = "Cancel", Width = 80 };
            var result = false;
            save.Click += async (_, _) => { editor.SaveNow(); await Task.Delay(50); dialog.Close(); result = true; };
            discard.Click += (_, _) => { dialog.Close(); result = true; };
            cancel.Click += (_, _) => { dialog.Close(); };
            buttons.Children.Add(save);
            buttons.Children.Add(discard);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            dialog.Content = panel;
            await dialog.ShowDialog(owner);

            if (!result)
                return false;
        }

        editor?.DisposePlayback();
        GoHome();
        return true;
    }

    [RelayCommand]
    private async Task CloseProject(object? parameter)
    {
        var window = parameter as Window;
        if (window is null && Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } mw })
            window = mw;
        await CloseProjectAsync(window);
    }
}
