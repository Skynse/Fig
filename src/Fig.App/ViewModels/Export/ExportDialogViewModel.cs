using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Fig.Core.Timeline;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.App.ViewModels;

/// <summary>A selectable resolution preset. Width/Height of 0 means "match the project".</summary>
public sealed record ResolutionPreset(string Name, int Width, int Height)
{
    public override string ToString() => Name;
}

/// <summary>One section of the timeline that is exported as its own video file.</summary>
public sealed record ExportChapter(string Name, string FileName, double StartSec, double EndSec);

/// <summary>The confirmed choices from the export dialog.</summary>
/// <param name="OutputPath">The output file, or the destination folder when <see cref="Chapters"/> is set.</param>
/// <param name="Chapters">When set, each chapter is exported as a separate file inside <see cref="OutputPath"/>.</param>
public sealed record ExportOptions(int Width, int Height, int Crf, string OutputPath,
    IReadOnlyList<ExportChapter>? Chapters = null,
    Fig.Core.Media.VideoEncoderChoice Encoder = Fig.Core.Media.VideoEncoderChoice.Automatic,
    Fig.Core.Media.ExportTurn Turn = Fig.Core.Media.ExportTurn.None);

/// <summary>One row in the "Export marked ranges" list.</summary>
public sealed partial class ExportChapterItem : ObservableObject
{
    private readonly Action _changed;

    public string Name { get; }
    public string Color { get; }
    public double StartSec { get; }
    public double EndSec { get; }

    [ObservableProperty]
    private bool _isIncluded = true;

    [ObservableProperty]
    private string _fileName;

    /// <summary>Why this file can't be written (shown next to the name), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string? _problem;

    public bool HasProblem => Problem is not null;
    public double DurationSec => EndSec - StartSec;
    public string RangeText => $"{ExportDialogViewModel.Clock(StartSec)} → {ExportDialogViewModel.Clock(EndSec)}";
    public string DurationText => $"{DurationSec:0.00}s";

    public ExportChapterItem(string name, string color, string fileName, double startSec, double endSec, Action changed)
    {
        Name = name;
        Color = color;
        _fileName = fileName;
        StartSec = startSec;
        EndSec = endSec;
        _changed = changed;
    }

    partial void OnIsIncludedChanged(bool value) => _changed();
    partial void OnFileNameChanged(string value) => _changed();
}

public partial class ExportDialogViewModel : ViewModelBase
{
    private readonly int _projectWidth;
    private readonly int _projectHeight;
    private readonly TimelineModel? _timeline;
    private bool _rebuilding;

    public IReadOnlyList<ResolutionPreset> Presets { get; } =
    [
        new("Match project", 0, 0),
        new("4K (3840×2160)", 3840, 2160),
        new("1080p (1920×1080)", 1920, 1080),
        new("720p (1280×720)", 1280, 720),
        new("540p (960×540)", 960, 540),
    ];

    [ObservableProperty]
    private ResolutionPreset? _selectedPreset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeLabel))]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private int _width;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeLabel))]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private int _height;

    /// <summary>H.264 quality (CRF, lower = better).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QualityStep))]
    [NotifyPropertyChangedFor(nameof(QualityLabel))]
    [NotifyPropertyChangedFor(nameof(CrfLabel))]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private int _quality = 23;

    /// <summary>The encoders on offer, in the order of <see cref="Fig.Core.Media.VideoEncoderChoice"/>.</summary>
    public static IReadOnlyList<string> Encoders { get; } =
    [
        "Automatic (GPU when there is one)",
        "NVIDIA NVENC",
        "Intel Quick Sync",
        "AMD AMF (Windows)",
        "VA-API (AMD / Intel on Linux)",
        "CPU (x264)",
    ];

    /// <summary>The chosen encoder, as an index into <see cref="Encoders"/>.</summary>
    [ObservableProperty]
    private int _encoderIndex;

    public Fig.Core.Media.VideoEncoderChoice Encoder
    {
        get => (Fig.Core.Media.VideoEncoderChoice)Math.Clamp(EncoderIndex, 0, Encoders.Count - 1);
        set => EncoderIndex = (int)value;
    }

    /// <summary>How the video lies in the file, in the order of <see cref="Fig.Core.Media.ExportTurn"/>.</summary>
    public static IReadOnlyList<string> Turns { get; } = ["As edited", "Sideways, turned right", "Sideways, turned left"];

    /// <summary>The chosen turn, as an index into <see cref="Turns"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeLabel))]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private int _turnIndex;

    public Fig.Core.Media.ExportTurn Turn => (Fig.Core.Media.ExportTurn)Math.Clamp(TurnIndex, 0, Turns.Count - 1);

    /// <summary>The file (single export) or destination folder (marked ranges).</summary>
    [ObservableProperty]
    private string _outputPath = "";

    /// <summary>Export each marked range as a separate video instead of the whole timeline.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSingle))]
    private bool _chapterMode;

    [ObservableProperty]
    private bool _showAdvanced;

    public string ProjectName { get; set; } = "";
    public string SequenceName { get; set; } = "Sequence 01";
    public string Subtitle => string.IsNullOrWhiteSpace(ProjectName) ? SequenceName : $"{ProjectName} · {SequenceName}";

    /// <summary>Set by the editor: opens the "Create ranges from bookmarks" step. Returns true if ranges were added.</summary>
    public Func<System.Threading.Tasks.Task<bool>>? CreateRangesFromBookmarks { get; set; }

    [ObservableProperty]
    private bool _showSettings;

    public bool HasBookmarks => _timeline?.Markers.Any(m => m.DurSec <= 0) == true;

    public ObservableCollection<ExportChapterItem> Chapters { get; } = new();

    public string FpsLabel { get; }
    /// <summary>The file's size: the picture's, the other way up when turned sideways.</summary>
    public string SizeLabel => Turn == Fig.Core.Media.ExportTurn.None ? $"{Width} × {Height}" : $"{Height} × {Width}";
    public bool IsSingle => !ChapterMode;
    public bool HasTimelineMarkers => _timeline is { Markers.Count: > 0 };

    /// <summary>The marker colours that exist on the timeline: a chip for each picks the ranges of that colour.</summary>
    public IReadOnlyList<string> MarkerColors { get; }

    /// <summary>
    /// The header's tick box: true when every range is ticked, false when none is, null (a dash) in between.
    /// Setting it ticks or unticks them all.
    /// </summary>
    public bool? AllIncluded
    {
        get => Chapters.Count == 0 || Chapters.All(c => c.IsIncluded) ? true : Chapters.Any(c => c.IsIncluded) ? null : false;
        set => SetIncluded(Chapters, value != false);
    }

    /// <summary>Ticks or unticks a set of ranges as one change.</summary>
    public void SetIncluded(IEnumerable<ExportChapterItem> chapters, bool included)
    {
        _rebuilding = true;
        foreach (var chapter in chapters)
            chapter.IsIncluded = included;
        _rebuilding = false;
        Revalidate();
    }

    /// <summary>Ticks just the ranges marked in <paramref name="color"/> (or adds them, when <paramref name="add"/>).</summary>
    public void SelectColor(string color, bool add)
    {
        _rebuilding = true;
        foreach (var chapter in Chapters)
        {
            var match = string.Equals(chapter.Color, color, StringComparison.OrdinalIgnoreCase);
            if (match || !add)
                chapter.IsIncluded = match;
        }
        _rebuilding = false;
        Revalidate();
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void InvertSelection()
    {
        _rebuilding = true;
        foreach (var chapter in Chapters)
            chapter.IsIncluded = !chapter.IsIncluded;
        _rebuilding = false;
        Revalidate();
    }

    /// <summary>Quality as a 0–10 slider position: left = smaller file, right = higher quality.</summary>
    public int QualityStep
    {
        get => Math.Clamp(28 - Quality, 0, 10);
        set => Quality = 28 - Math.Clamp(value, 0, 10);
    }

    public string QualityLabel => QualityStep switch
    {
        <= 2 => "Smaller file",
        >= 8 => "High quality",
        _ => "Balanced",
    };

    public string CrfLabel => $"CRF {Quality}";

    public string SummaryText => $"MP4 · {Width} × {Height} · {FpsLabel} · {QualityLabel} quality";

    public string SizeNote => "Frame rate matches the project";

    public string ChapterSummary
    {
        get
        {
            var included = Chapters.Where(c => c.IsIncluded).ToList();
            var total = included.Sum(c => c.DurationSec);
            var of = included.Count == Chapters.Count ? $"{Chapters.Count} ranges" : $"{included.Count} of {Chapters.Count} ranges selected";
            return $"{of} · {included.Count} files · {total:0} seconds total";
        }
    }

    public string ExportButtonText => ChapterMode
        ? Chapters.Count(c => c.IsIncluded) is var n && n == 1 ? "Export 1 video" : $"Export {Chapters.Count(c => c.IsIncluded)} videos"
        : "Export";

    /// <summary>The first thing blocking export, shown under the buttons so the user knows what to fix.</summary>
    public string? ValidationMessage { get; private set; }

    public bool CanExport => Validate() is null;

    public ExportDialogViewModel(double fps, int defaultWidth, int defaultHeight, TimelineModel? timeline = null)
    {
        FpsLabel = $"{fps:0.###} fps";
        _projectWidth = defaultWidth > 0 ? defaultWidth : 1920;
        _projectHeight = defaultHeight > 0 ? defaultHeight : 1080;
        _width = _projectWidth;
        _height = _projectHeight;
        _selectedPreset = Presets[0];
        _timeline = timeline;
        MarkerColors = timeline?.Markers.Select(m => m.Color).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                       ?? new List<string>();
    }

    partial void OnSelectedPresetChanged(ResolutionPreset? value)
    {
        if (value is null)
            return;
        if (value.Width > 0 && value.Height > 0)
        {
            Width = value.Width;
            Height = value.Height;
        }
        else
        {
            // "Match project" restores the project's own size
            Width = _projectWidth;
            Height = _projectHeight;
        }
    }

    partial void OnWidthChanged(int value) => Revalidate();
    partial void OnHeightChanged(int value) => Revalidate();
    partial void OnOutputPathChanged(string value) => Revalidate();
    partial void OnQualityChanged(int value) => Revalidate();

    partial void OnChapterModeChanged(bool value)
    {
        RebuildChapters();
        OnPropertyChanged(nameof(ExportButtonText));
        Revalidate();
    }


    /// <summary>
    /// Builds the range list from the timeline's markers. A range marker spans its own
    /// duration; a point marker runs until the next marker (or the end of the timeline).
    /// Filtering by colour never changes where a kept range ends.
    /// </summary>
    public void RebuildChapters()
    {
        _rebuilding = true;
        Chapters.Clear();
        if (ChapterMode && _timeline is not null)
        {
            var rate = _timeline.Rate;
            var end = rate.ToSeconds(rate.ToFrame(TimelineEnd(_timeline)));
            // explicit ranges win; a timeline with only point bookmarks falls back to
            // "section between bookmarks" so it is still exportable without extra setup
            var markers = _timeline.Markers.OrderBy(m => m.StartSec).ToList();
            var hasRanges = markers.Any(m => m.DurSec > 0);
            var index = 0;
            for (var i = 0; i < markers.Count; i++)
            {
                var marker = markers[i];
                if (hasRanges && marker.DurSec <= 0)
                    continue;
                index++;
                var color = marker.Color;

                var start = rate.ToSeconds(rate.ToFrame(marker.StartSec));
                double stop;
                if (marker.DurSec > 0)
                    stop = marker.StartSec + marker.DurSec;
                else
                    stop = markers.Skip(i + 1).Select(m => m.StartSec).Where(s => s > marker.StartSec + 1e-6).DefaultIfEmpty(end).First();
                stop = rate.ToSeconds(rate.ToFrame(Math.Min(stop, end)));
                if (stop - start < 1e-6)
                    continue;

                var name = string.IsNullOrWhiteSpace(marker.Name) ? $"Range {index}" : marker.Name.Trim();
                var fileName = string.IsNullOrWhiteSpace(marker.OutputName)
                    ? $"{index:00}-{Slug(name)}.mp4"
                    : (marker.OutputName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? marker.OutputName.Trim() : marker.OutputName.Trim() + ".mp4");
                Chapters.Add(new ExportChapterItem(name, color, fileName, start, stop, Revalidate) { IsIncluded = marker.IncludeInExport });
            }
        }
        _rebuilding = false;
        OnPropertyChanged(nameof(AllIncluded));
    }

    private static double TimelineEnd(TimelineModel timeline) =>
        timeline.Tracks.SelectMany(t => t.Clips).Select(c => c.StartSec + c.DurSec).DefaultIfEmpty(0).Max();

    /// <summary>Name → a file-system-friendly lowercase slug ("Save changes" → "save-changes").</summary>
    public static string Slug(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "range" : slug;
    }

    public static string Clock(double sec)
    {
        var total = (int)Math.Round(sec);
        return $"{total / 60:00}:{total % 60:00}";
    }

    private void Revalidate()
    {
        if (_rebuilding)
            return;
        Validate();
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(ChapterSummary));
        OnPropertyChanged(nameof(ExportButtonText));
        OnPropertyChanged(nameof(AllIncluded));
    }

    /// <summary>Returns the reason export is blocked, or null when everything is valid. Also marks per-file problems.</summary>
    private string? Validate()
    {
        string? message = null;
        if (Width < 2 || Height < 2)
            message = "Width and height must be at least 2.";
        else if (Width % 2 != 0 || Height % 2 != 0)
            message = "Width and height must be even numbers (H.264 requirement).";

        if (!ChapterMode)
        {
            if (message is null && string.IsNullOrWhiteSpace(OutputPath))
                message = "Choose where to save the video.";
            ValidationMessage = message;
            return message;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // the folder is created on export, so it only has to be creatable (its parent exists)
        var folderExists = !string.IsNullOrWhiteSpace(OutputPath) && Directory.Exists(OutputPath);
        var folderOk = folderExists || (!string.IsNullOrWhiteSpace(OutputPath)
            && Path.GetDirectoryName(OutputPath.TrimEnd('/', '\\')) is { Length: > 0 } parent && Directory.Exists(parent));
        var included = 0;
        var clashes = 0;
        foreach (var chapter in Chapters)
        {
            chapter.Problem = null;
            if (!chapter.IsIncluded)
                continue;
            included++;
            var file = chapter.FileName?.Trim() ?? "";
            if (file.Length == 0 || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                chapter.Problem = "Enter a valid file name";
            else if (!seen.Add(file))
                chapter.Problem = "Another range uses this file name";
            else if (folderExists && File.Exists(Path.Combine(OutputPath, file)))
                chapter.Problem = "A file with this name already exists";
            if (chapter.Problem is not null)
                clashes++;
        }

        if (message is null && included == 0)
            message = Chapters.Count == 0 ? "There are no ranges to export." : "Select at least one range to export.";
        if (message is null && string.IsNullOrWhiteSpace(OutputPath))
            message = "Choose a destination folder.";
        if (message is null && !folderOk)
            message = "The destination folder can't be created here.";
        if (message is null && clashes > 0)
            message = clashes == 1 ? "Fix the file name marked below." : $"Fix the {clashes} file names marked below.";
        ValidationMessage = message;
        return message;
    }

    /// <summary>The confirmed export request (valid only while <see cref="CanExport"/> is true).</summary>
    public ExportOptions CreateOptions()
    {
        IReadOnlyList<ExportChapter>? chapters = null;
        if (ChapterMode)
            chapters = Chapters.Where(c => c.IsIncluded)
                .Select(c => new ExportChapter(c.Name, c.FileName.Trim(), c.StartSec, c.EndSec))
                .ToList();
        return new ExportOptions(Width, Height, Quality, OutputPath, chapters, Encoder, Turn);
    }
}
