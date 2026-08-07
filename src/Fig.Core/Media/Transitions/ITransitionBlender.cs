using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>Blends two frames for a between-clip transition (t in 0..1).</summary>
    public interface ITransitionBlender
    {
        string TypeId { get; }

        DecodedFrame Blend(
            DecodedFrame outgoing,
            DecodedFrame incoming,
            double t01,
            IReadOnlyDictionary<string, ParamValue> parameters);
    }
}
