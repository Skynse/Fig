using System;
using System.IO;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Fig.App.ViewModels;

public enum ExportJobStatus
{
    Queued,
    Running,
    Done,
    Failed,
    Cancelled,
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
    [NotifyPropertyChangedFor(nameof(IsDone))]
    private ExportJobStatus _status = ExportJobStatus.Queued;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    /// <summary>The encoder the export ended up using ("NVIDIA NVENC", "CPU (x264)"), once it starts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeLabel))]
    private string? _encoderName;

    /// <summary>Stops the export: a queued one never starts, a running one stops and its partial file is deleted.</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    [RelayCommand]
    private void Cancel()
    {
        if (!IsActive)
            return;
        Cancellation.Cancel();
        // a queued job is done with straight away; a running one says so until the render stops
        if (Status == ExportJobStatus.Queued)
            Status = ExportJobStatus.Cancelled;
        else
            IsCancelling = true;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private bool _isCancelling;

    public ExportJob(string outputPath, int width, int height, double fps)
    {
        OutputPath = outputPath;
        Width = width;
        Height = height;
        Fps = fps;
    }

    public string FileName => Path.GetFileName(OutputPath);
    public string SizeLabel => EncoderName is null ? $"{Width}×{Height} · {Fps:0.###} fps" : $"{Width}×{Height} · {Fps:0.###} fps · {EncoderName}";
    public string StatusLabel => Status switch
    {
        ExportJobStatus.Queued => "Queued",
        ExportJobStatus.Running when IsCancelling => "Cancelling…",
        ExportJobStatus.Running => $"Exporting \u00b7 {Math.Clamp(Progress, 0, 1) * 100:0}%",
        ExportJobStatus.Done => "Complete",
        ExportJobStatus.Failed => "Failed",
        ExportJobStatus.Cancelled => "Cancelled",
        _ => "",
    };
    public bool IsDone => Status == ExportJobStatus.Done;
    public bool IsActive => Status is ExportJobStatus.Queued or ExportJobStatus.Running;
    public bool HasError => Error is not null;
}
