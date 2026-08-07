using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>Maps transition TypeIds to their blenders. Register new transitions here.</summary>
    /// To add a new transition, register it here, after writing the blender implementation.


    public static class TransitionRegistry
    {
        private static readonly Dictionary<string, ITransitionBlender> Blenders = new()
        {
            [TransitionCatalog.CrossDissolve] = new CrossDissolveBlender(),
            [TransitionCatalog.Wipe] = new WipeBlender(),
        };

        public static ITransitionBlender? Resolve(string typeId)
            => Blenders.TryGetValue(typeId, out var b) ? b : null;
    }
}
