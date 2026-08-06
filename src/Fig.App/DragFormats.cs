using Avalonia.Input;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.App;

/// <summary>
/// Shared drag-drop format keys. Defining them once (not per-view) ensures the sender's
/// and receiver's format objects are the same reference, without which Avalonia's
/// DataFormat equality check would reject matching payloads.
/// </summary>
public static class DragFormats
{
    public static readonly DataFormat<MediaAsset> Media =
        DataFormat<MediaAsset>.CreateInProcessFormat<MediaAsset>("fig.media");

    public static readonly DataFormat<EffectCatalogEntry> Effect =
        DataFormat<EffectCatalogEntry>.CreateInProcessFormat<EffectCatalogEntry>("fig.effect");

    public static readonly DataFormat<TransitionCatalogEntry> Transition =
        DataFormat<TransitionCatalogEntry>.CreateInProcessFormat<TransitionCatalogEntry>("fig.transition");
}
