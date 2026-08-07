using System.Collections.Generic;

namespace Fig.Core.Media
{
    /// <summary>Applies a video effect to a decoded BGRA frame.</summary>
    public interface IEffectProcessor
    {
        string TypeId { get; }

        /// <summary>
        /// Mutates or returns a new frame. <paramref name="localT"/> is seconds from clip start.
        /// Unknown/disabled effects are skipped by the pipeline before calling this.
        /// </summary>
        DecodedFrame Apply(DecodedFrame frame, IReadOnlyDictionary<string, double> parameters, double localT);
    }
}
