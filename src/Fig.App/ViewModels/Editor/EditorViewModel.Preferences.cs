using Fig.App.Services;

namespace Fig.App.ViewModels;

/// <summary>
/// The editor's toggles that the user expects to stay as they left them (snap, link, loop, audio scrub) are
/// preferences: they start from <see cref="Preferences"/>, and flipping one saves it.
/// </summary>
public partial class EditorViewModel
{
    private bool _applyingPreferences;

    private Fig.App.Rendering.SkiaLayerRasterizer? _layers;

    /// <summary>Draws text and image clips for the preview and for export (one cache shared by both).</summary>
    public Fig.App.Rendering.SkiaLayerRasterizer Layers => _layers ??= new Fig.App.Rendering.SkiaLayerRasterizer(FindAssetById);

    /// <summary>The user's preferences, shared with the rest of the app.</summary>
    public PreferenceService Preferences { get; private set; } = new();

    private void AttachPreferences(PreferenceService preferences)
    {
        Preferences = preferences;
        ApplyPreferences();
        Preferences.Changed += ApplyPreferences;
    }

    /// <summary>Reads the preferences into the editor's own state (at start-up, and after any change).</summary>
    private void ApplyPreferences()
    {
        _applyingPreferences = true;
        try
        {
            var p = Preferences.Current;
            MagneticSnap = p.Editing.Snap;
            LinkedSelection = p.Editing.LinkedSelection;
            AudioScrub = p.Playback.AudioScrub;
            LoopPlayback = p.Playback.Loop;
            CaptionStyleToAll = p.Captions.StyleAll;
            if (Preview is not null)
                Preview.Renderer = p.Rendering.Renderer;
        }
        finally
        {
            _applyingPreferences = false;
        }
    }

    /// <summary>Writes a toggle the user just flipped back to the preferences.</summary>
    private void Remember(Action<Fig.App.Services.Preferences> change)
    {
        if (_applyingPreferences)
            return;
        change(Preferences.Current);
        Preferences.Save();
    }

    partial void OnLinkedSelectionChanged(bool value) => Remember(p => p.Editing.LinkedSelection = value);

    partial void OnLoopPlaybackChanged(bool value) => Remember(p => p.Playback.Loop = value);
}
