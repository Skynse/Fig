using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fig.App.Services;
using Fig.Core.Project;
using Fig.Core.Timeline;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.App.ViewModels;

/// <summary>One timeline's tab above the tracks.</summary>
public sealed partial class TimelineTab(TimelineModel timeline, string name, bool isActive) : ObservableObject
{
    public TimelineModel Timeline { get; } = timeline;

    [ObservableProperty]
    private string _name = name;

    [ObservableProperty]
    private bool _isActive = isActive;
}

/// <summary>
/// A project's timelines (alternate cuts, a short version, a vertical edit…), one tab each. Each keeps its own undo
/// history and playhead while another is open; export renders the one that is open.
/// </summary>
public partial class EditorViewModel
{
    public ObservableCollection<TimelineTab> TimelineTabs { get; } = new();

    /// <summary>The project's timelines (none before a project is open).</summary>
    private List<TimelineModel> Timelines => Project?.Timelines ?? [];

    // the timelines opened in this session: their edit history and where their playhead was
    private readonly Dictionary<TimelineModel, (TimelineEditor Editor, double Playhead)> _timelineStates = new();

    /// <summary>What a timeline's tab says: its name, or "Timeline 2" by its place in the project.</summary>
    private string TimelineName(TimelineModel timeline)
        => string.IsNullOrWhiteSpace(timeline.Name) ? $"Timeline {Timelines.IndexOf(timeline) + 1}" : timeline.Name!;

    private void RefreshTimelineTabs()
    {
        TimelineTabs.Clear();
        foreach (var timeline in Timelines)
            TimelineTabs.Add(new TimelineTab(timeline, TimelineName(timeline), ReferenceEquals(timeline, Editor.Document)));
    }

    /// <summary>Remembers the open timeline's editor and playhead before another one opens.</summary>
    private void StashOpenTimeline()
    {
        if (Editor?.Document is { } open && Timelines.Contains(open))
            _timelineStates[open] = (Editor, PlayheadTimeSec);
    }

    private void TimelinesChanged()
    {
        IsDirty = true;
        ScheduleAutosave();
        RefreshTimelineTabs();
    }

    [RelayCommand]
    private void SwitchTimeline(TimelineTab? tab)
    {
        if (tab is null || ReferenceEquals(tab.Timeline, Editor.Document))
            return;
        ActivateTimeline(tab.Timeline);
    }

    /// <summary>A new, empty timeline at the open one's frame rate and shape, after it.</summary>
    [RelayCommand]
    private void NewTimeline()
    {
        var current = Editor.Document;
        var timeline = new TimelineModel { Rate = current.Rate, RateChosen = current.RateChosen, Shape = current.Shape };
        Timelines.Insert(Timelines.IndexOf(current) + 1, timeline);
        ActivateTimeline(timeline);
        TimelinesChanged();
    }

    /// <summary>A copy of a timeline (the open one if none is given), after it, opened.</summary>
    [RelayCommand]
    private void DuplicateTimeline(TimelineTab? tab)
    {
        var source = tab?.Timeline ?? Editor.Document;
        var copy = ProjectStore.CopyTimeline(source);
        copy.Name = $"{TimelineName(source)} copy";
        Timelines.Insert(Timelines.IndexOf(source) + 1, copy);
        ActivateTimeline(copy);
        TimelinesChanged();
        Notify?.Invoke($"Duplicated {TimelineName(source)}");
    }

    [RelayCommand]
    private async Task RenameTimelineAsync(TimelineTab? tab)
    {
        var timeline = tab?.Timeline ?? Editor.Document;
        var name = await Dialogs.PromptTextAsync(null, "Rename timeline", "Timeline name", TimelineName(timeline), "Rename", "");
        if (name is null)
            return;
        timeline.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        TimelinesChanged();
    }

    /// <summary>Deletes a timeline after asking (it can't be undone). The last one stays.</summary>
    [RelayCommand]
    private async Task DeleteTimelineAsync(TimelineTab? tab)
    {
        var timeline = tab?.Timeline ?? Editor.Document;
        if (Timelines.Count <= 1)
        {
            Notify?.Invoke("A project keeps at least one timeline");
            return;
        }
        var name = TimelineName(timeline);
        if (timeline.Tracks.Any(t => t.Clips.Count > 0)
            && !await Dialogs.ConfirmAsync(null, "Delete timeline", $"Delete \"{name}\" and its edit? This can't be undone.", "Delete", danger: true))
            return;

        var index = Timelines.IndexOf(timeline);
        var wasOpen = ReferenceEquals(timeline, Editor.Document);
        if (wasOpen)
            ActivateTimeline(Timelines[index == 0 ? 1 : index - 1]);
        Timelines.Remove(timeline);
        _timelineStates.Remove(timeline);
        TimelinesChanged();
        Notify?.Invoke($"Deleted {name}");
    }
}
