using CommunityToolkit.Mvvm.Input;

namespace Fig.App.ViewModels;

/// <summary>How the timeline's visible range or track height changes. The menu asks; the timeline view does it.</summary>
public enum TimelineZoom
{
    In,
    Out,
    FitSequence,
    TallerTracks,
    ShorterTracks,
    FitTracks,
}

public partial class EditorViewModel
{
    /// <summary>Raised when something outside the timeline (the View menu) wants its zoom changed.</summary>
    public event Action<TimelineZoom>? TimelineZoomRequested;

    [RelayCommand]
    private void ZoomTimelineIn() => TimelineZoomRequested?.Invoke(TimelineZoom.In);

    [RelayCommand]
    private void ZoomTimelineOut() => TimelineZoomRequested?.Invoke(TimelineZoom.Out);

    [RelayCommand]
    private void ZoomTimelineToFit() => TimelineZoomRequested?.Invoke(TimelineZoom.FitSequence);

    [RelayCommand]
    private void TallerTracks() => TimelineZoomRequested?.Invoke(TimelineZoom.TallerTracks);

    [RelayCommand]
    private void ShorterTracks() => TimelineZoomRequested?.Invoke(TimelineZoom.ShorterTracks);

    [RelayCommand]
    private void FitTracks() => TimelineZoomRequested?.Invoke(TimelineZoom.FitTracks);
}
