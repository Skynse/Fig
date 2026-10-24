using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Fig.App.ViewModels;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.App.Views;

public partial class ExportDialog : Window
{
    public ExportDialogViewModel Model { get; }

    public ExportDialog()
        : this(30, 1920, 1080)
    {
    }

    public ExportDialog(double fps, int defaultWidth, int defaultHeight, TimelineModel? timeline = null,
        string projectName = "", bool chapterMode = false, Fig.App.Services.ExportPreferences? defaults = null)
    {
        Model = new ExportDialogViewModel(fps, defaultWidth, defaultHeight, timeline) { ProjectName = projectName };
        if (defaults is not null)
        {
            Model.Quality = defaults.Quality;
            Model.Encoder = defaults.Encoder;
        }
        var videos = defaults?.Folder is { Length: > 0 } folder && Directory.Exists(folder)
            ? folder
            : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrEmpty(videos))
            videos = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var safeName = string.IsNullOrWhiteSpace(projectName) ? "export" : projectName.Trim();
        Model.ChapterMode = chapterMode;
        Model.OutputPath = chapterMode ? Path.Combine(videos, safeName) : Path.Combine(videos, safeName + ".mp4");
        DataContext = Model;
        InitializeComponent();
        AddHandler(PointerPressedEvent, (_, e) => _lastModifiers = e.KeyModifiers, RoutingStrategies.Tunnel, handledEventsToo: true);
        Title = chapterMode ? "Export marked ranges" : "Export timeline";
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        if (Model.ChapterMode)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Export videos to",
                AllowMultiple = false,
            });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } folder)
                Model.OutputPath = folder;
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export to",
            SuggestedFileName = Path.GetFileName(Model.OutputPath) is { Length: > 0 } n ? n : "export.mp4",
            DefaultExtension = "mp4",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("MP4 (H.264 / AAC)") { Patterns = new[] { "*.mp4" } },
            },
        });
        if (file is not null)
            Model.OutputPath = file.TryGetLocalPath() ?? "";
    }

    private async void CreateRanges_Click(object? sender, RoutedEventArgs e)
    {
        if (Model.CreateRangesFromBookmarks is null)
            return;
        if (await Model.CreateRangesFromBookmarks())
            Model.RebuildChapters();
    }

    // the range whose box was clicked last, for Shift-click
    private ExportChapterItem? _anchor;

    /// <summary>A range's box was clicked: Shift-click sets every range from the last one clicked to this one the same way.</summary>
    private void ChapterCheck_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ExportChapterItem chapter })
            return;
        ApplyRange(chapter, chapter.IsIncluded);
    }

    /// <summary>A click on a range's row (not its box or file name) ticks or unticks it.</summary>
    private void ChapterRow_Tapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.GetSelfAndVisualAncestors().Any(v => v is TextBox or CheckBox))
            return;
        if (sender is not Control { DataContext: ExportChapterItem chapter })
            return;
        chapter.IsIncluded = !chapter.IsIncluded;
        ApplyRange(chapter, chapter.IsIncluded);
    }

    private void ApplyRange(ExportChapterItem chapter, bool included)
    {
        var shift = _lastModifiers.HasFlag(KeyModifiers.Shift);
        if (shift && _anchor is not null && Model.Chapters.IndexOf(_anchor) is var from and >= 0)
        {
            var to = Model.Chapters.IndexOf(chapter);
            var (lo, hi) = from < to ? (from, to) : (to, from);
            Model.SetIncluded(Model.Chapters.Skip(lo).Take(hi - lo + 1), included);
        }
        _anchor = chapter;
    }

    /// <summary>A colour chip: just the ranges of that colour (Shift: those as well).</summary>
    private void ColorChip_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string color })
            Model.SelectColor(color, _lastModifiers.HasFlag(KeyModifiers.Shift));
    }

    // clicks don't carry the keys held, so the window notes them as the pointer goes down (on the way in, before a
    // box or button takes the press)
    private KeyModifiers _lastModifiers;


    private void ToggleSettings_Click(object? sender, RoutedEventArgs e) => Model.ShowSettings = !Model.ShowSettings;

    private void Export_Click(object? sender, RoutedEventArgs e)
    {
        if (!Model.CanExport)
            return;
        Close(Model.CreateOptions());
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
