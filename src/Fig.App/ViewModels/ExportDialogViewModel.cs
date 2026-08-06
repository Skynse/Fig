using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Fig.App.ViewModels;

/// <summary>A selectable resolution preset. Width/Height of 0 means "match the project".</summary>
public sealed record ResolutionPreset(string Name, int Width, int Height)
{
    public override string ToString() => Name;
}

/// <summary>The confirmed choices from the export dialog.</summary>
public sealed record ExportOptions(int Width, int Height, int Crf, string OutputPath);

public partial class ExportDialogViewModel : ViewModelBase
{
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
    private int _width;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeLabel))]
    private int _height;

    /// <summary>H.264 quality (CRF, lower = better).</summary>
    [ObservableProperty]
    private int _quality = 23;

    [ObservableProperty]
    private string _outputPath = "";

    public string FpsLabel { get; }
    public string SizeLabel => $"{Width}×{Height}";
    public bool CanExport => Width >= 2 && Height >= 2 && OutputPath.Length > 0;

    public ExportDialogViewModel(double fps, int defaultWidth, int defaultHeight)
    {
        FpsLabel = $"{fps:0.###} fps";
        _width = defaultWidth > 0 ? defaultWidth : 1920;
        _height = defaultHeight > 0 ? defaultHeight : 1080;
        _selectedPreset = Presets[0];
    }

    partial void OnSelectedPresetChanged(ResolutionPreset? value)
    {
        if (value is not null && value.Width > 0 && value.Height > 0)
        {
            Width = value.Width;
            Height = value.Height;
        }
    }

    partial void OnWidthChanged(int value) => OnPropertyChanged(nameof(CanExport));
    partial void OnHeightChanged(int value) => OnPropertyChanged(nameof(CanExport));
    partial void OnOutputPathChanged(string value) => OnPropertyChanged(nameof(CanExport));
}
