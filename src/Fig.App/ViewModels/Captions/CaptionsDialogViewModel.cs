using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fig.App.Services;
using Fig.Core.Audio;
using Fig.Core.Captions;
using Fig.Core.Timeline;

namespace Fig.App.ViewModels;

/// <summary>A speech model in the captions dialog: whether it is downloaded, and its download while it runs.</summary>
public sealed partial class SpeechModelItem(SpeechModel model, bool installed) : ObservableObject
{
    public SpeechModel Model { get; } = model;
    public string Name => Model.Name;
    public string Description => Model.Description;

    [NotifyPropertyChangedFor(nameof(State))]
    [ObservableProperty] private bool _isInstalled = installed;

    public string State => IsInstalled ? "Downloaded" : $"{Model.SizeLabel} download";
}

/// <summary>
/// Auto captions: pick a speech model (downloaded on first use to <see cref="AppPaths.SpeechModelsDir"/>), the
/// language and how many words a caption holds, then listen to the timeline (or the selected clips' stretch of it)
/// and turn what is said into captions. The dialog closes with the captions; nothing in the document changes here.
/// </summary>
public sealed partial class CaptionsDialogViewModel : ViewModelBase
{
    private readonly SpeechModels _models;
    private readonly TimelineEditor _editor;
    private readonly Func<AudioMixer> _createMixer;
    private CancellationTokenSource? _work;

    /// <summary>The languages offered, as Whisper codes; Whisper knows more, these are the common ones.</summary>
    public static IReadOnlyList<(string Code, string Name)> LanguageChoices { get; } =
    [
        ("auto", "Detect automatically"), ("en", "English"), ("es", "Spanish"), ("fr", "French"), ("de", "German"),
        ("it", "Italian"), ("pt", "Portuguese"), ("nl", "Dutch"), ("pl", "Polish"), ("sv", "Swedish"),
        ("tr", "Turkish"), ("ru", "Russian"), ("uk", "Ukrainian"), ("ar", "Arabic"), ("hi", "Hindi"),
        ("id", "Indonesian"), ("vi", "Vietnamese"), ("ja", "Japanese"), ("ko", "Korean"), ("zh", "Chinese"),
        ("yo", "Yoruba"), ("sw", "Swahili"),
    ];

    public static IReadOnlyList<string> Languages { get; } = LanguageChoices.Select(l => l.Name).ToList();
    public static IReadOnlyList<string> Groupings { get; } = CaptionGrouping.Choices.Select(g => g.Name).ToList();

    public ObservableCollection<SpeechModelItem> Models { get; }

    [NotifyPropertyChangedFor(nameof(GenerateLabel))]
    [ObservableProperty] private SpeechModelItem? _selectedModel;
    [ObservableProperty] private int _languageIndex;
    [ObservableProperty] private int _groupingIndex;
    [ObservableProperty] private bool _translate;
    [ObservableProperty] private bool _useSelection;

    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private double _progress;
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    [ObservableProperty] private string _status = "";

    /// <summary>The captions made, once the dialog is done; null when it was cancelled.</summary>
    public List<CaptionCue>? Result { get; private set; }

    /// <summary>Closes the dialog (set by the window).</summary>
    public Action? Done { get; set; }

    public CaptionsDialogViewModel(SpeechModels models, TimelineEditor editor, Func<AudioMixer> createMixer, CaptionPreferences remembered)
    {
        _models = models;
        _editor = editor;
        _createMixer = createMixer;
        Models = new ObservableCollection<SpeechModelItem>(SpeechModels.All.Select(m => new SpeechModelItem(m, models.IsInstalled(m))));
        _selectedModel = Models.FirstOrDefault(m => m.Model.Id == remembered.Model) ?? Models.First(m => m.Model == SpeechModels.Default);
        _languageIndex = Math.Max(0, LanguageChoices.ToList().FindIndex(l => l.Code == remembered.Language));
        _groupingIndex = Math.Clamp(remembered.Grouping, 0, Groupings.Count - 1);
        _translate = remembered.Translate;
        _useSelection = HasSelection;
    }

    public bool IsIdle => !IsWorking;
    public bool HasStatus => Status.Length > 0;
    public bool HasSelection => SelectedSpan() is not null;
    public string ModelsFolder => _models.Directory;
    public string GenerateLabel => Chosen.IsInstalled ? "Generate captions" : $"Download ({Chosen.Model.SizeLabel}) and generate";

    // the list can briefly select nothing
    private SpeechModelItem Chosen => SelectedModel ?? Models.First(m => m.Model == SpeechModels.Default);

    /// <summary>What the dialog chose, to remember for next time.</summary>
    public void RememberInto(CaptionPreferences p)
    {
        p.Model = Chosen.Model.Id;
        p.Language = LanguageChoices[LanguageIndex].Code;
        p.Grouping = GroupingIndex;
        p.Translate = Translate;
    }

    /// <summary>The stretch of the timeline the selected clips cover, or null.</summary>
    private (double Start, double End)? SelectedSpan()
    {
        var clips = _editor.Selected.OfType<Clip>().ToList();
        return clips.Count == 0 ? null : (clips.Min(c => c.StartSec), clips.Max(c => c.StartSec + c.DurSec));
    }

    private (double Start, double End) Span()
    {
        if (UseSelection && SelectedSpan() is { } selected)
            return selected;
        var end = _editor.Document.Tracks.SelectMany(t => t.Clips).Select(c => c.StartSec + c.DurSec).DefaultIfEmpty(0).Max();
        return (0, end);
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task Generate()
    {
        var (start, end) = Span();
        if (end - start < 0.2)
        {
            Status = "There is nothing on the timeline to listen to.";
            return;
        }
        var item = Chosen;
        var options = new TranscribeOptions(LanguageChoices[LanguageIndex].Code, Translate);
        var grouping = CaptionGrouping.Choices[GroupingIndex].Grouping;

        _work = new CancellationTokenSource();
        var cancel = _work.Token;
        IsWorking = true;
        Progress = 0;
        try
        {
            if (!item.IsInstalled)
            {
                Status = $"Downloading the {item.Name} model ({item.Model.SizeLabel})…";
                await _models.DownloadAsync(item.Model, new Progress<double>(p => Progress = p), cancel);
                item.IsInstalled = true;
                OnPropertyChanged(nameof(GenerateLabel));
            }

            // listening is quick next to transcribing: the first tenth of the bar
            Status = "Listening to the timeline…";
            Progress = 0;
            var listening = new Progress<double>(p => Progress = p * 0.1);
            var timeline = _editor.Document;
            var samples = await Task.Run(() =>
            {
                using var mixer = _createMixer();
                return SpeechTranscriber.MixForSpeech(mixer, timeline, start, end - start, listening, cancel);
            }, cancel);

            Status = "Writing down what is said…";
            var transcribing = new Progress<double>(p => Progress = 0.1 + p * 0.9);
            var path = _models.PathOf(item.Model);
            var words = await Task.Run(() => SpeechTranscriber.TranscribeAsync(samples, path, options, start, transcribing, cancel), cancel);
            if (words.Count == 0)
            {
                Status = "No speech was heard. Check the sound isn't muted, or try a larger model.";
                return;
            }
            Result = CaptionBuilder.Group(words, grouping);
            Done?.Invoke();
        }
        catch (OperationCanceledException)
        {
            Status = "";
        }
        catch (Exception ex)
        {
            Status = $"Captions failed: {CrashLog.Write("captions", ex)}";
        }
        finally
        {
            IsWorking = false;
            Progress = 0;
            _work?.Dispose();
            _work = null;
        }
    }

    /// <summary>Stops a download or transcription in progress.</summary>
    [RelayCommand]
    public void Stop() => _work?.Cancel();

    /// <summary>Removes a downloaded model to free the space.</summary>
    [RelayCommand]
    private void DeleteModel(SpeechModelItem? item)
    {
        if (item is null || !item.IsInstalled || IsWorking)
            return;
        try
        {
            _models.Delete(item.Model);
            item.IsInstalled = false;
            OnPropertyChanged(nameof(GenerateLabel));
        }
        catch (IOException ex)
        {
            Status = $"Couldn't remove the model: {ex.Message}";
        }
    }
}
