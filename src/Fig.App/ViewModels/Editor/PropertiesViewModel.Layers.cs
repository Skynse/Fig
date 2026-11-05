using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Fig.Core.Timeline;

namespace Fig.App.ViewModels;

/// <summary>
/// The clip properties in the inspector, built from <see cref="ClipProperties.All"/>: each tab gets the groups of
/// properties its selection has, one editor per property, keyframe controls on the keyable ones. Nothing here is
/// written per property; add one to the schema and it appears. Also the two text settings that aren't properties
/// with a range: the words, and the font (whose choices are this machine's fonts).
/// </summary>
public partial class PropertiesViewModel
{
    [ObservableProperty] private string _textValue = "";
    [ObservableProperty] private string _fontFamilyValue = TextStyle.Default.FontFamily;

    private IReadOnlyList<string>? _fontFamilies;

    /// <summary>The fonts installed on this machine, by name.</summary>
    public IReadOnlyList<string> FontFamilies => _fontFamilies ??= FontManager.Current.SystemFonts
        .Select(f => f.Name).Append(TextStyle.Default.FontFamily)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    private readonly Dictionary<Facet, ObservableCollection<PropertyGroupViewModel>> _propertyGroups = new();
    private string? _propertyContext;

    public ObservableCollection<PropertyGroupViewModel> PictureGroups => GroupsFor(Facet.Picture);
    public ObservableCollection<PropertyGroupViewModel> TextGroups => GroupsFor(Facet.Text);
    public ObservableCollection<PropertyGroupViewModel> LayoutGroups => GroupsFor(Facet.Layout);
    public ObservableCollection<PropertyGroupViewModel> SoundGroups => GroupsFor(Facet.Sound);
    public ObservableCollection<PropertyGroupViewModel> RetimeGroups => GroupsFor(Facet.Retime);
    public ObservableCollection<PropertyGroupViewModel> FadesGroups => GroupsFor(Facet.Fades);

    /// <summary>The property groups shown in a tab (live: they follow the selection).</summary>
    public ObservableCollection<PropertyGroupViewModel> GroupsFor(Facet tab)
    {
        if (!_propertyGroups.TryGetValue(tab, out var groups))
            _propertyGroups[tab] = groups = new ObservableCollection<PropertyGroupViewModel>();
        return groups;
    }

    /// <summary>
    /// Builds the property editors for the selected clips (the properties every one of them has), or, when the
    /// selection is the same, re-reads their values.
    /// </summary>
    private void LoadProperties(IReadOnlyList<Clip> clips)
    {
        var context = string.Join("|", clips.Select(c => c.Id));
        if (context == _propertyContext)
        {
            RefreshProperties();
            return;
        }
        _propertyContext = context;
        foreach (var groups in _propertyGroups.Values)
            groups.Clear();
        if (clips.Count == 0)
            return;

        var primaryId = clips[^1].Id;
        foreach (var property in ClipProperties.All.Where(p => clips.All(p.Has)))
        {
            var groups = GroupsFor(property.ShownIn);
            var group = groups.FirstOrDefault(g => g.Title == property.Group);
            if (group is null)
                groups.Add(group = new PropertyGroupViewModel(property.Group));
            var p = property;
            var target = new KeyedParamTarget(_editor, KeyframeHostRef.OfClip(primaryId), p.Key, p.Keyable,
                readBase: () => FindClip(primaryId) is { } clip ? p.Get(clip) : p.Def.DefaultValue(),
                writeBase: value => _editor.Editor.SetClipProperty(Targets(p), p, value, _editor.PlayheadTimeSec),
                writeHandlesKeys: true);
            var editor = ParamEditorViewModel.For(p.Def, target, p.Icon);
            if (p.Shown is { } shown)
                editor.ShownWhen = () => FindClip(primaryId) is { } clip && shown(clip);
            group.Editors.Add(editor);
        }
        RefreshProperties();
    }

    /// <summary>
    /// The clips an edit of <paramref name="property"/> reaches: the selection (and what is linked to it), and, for a
    /// caption's look or position while styling all, the other captions on its track.
    /// </summary>
    private IReadOnlyList<string> Targets(ClipProperty property)
    {
        var targets = _editor.Editor.EditTargets(SelectedClipIds, property.Facet);
        return property.Facet is Facet.Text or Facet.Layout && property != ClipProperties.IsCaption
            ? _editor.StyleTargets(targets)
            : targets;
    }

    /// <summary>Re-reads every property editor at the playhead.</summary>
    private void RefreshProperties()
    {
        foreach (var group in _propertyGroups.Values.SelectMany(g => g))
            group.Refresh();
    }

    private void ClearProperties()
    {
        _propertyContext = null;
        foreach (var groups in _propertyGroups.Values)
            groups.Clear();
    }

    /// <summary>The clips selected on the timeline (the primary one last).</summary>
    private IEnumerable<string> SelectedClipIds => _editor.Editor.Selected.OfType<Clip>().Select(c => c.Id);

    /// <summary>
    /// Writes one inspector value to every selected clip that has <paramref name="facet"/> (and the clips linked
    /// to them), as one undo step; a slider drag coalesces into it. Each <paramref name="property"/> name must be
    /// unique to its setter: edits coalesce by name.
    /// </summary>
    private void SetOnSelection<T>(Facet facet, string property, Func<Clip, T> get, Action<Clip, T> set, T value,
        bool style = false)
    {
        if (_suppressApply || !Has(facet))
            return;
        var targets = _editor.Editor.EditTargets(SelectedClipIds, facet);
        _editor.Editor.SetClipValue(style ? _editor.StyleTargets(targets) : targets, property, get, set, value);
        _editor.Preview.RefreshFrame();
    }

    private void LoadLayer(Clip? clip)
    {
        if (clip is not TextClip text)
            return;
        TextValue = text.Text;
        FontFamilyValue = text.Style.FontFamily;
    }

    partial void OnTextValueChanged(string value)
    {
        if (_suppressApply || !Has(Facet.Text))
            return;
        // a caption's words are retimed with it, so its spoken word stays lit
        _editor.Editor.SetText(_editor.Editor.EditTargets(SelectedClipIds, Facet.Text), value);
        _editor.Preview.RefreshFrame();
    }

    partial void OnFontFamilyValueChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            SetOnSelection(Facet.Text, "font", c => ((TextClip)c).Style.FontFamily,
                (c, v) => ((TextClip)c).Style = ((TextClip)c).Style with { FontFamily = v }, value, style: true);
    }
}
