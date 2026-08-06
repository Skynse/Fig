using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Fig.App.ViewModels;

public enum ExportJobStatus
{
    Queued,
    Running,
    Done,
    Failed,
}

/// <summary>One timeline export: output target, status, and live progress.</summary>
public partial class ExportJob : ViewModelBase
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string OutputPath { get; }
    public int Width { get; }
    public int Height { get; }
    public double Fps { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    private ExportJobStatus _status = ExportJobStatus.Queued;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public ExportJob(string outputPath, int width, int height, double fps)
    {
        OutputPath = outputPath;
        Width = width;
        Height = height;
        Fps = fps;
    }

    public string FileName => Path.GetFileName(OutputPath);
    public string SizeLabel => $"{Width}×{Height} · {Fps:0.###} fps";
    public string StatusLabel => Status switch
    {
        ExportJobStatus.Queued => "Queued",
        ExportJobStatus.Running => "Exporting…",
        ExportJobStatus.Done => "Done",
        ExportJobStatus.Failed => "Failed",
        _ => "",
    };
    public bool IsActive => Status is ExportJobStatus.Queued or ExportJobStatus.Running;
    public bool HasError => Error is not null;
}
