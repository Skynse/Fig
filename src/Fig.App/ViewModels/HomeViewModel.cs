using System;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fig.Core.Project;

namespace Fig.App.ViewModels;

public partial class HomeViewModel : ViewModelBase
{
    private readonly ProjectStore _store;

    [ObservableProperty]
    private System.Collections.ObjectModel.ObservableCollection<ProjectSummary> _projects = new();

    public event Action<Project>? ProjectOpened;
    public event Action<Project>? ProjectCreated;
    public Action<string>? Notify { get; set; }

    public HomeViewModel(ProjectStore store)
    {
        _store = store;
        Refresh();
    }

    public void Refresh()
    {
        Projects.Clear();
        foreach (var p in _store.ListProjects())
            Projects.Add(p);
    }

    [RelayCommand]
    private void OpenProject(ProjectSummary? summary)
    {
        if (summary is null)
            return;

        var project = _store.LoadProject(summary.Id);
        if (project is null)
            return;

        ProjectOpened?.Invoke(project);
    }

    [RelayCommand]
    private void DeleteProject(ProjectSummary? summary)
    {
        if (summary is null)
            return;
        _store.DeleteProject(summary.Id);
        Refresh();
    }

    [RelayCommand]
    private async Task CreateProjectAsync(Window? owner)
    {
        var name = await PromptProjectNameAsync(owner, "New Project", $"Untitled {DateTime.Now:yyyy-MM-dd}");
        if (name is null)
            return;

        var id = _store.CreateProject(name);
        var project = _store.LoadProject(id);
        if (project is not null)
            ProjectCreated?.Invoke(project);

        Refresh();
    }

    /// <summary>
    /// Imports one or more OpenTimelineIO (.otio) projects: parses them into the fig
    /// model, saves each as a project in the store, and opens the first one.
    /// </summary>
    [RelayCommand]
    private async Task ImportOtioAsync(Window? owner)
    {
        owner ??= Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } mw }
            ? mw
            : null;
        if (owner is null)
            return;

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import OpenTimelineIO Project",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("OpenTimelineIO")
                {
                    Patterns = new[] { "*.otio", "*.otioz", "*.json" },
                },
            },
        });

        Project? first = null;
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path is null)
                continue;

            try
            {
                var result = OtioImporter.ImportFromFile(path);
                var project = result.Project;
                project.Id = _store.CreateProject(project.Name);
                _store.SaveProject(project);

                Notify?.Invoke(SummarizeImport(Path.GetFileName(path), result));
                first ??= project;
            }
            catch (Exception ex)
            {
                Notify?.Invoke($"Import failed for {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        Refresh();

        if (first is not null)
            ProjectOpened?.Invoke(first);
    }

    private static string SummarizeImport(string name, OtioImportResult result)
    {
        var tracks = result.Project.Timelines.Sum(t => t.Tracks.Count);
        var summary = $"Imported \"{name}\": {result.ClipsImported} clips, {tracks} track(s), "
                    + $"{result.MarkersImported} marker(s), {result.EffectsImported} effect(s), "
                    + $"{result.TransitionsImported} transition(s)";
        if (result.Warnings.Count > 0)
            summary += $" ({result.Warnings.Count} warning(s))";
        return summary;
    }

    /// <summary>Asks for a project name. Returns null if the user cancels.</summary>
    public static async Task<string?> PromptProjectNameAsync(Window? owner, string title, string initial)
    {        if (owner is null)
            return string.IsNullOrWhiteSpace(initial) ? "Untitled" : initial.Trim();

        var dialog = new Window
        {
            Title = title,
            Width = 420,
            Height = 180,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#252526")),
        };

        string? result = null;
        var nameBox = new TextBox
        {
            Text = initial,
            PlaceholderText = "Project name",
            Margin = new Thickness(0, 4, 0, 0),
        };
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "What should this project be called?",
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(nameBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var create = new Button { Content = "Create", Width = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };
        create.Click += (_, _) =>
        {
            var typed = nameBox.Text?.Trim();
            result = string.IsNullOrWhiteSpace(typed) ? "Untitled" : typed;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        buttons.Children.Add(create);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        nameBox.AttachedToVisualTree += (_, _) =>
        {
            nameBox.Focus();
            nameBox.SelectAll();
        };

        await dialog.ShowDialog(owner);
        return result;
    }
}
