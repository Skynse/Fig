using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>An active between-clip transition at a timeline time.</summary>
    public sealed class ActiveTransition
    {
        public required Clip Outgoing { get; init; }
        public required Clip Incoming { get; init; }
        public required string TypeId { get; init; }
        public required double Progress01 { get; init; }
        public required double DurationSec { get; init; }
        public required IReadOnlyDictionary<string, double> Params { get; init; }
        public required double CutSec { get; init; }
    }
}
