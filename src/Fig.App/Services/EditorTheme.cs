using Avalonia.Media;

namespace Fig.App.Services;

/// <summary>Shared editor colors for AXAML and custom-drawn controls.</summary>
public static class EditorTheme
{
    public static readonly Color Surface = Color.Parse("#1e1e1e");
    public static readonly Color Card = Color.Parse("#252526");
    public static readonly Color Border = Color.Parse("#333333");
    public static readonly Color TextPrimary = Color.Parse("#cccccc");
    public static readonly Color TextMuted = Color.Parse("#8a8a8a");
    public static readonly Color Accent = Color.Parse("#4da3ff");
    public static readonly Color Playhead = Color.Parse("#e8e8e8");
    public static readonly Color TrackLane = Color.Parse("#252526");
    public static readonly Color TrackLaneAlt = Color.Parse("#2a2a2b");
    public static readonly Color RulerBackground = Color.Parse("#1a1a1a");
    public static readonly Color SelectedLaneTint = Color.FromArgb(28, 0x4d, 0xa3, 0xff);

    public static readonly IBrush SurfaceBrush = new SolidColorBrush(Surface);
    public static readonly IBrush CardBrush = new SolidColorBrush(Card);
    public static readonly IBrush BorderBrush = new SolidColorBrush(Border);
    public static readonly IBrush TextPrimaryBrush = new SolidColorBrush(TextPrimary);
    public static readonly IBrush TextMutedBrush = new SolidColorBrush(TextMuted);
    public static readonly IBrush AccentBrush = new SolidColorBrush(Accent);
    public static readonly IBrush PlayheadBrush = new SolidColorBrush(Playhead);
}
