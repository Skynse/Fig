using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fig.App.Services;
using Fig.App.Views;
using Fig.Core.Audio;
using Fig.Core.Captions;

namespace Fig.App.ViewModels;

/// <summary>Auto captions, and exporting captions as subtitles (SRT) or a transcript (TXT).</summary>
public partial class EditorViewModel
{
    private SpeechModels? _speechModels;

    /// <summary>
    /// Style edits to a caption (font, colours, size, position) reach every caption on its track, as in other
    /// editors' caption tracks; what each caption says stays its own.
    /// </summary>
    [ObservableProperty] private bool _captionStyleToAll = true;

    partial void OnCaptionStyleToAllChanged(bool value) => Remember(p => p.Captions.StyleAll = value);

    /// <summary>The clips a style edit of <paramref name="clipIds"/> reaches: their caption trackmates too, when styling all.</summary>
    internal IReadOnlyList<string> StyleTargets(IEnumerable<string> clipIds)
        => CaptionStyleToAll ? Editor.WithCaptionTrackmates(clipIds) : clipIds.ToList();
    private SpeechModels SpeechModels => _speechModels ??= new SpeechModels(AppPaths.SpeechModelsDir);

    /// <summary>Opens the auto captions dialog; the captions it makes go on a new track, as one undo step.</summary>
    [RelayCommand]
    private async Task GenerateCaptionsAsync(Window? owner)
    {
        if (Editor is null)
            return;
        owner ??= MainWindow;
        if (owner is null)
            return;
        await ShuttleStopAsync();

        var model = new CaptionsDialogViewModel(SpeechModels, Editor, () => new AudioMixer(_mediaService, FindAssetById),
            Preferences.Current.Captions);
        var cues = await new CaptionsDialog(model).ShowDialog<List<CaptionCue>?>(owner);
        Remember(p => model.RememberInto(p.Captions));
        if (cues is null)
            return;

        Editor.AddCaptions(cues);
        SelectedLibraryTab = LibraryTabs.First(t => t.Tab == LibraryPanelTab.Captions);
        Properties.Refresh();
        Preview.RefreshFrame();
        Notify?.Invoke($"Added {cues.Count} caption{(cues.Count == 1 ? "" : "s")} on a new track");
    }

    /// <summary>Saves the timeline's captions as an SRT subtitle file or a plain-text transcript, by the extension picked.</summary>
    [RelayCommand]
    private async Task ExportCaptionsAsync(Window? owner)
    {
        if (Editor is null || Project is null)
            return;
        owner ??= MainWindow;
        if (owner is null)
            return;
        var cues = Editor.CaptionCues();
        if (cues.Count == 0)
        {
            Notify?.Invoke("No captions to export. Make some with Auto captions, or tick “Include in subtitle export” on a text clip.");
            return;
        }

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export captions",
            SuggestedFileName = $"{Project.Name}.srt",
            DefaultExtension = "srt",
            FileTypeChoices =
            [
                new FilePickerFileType("SubRip subtitles") { Patterns = ["*.srt"] },
                new FilePickerFileType("Plain text transcript") { Patterns = ["*.txt"] },
            ],
        });
        if (file?.TryGetLocalPath() is not { } path)
            return;
        var text = Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase)
            ? CaptionBuilder.ToPlainText(cues)
            : CaptionBuilder.ToSrt(cues);
        try
        {
            await File.WriteAllTextAsync(path, text);
            Notify?.Invoke($"Exported {cues.Count} caption{(cues.Count == 1 ? "" : "s")} to {Path.GetFileName(path)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify?.Invoke($"Couldn't save the captions: {ex.Message}");
        }
    }
}
