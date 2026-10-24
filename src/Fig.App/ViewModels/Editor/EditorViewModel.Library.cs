using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fig.App.Services;
using Fig.App.Views;
using Fig.Core.Input;
using Fig.Core.Media;
using Fig.Core.Project;
using Fig.Core.Templates;
using Fig.Core.Timeline;
using ProjectModel = Fig.Core.Project.Project;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.App.ViewModels;

/// <summary>
/// The library panel: its tabs, the effect / transition / text browsers with search and categories,
/// effect presets, and templates; and applying what is picked to the timeline.
/// </summary>
public partial class EditorViewModel
{
    /// <summary>Vertical library rail: Media, Transitions, Effects.</summary>
    public IReadOnlyList<LibraryTabItem> LibraryTabs { get; } =
    [
        new(LibraryPanelTab.Media, "film", "Media"),
        new(LibraryPanelTab.Templates, "clapperboard", "Templates"),
        new(LibraryPanelTab.Text, "type", "Text"),
        new(LibraryPanelTab.Transitions, "blend", "Transitions"),
        new(LibraryPanelTab.Effects, "wand-sparkles", "Effects"),
        new(LibraryPanelTab.Markers, "flag", "Markers"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLibraryMedia))]
    [NotifyPropertyChangedFor(nameof(IsLibraryTemplates))]
    [NotifyPropertyChangedFor(nameof(IsLibraryText))]
    [NotifyPropertyChangedFor(nameof(IsLibraryTransitions))]
    [NotifyPropertyChangedFor(nameof(IsLibraryEffects))]
    [NotifyPropertyChangedFor(nameof(IsLibraryMarkers))]
    [NotifyPropertyChangedFor(nameof(LibraryPanelTitle))]
    [NotifyPropertyChangedFor(nameof(LibraryPanelCount))]
    private LibraryTabItem? _selectedLibraryTab;

    public bool IsLibraryMedia => SelectedLibraryTab?.Tab == LibraryPanelTab.Media;
    public bool IsLibraryTemplates => SelectedLibraryTab?.Tab == LibraryPanelTab.Templates;
    public bool IsLibraryText => SelectedLibraryTab?.Tab == LibraryPanelTab.Text;
    public bool IsLibraryTransitions => SelectedLibraryTab?.Tab == LibraryPanelTab.Transitions;
    public bool IsLibraryEffects => SelectedLibraryTab?.Tab == LibraryPanelTab.Effects;
    public bool IsLibraryMarkers => SelectedLibraryTab?.Tab == LibraryPanelTab.Markers;
    public string LibraryPanelTitle => SelectedLibraryTab?.Title ?? "Media";

    public IReadOnlyList<EffectCatalogEntry> EffectCatalogItems => EffectCatalog.All;
    public IReadOnlyList<TransitionCatalogEntry> TransitionCatalogItems => TransitionCatalog.All;
    public IReadOnlyList<TextPreset> TextPresetItems => TextPresets.All;

    /// <summary>Adds a library title at the playhead, on the active track when it has room there.</summary>
    [RelayCommand]
    private void AddText(TextPreset? preset)
        => Editor.AddText(Editor.Selection.ActiveTrackId, PlayheadTimeSec, preset ?? TextPresets.All[0]);

    // ---- effects / transitions browsers: search + category filtering ----
    // ---- presets: named, reusable keyframed effects (built in, plus the user's own) ----
    private readonly EffectPresetStore _presetStore = new(AppPaths.PresetsDir);
    private List<EffectPreset> _allPresets = new();
    public ObservableCollection<EffectPreset> FilteredPresets { get; } = new();
    public bool HasPresets => FilteredPresets.Count > 0;

    private void LoadPresets()
    {
        _allPresets = BuiltInEffectPresets.All.Concat(_presetStore.List()).ToList();
        RefreshPresetFilter();
    }

    private void RefreshPresetFilter()
    {
        var query = EffectSearchText.Trim();
        FilteredPresets.Clear();
        foreach (var preset in _allPresets.Where(p =>
                     (SelectedEffectCategory == "All" || (p.Category ?? EffectCatalog.Find(p.TypeId)?.Category) == SelectedEffectCategory)
                     && (query.Length == 0
                         || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                         || p.Description.Contains(query, StringComparison.OrdinalIgnoreCase))))
                FilteredPresets.Add(preset);
        OnPropertyChanged(nameof(HasPresets));
        OnPropertyChanged(nameof(NoEffectMatches));
    }

    private Track? EffectTargetTrack()
    {
        var ownerId = Editor.Selection.SelectedEffect?.ClipId ?? Editor.Selection.ActiveTrackId;
        var track = Editor.Document.Tracks.FirstOrDefault(t => t.Id == ownerId);
        if (track is null && Editor.Selection.SelectedClipIds.FirstOrDefault() is { } clipId)
            track = Editor.Document.Tracks.FirstOrDefault(t => t.Id == Editor.FindClipTrackId(clipId));
        return track?.Kind == TrackKind.Video ? track : null;
    }

    [RelayCommand]
    private void ApplyPresetFromLibrary(EffectPreset? preset)
    {
        if (preset is null || Editor is null)
            return;
        var track = EffectTargetTrack();
        if (track is null)
        {
            Notify?.Invoke("Select a video track or clip to apply this");
            return;
        }
        var start = Math.Max(0, Editor.SnapTime(PlayheadTimeSec));
        var instance = EffectPresetInstantiator.Create(preset, start + preset.DefaultDurationSec, start);
        Editor.AddEffect(track.Id, instance);
        Preview.RefreshFrame();
        Properties.SelectEffect(track.Id, instance.Id);
        Notify?.Invoke(preset.TypeId == EffectCatalog.Transform
            ? $"Added \"{preset.Name}\" \u2014 move the playhead and drag the frame on the preview to add more keyframes"
            : $"Added \"{preset.Name}\" \u2014 adjust its keyframes in the inspector");
    }

    [RelayCommand]
    private void DeletePreset(EffectPreset? preset)
    {
        if (preset is null || preset.BuiltIn)
            return;
        _presetStore.Delete(preset.Id);
        LoadPresets();
    }

    /// <summary>Asks for a name and saves an effect's bar and keyframes as a reusable preset.</summary>
    public async Task SaveEffectAsPresetAsync(string clipId, string effectId)
    {
        var clip = Editor.FindEffectOwner(clipId);
        var effect = clip?.Effects.FirstOrDefault(e => e.Id == effectId);
        if (clip is null || effect is null)
            return;
        var name = await Dialogs.PromptTextAsync(null, "Save as preset", "Preset name", "My preset", "Save", "My preset");
        if (name is null)
            return;
        var preset = EffectPresetInstantiator.FromEffect(effect, clip.DurSec, name);
        preset.Description = "Saved from your own edit.";
        _presetStore.Save(preset);
        LoadPresets();
        SelectedLibraryTab = LibraryTabs.First(t => t.Tab == LibraryPanelTab.Effects);
        SelectedEffectCategory = EffectCatalog.Find(effect.TypeId)?.Category ?? "All";
        Notify?.Invoke($"Saved \"{name}\" \u2014 find it under Effects \u2192 {SelectedEffectCategory}");
    }

    public IReadOnlyList<string> EffectCategories { get; } = ["All", "Motion", "Blur", "Light", "Glitch", "Retro", "Distort", "Color", "Stylize", GraphEffectLibrary.Category];
    public IReadOnlyList<string> TransitionCategories { get; } = ["All", "Basic", "Camera", "Blur", "Light", "Glitch", "Wipe"];
    [ObservableProperty] private string _selectedTransitionCategory = "All";
    public ObservableCollection<EffectCatalogEntry> FilteredEffects { get; } = new();
    public ObservableCollection<TransitionCatalogEntry> FilteredTransitions { get; } = new();

    [ObservableProperty] private string _effectSearchText = "";
    [ObservableProperty] private string _selectedEffectCategory = "All";
    [ObservableProperty] private string _transitionSearchText = "";

    public bool NoEffectMatches => FilteredEffects.Count == 0 && FilteredPresets.Count == 0;
    public bool NoTransitionMatches => FilteredTransitions.Count == 0;

    /// <summary>Number of items in the open library tab, shown beside its title (effects and transitions only).</summary>
    public string? LibraryPanelCount => SelectedLibraryTab?.Tab switch
    {
        LibraryPanelTab.Effects => EffectCatalogItems.Count.ToString(),
        LibraryPanelTab.Transitions => TransitionCatalogItems.Count.ToString(),
        _ => null,
    };

    [RelayCommand]
    private void SelectEffectCategory(string? category)
    {
        if (!string.IsNullOrEmpty(category))
            SelectedEffectCategory = category;
    }

    partial void OnEffectSearchTextChanged(string value) { RefreshEffectFilter(); RefreshPresetFilter(); }
    partial void OnSelectedEffectCategoryChanged(string value) { RefreshEffectFilter(); RefreshPresetFilter(); }
    partial void OnTransitionSearchTextChanged(string value) => RefreshTransitionFilter();
    partial void OnSelectedTransitionCategoryChanged(string value) => RefreshTransitionFilter();

    private void RefreshEffectFilter()
    {
        var query = EffectSearchText.Trim();
        FilteredEffects.Clear();
        foreach (var e in EffectCatalogItems.Where(e =>
                     (SelectedEffectCategory == "All" || e.Category == SelectedEffectCategory)
                     && (query.Length == 0
                         || e.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                         || e.Description.Contains(query, StringComparison.OrdinalIgnoreCase))))
            FilteredEffects.Add(e);
        OnPropertyChanged(nameof(NoEffectMatches));
    }

    private void RefreshTransitionFilter()
    {
        var query = TransitionSearchText.Trim();
        FilteredTransitions.Clear();
        foreach (var t in TransitionCatalogItems.Where(t =>
                     (SelectedTransitionCategory == "All" || t.Category == SelectedTransitionCategory)
                     && (query.Length == 0
                     || t.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || t.Description.Contains(query, StringComparison.OrdinalIgnoreCase))))
            FilteredTransitions.Add(t);
        OnPropertyChanged(nameof(NoTransitionMatches));
    }

    private readonly TemplateStore _templateStore = new(AppPaths.TemplatesDir);
    public System.Collections.ObjectModel.ObservableCollection<VideoTemplate> Templates { get; } = new();
    public System.Collections.ObjectModel.ObservableCollection<VideoTemplate> FilteredTemplates { get; } = new();
    public IReadOnlyList<string> TemplateCategories { get; } =
        ["All", "My templates", "Social", "YouTube", "Business"];

    [ObservableProperty] private VideoTemplate? _selectedTemplate;
    [ObservableProperty] private string _templateSearchText = "";
    [ObservableProperty] private string _selectedTemplateCategory = "All";

    public bool HasTemplates => FilteredTemplates.Count > 0;
    public bool NoTemplates => !HasTemplates;

    partial void OnTemplateSearchTextChanged(string value) => RefreshTemplateFilter();
    partial void OnSelectedTemplateCategoryChanged(string value) => RefreshTemplateFilter();

    partial void OnSelectedLibraryTabChanged(LibraryTabItem? value)
    {
        // picking a rail tab while the panel is tucked away should show it
        if (value is not null && _constructed)
            Layout.RevealLeft();
    }

    private void LoadTemplates()
    {
        Templates.Clear();
        foreach (var template in BuiltInTemplateCatalog.All)
            Templates.Add(template);
        foreach (var template in _templateStore.List())
            Templates.Add(template);
        RefreshTemplateFilter();
    }

    private void RefreshTemplateFilter()
    {
        var query = TemplateSearchText.Trim();
        FilteredTemplates.Clear();
        foreach (var template in Templates.Where(template =>
                     (SelectedTemplateCategory == "All" || template.Category == SelectedTemplateCategory)
                     && (query.Length == 0
                         || template.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                         || template.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                         || template.Tags.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase)))))
        {
            FilteredTemplates.Add(template);
        }
        OnPropertyChanged(nameof(HasTemplates));
        OnPropertyChanged(nameof(NoTemplates));
    }

    /// <summary>Opens the transitions library (used by the inspector's "Replace").</summary>
    public void OpenTransitionsLibrary()
    {
        SelectedLibraryTab = LibraryTabs.First(t => t.Tab == LibraryPanelTab.Transitions);
        Layout.RevealLeft();
    }

    /// <summary>Opens the effects library (used by the inspector's "Add effect").</summary>
    [RelayCommand]
    private void OpenEffectsLibrary()
    {
        SelectedLibraryTab = LibraryTabs.First(t => t.Tab == LibraryPanelTab.Effects);
        Layout.RevealLeft();
    }

    [RelayCommand]
    private void CreateTemplate()
    {
        if (Project is null || !Editor.Document.Tracks.SelectMany(track => track.Clips).Any())
        {
            Notify?.Invoke("Add clips before saving a template");
            return;
        }

        var template = TemplateBuilder.FromTimeline(
            $"{Project.Name} template",
            "My templates",
            Editor.Document,
            Media,
            "Reusable edit created from the current timeline.");
        _templateStore.Save(template);
        LoadTemplates();
        SelectedLibraryTab = LibraryTabs.First(tab => tab.Tab == LibraryPanelTab.Templates);
        SelectedTemplateCategory = "My templates";
        SelectedTemplate = template;
        Notify?.Invoke($"Saved template \"{template.Name}\"");
    }

    [RelayCommand]
    private void UseTemplate(VideoTemplate? template)
    {
        if (template is null || Project is null)
            return;
        if (Editor.Document.Tracks.SelectMany(track => track.Clips).Any())
        {
            Notify?.Invoke("Templates can be applied to an empty timeline");
            return;
        }

        var available = Media.ToList();
        var assignments = new Dictionary<string, MediaAsset>();
        foreach (var slot in template.Slots)
        {
            var asset = available.FirstOrDefault(candidate =>
                candidate.Kind == slot.Kind || slot.Kind == MediaKind.Video && candidate.Kind == MediaKind.Image);
            if (asset is null)
            {
                Notify?.Invoke($"Import {template.SlotLabel} with compatible media first");
                return;
            }
            assignments[slot.Id] = asset;
            available.Remove(asset);
        }

        var timeline = TemplateInstantiator.Instantiate(template, assignments);
        // the template replaces the open timeline, keeping its name
        var open = Editor.Document;
        timeline.Name = open.Name;
        Project.Timelines[Math.Max(0, Project.Timelines.IndexOf(open))] = timeline;
        _timelineStates.Remove(open);
        ActivateTimeline(timeline);
        _store?.SaveProject(Project);
        Notify?.Invoke($"Applied \"{template.Name}\"");
    }

    [RelayCommand]
    private void ApplyEffectFromCatalog(EffectCatalogEntry? entry)
    {
        if (entry is null || Editor is null)
            return;
        var track = EffectTargetTrack();
        if (track is null)
        {
            Notify?.Invoke("Select a video track or clip to apply an effect");
            return;
        }
        var instance = entry.CreateInstance();
        instance.StartSec = Math.Max(0, Editor.SnapTime(PlayheadTimeSec));
        instance.DurSec = 3;
        Editor.AddEffect(track.Id, instance);
        Preview.RefreshFrame();
        Properties.SelectEffect(track.Id, instance.Id);
        Notify?.Invoke(entry.TypeId == EffectCatalog.Transform
            ? "Added Transform \u2014 press the stopwatch to animate it with keyframes"
            : $"Added {entry.DisplayName}");
    }

    // ---- effect graphs ---------------------------------------------------------------------

    [RelayCommand]
    private void ApplyTransitionFromCatalog(TransitionCatalogEntry? entry)
    {
        if (entry is null || Editor is null)
            return;
        var clipId = Editor.Selection.SelectedClipIds.FirstOrDefault();
        if (clipId is null)
        {
            Notify?.Invoke("Select the outgoing clip at a cut");
            return;
        }
        if (!Editor.TryApplyTransitionFromSelection(clipId, entry.CreateRef()))
        {
            Notify?.Invoke(Editor.TransitionValidationMessage ?? "Could not apply transition");
            return;
        }
        Preview.RefreshFrame();
        Properties.Refresh();
        Notify?.Invoke($"Applied {entry.DisplayName}");
    }
}
