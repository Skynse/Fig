using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>Maps effect TypeIds to their processors. Register new effects here.</summary>
    public static class EffectRegistry
    {
        private static readonly Dictionary<string, IEffectProcessor> Processors = new()
        {
            [EffectCatalog.Brightness] = new BrightnessEffect(),
            [EffectCatalog.Grayscale] = new GrayscaleEffect(),
        };

        public static IEffectProcessor? Resolve(string typeId)
            => Processors.TryGetValue(typeId, out var p) ? p : null;
    }
}
