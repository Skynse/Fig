using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fig.Core.Timeline;

namespace Fig.App.ViewModels;

/// <summary>One caption in the Captions panel: when it shows, and what it says (edited in place).</summary>
public sealed partial class CaptionRowViewModel : ObservableObject
{
    private readonly CaptionsPanelViewModel _panel;
    private bool _loading;

    public CaptionRowViewModel(CaptionsPanelViewModel panel, TextClip clip)
    {
        _panel = panel;
        Id = clip.Id;
        Load(clip);
    }

    public string Id { get; }
    public double StartSec { get; private set; }
    public double EndSec { get; private set; }

    [ObservableProperty] private string _time = "";
    [ObservableProperty] private string _text = "";
    /// <summary>Showing at the playhead.</summary>
    [ObservableProperty] private bool _isCurrent;
    /// <summary>Selected on the timeline.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>Takes the clip's current values without writing them back.</summary>
    internal void Load(TextClip clip)
    {
        _loading = true;
        var (start, end) = (clip.StartSec, clip.StartSec + clip.DurSec);
        // formatted only when it moved: every row is reloaded on each keystroke
        if (start != StartSec || end != EndSec || Time.Length == 0)
        {
            (StartSec, EndSec) = (start, end);
            Time = $"{CaptionsPanelViewModel.Clock(start)} – {CaptionsPanelViewModel.Clock(end)}";
        }
        if (Text != clip.Text)
            Text = clip.Text;
        _loading = false;
    }

    partial void OnTextChanged(string value)
    {
        if (!_loading)
            _panel.SetText(this, value);
    }
}

/// <summary>
/// The Captions panel: every caption of a captions track as a list, to read and fix while the video plays. Typing
/// rewrites a caption (its word timings follow), and each row can be merged with the next, split at the cursor, or
/// deleted, each one undo step. Find and replace fixes a word everywhere. The look is set in the inspector, for all
/// captions at once while <see cref="EditorViewModel.CaptionStyleToAll"/> is on.
/// </summary>
public sealed partial class CaptionsPanelViewModel : ViewModelBase
{
    private readonly EditorViewModel _host;

    public CaptionsPanelViewModel(EditorViewModel host) => _host = host;

    public ObservableCollection<CaptionRowViewModel> Rows { get; } = new();

    /// <summary>The tracks that hold captions, by name; the list shows one.</summary>
    public ObservableCollection<string> TrackNames { get; } = new();
    private readonly List<string> _trackIds = new();

    [ObservableProperty] private int _trackIndex;
    [NotifyPropertyChangedFor(nameof(MatchSummary))]
    [ObservableProperty] private string _findText = "";
    [ObservableProperty] private string _replaceText = "";
    [ObservableProperty] private bool _matchCase;

    public bool HasCaptions => _trackIds.Count > 0;
    public bool NoCaptions => !HasCaptions;
    public bool HasManyTracks => _trackIds.Count > 1;
    public bool IsFinding => FindText.Length > 0;
    public string MatchSummary => !IsFinding ? "" : Rows.Count == 1 ? "1 caption" : $"{Rows.Count} captions";

    /// <summary>The row now playing changed; the view scrolls it into sight.</summary>
    public event Action<CaptionRowViewModel>? CurrentChanged;

    private bool _active;
    private bool _stale = true;

    /// <summary>
    /// The panel is on screen. While it isn't, timeline changes and the playhead only mark it stale, so a long
    /// caption list costs nothing during editing and playback elsewhere.
    /// </summary>
    public bool IsActive
    {
        get => _active;
        set
        {
            _active = value;
            if (value && _stale)
                Refresh();
        }
    }

    private string? TrackId => TrackIndex >= 0 && TrackIndex < _trackIds.Count ? _trackIds[TrackIndex] : null;

    partial void OnTrackIndexChanged(int value) => Refresh();
    partial void OnFindTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsFinding));
        Refresh();
    }
    partial void OnMatchCaseChanged(bool value) => Refresh();

    /// <summary>
    /// Re-reads the captions (called whenever the timeline changes). Rows are kept and updated when the same
    /// captions are still there, so the one being typed in keeps its cursor.
    /// </summary>
    public void Refresh()
    {
        if (!_active)
        {
            _stale = true;
            return;
        }
        _stale = false;
        var editor = _host.Editor;
        var tracks = editor.Document.Tracks.Where(t => TimelineEditor.CaptionsOn(t).Any()).ToList();
        if (!tracks.Select(t => t.Id).SequenceEqual(_trackIds) || !tracks.Select(Name).SequenceEqual(TrackNames))
        {
            var keep = TrackId;
            _trackIds.Clear();
            _trackIds.AddRange(tracks.Select(t => t.Id));
            TrackNames.Clear();
            foreach (var t in tracks)
                TrackNames.Add(Name(t));
            var index = keep is null ? 0 : Math.Max(0, _trackIds.IndexOf(keep));
            if (TrackIndex != index)
            {
                TrackIndex = index; // refreshes again
                return;
            }
            OnPropertyChanged(nameof(HasCaptions));
            OnPropertyChanged(nameof(NoCaptions));
            OnPropertyChanged(nameof(HasManyTracks));
        }

        var track = tracks.FirstOrDefault(t => t.Id == TrackId);
        var comparison = MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var captions = track is null
            ? []
            : TimelineEditor.CaptionsOn(track).Where(c => !IsFinding || c.Text.Contains(FindText, comparison)).ToList();

        if (captions.Select(c => c.Id).SequenceEqual(Rows.Select(r => r.Id)))
        {
            for (var i = 0; i < captions.Count; i++)
                Rows[i].Load(captions[i]);
        }
        else
        {
            Rows.Clear();
            foreach (var c in captions)
                Rows.Add(new CaptionRowViewModel(this, c));
        }
        OnPropertyChanged(nameof(MatchSummary));
        RefreshSelection();
        _current = null;
        foreach (var row in Rows)
            row.IsCurrent = false;
        FollowPlayhead(_host.PlayheadTimeSec);
    }

    private static string Name(Track t) => string.IsNullOrWhiteSpace(t.Name) ? "Captions" : t.Name!;

    public void RefreshSelection()
    {
        if (!_active)
            return;
        var selected = _host.Editor.Selection.SelectedClipIds;
        foreach (var row in Rows)
            row.IsSelected = selected.Contains(row.Id);
    }

    private CaptionRowViewModel? _current;

    /// <summary>
    /// Marks the caption showing at <paramref name="sec"/>. Runs on every playhead move, so it finds the row by
    /// binary search (rows are in time order and don't overlap) and touches only the old and new current rows.
    /// </summary>
    public void FollowPlayhead(double sec)
    {
        if (!_active)
            return;
        if (_current is { } still && sec >= still.StartSec && sec < still.EndSec)
            return;
        CaptionRowViewModel? now = null;
        int lo = 0, hi = Rows.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var row = Rows[mid];
            if (sec < row.StartSec)
                hi = mid - 1;
            else if (sec >= row.EndSec)
                lo = mid + 1;
            else
            {
                now = row;
                break;
            }
        }
        if (now == _current)
            return;
        if (_current is not null)
            _current.IsCurrent = false;
        _current = now;
        if (now is null)
            return;
        now.IsCurrent = true;
        CurrentChanged?.Invoke(now);
    }

    internal void SetText(CaptionRowViewModel row, string text)
    {
        _host.Editor.SetText([row.Id], text);
        _host.Preview.RefreshFrame();
    }

    /// <summary>A row was clicked or its text box entered: select the caption and show it (unless the video is playing).</summary>
    [RelayCommand]
    public void Go(CaptionRowViewModel? row)
    {
        if (row is null)
            return;
        if (!_host.Editor.Selection.SelectedClipIds.Contains(row.Id) || _host.Editor.Selection.SelectedClipIds.Count > 1)
            _host.Editor.Selection.SelectOnly(row.Id);
        if (!_host.IsPlaying && !row.IsCurrent)
            _host.SeekFromUser(row.StartSec);
    }

    [RelayCommand]
    private void MergeWithNext(CaptionRowViewModel? row)
    {
        if (row is null || _host.Editor.MergeCaptionWithNext(row.Id) is null)
            return;
        _host.Preview.RefreshFrame();
    }

    /// <summary>Splits a caption before the word at <paramref name="caret"/> (the cursor in its text box).</summary>
    public void Split(CaptionRowViewModel row, int caret)
    {
        if (_host.Editor.SplitCaption(row.Id, caret) is null)
        {
            _host.Notify?.Invoke("Put the cursor between two words to split the caption there.");
            return;
        }
        _host.Preview.RefreshFrame();
    }

    [RelayCommand]
    private void Delete(CaptionRowViewModel? row)
    {
        if (row is null)
            return;
        _host.Editor.RemoveCaption(row.Id);
        _host.Preview.RefreshFrame();
    }

    [RelayCommand]
    private void ReplaceAll()
    {
        var find = FindText;
        var count = _host.Editor.ReplaceInCaptions(find, ReplaceText, MatchCase, TrackId);
        _host.Notify?.Invoke(count == 0 ? $"“{find}” isn't in the captions" : $"Replaced {count} × “{find}”");
        if (count > 0)
        {
            FindText = "";
            _host.Preview.RefreshFrame();
        }
    }

    internal static string Clock(double sec)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, sec));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f") : t.ToString(@"m\:ss\.f");
    }
}
