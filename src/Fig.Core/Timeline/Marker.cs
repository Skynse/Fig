using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Fig.Core.Timeline
{
    /// <summary>
    /// An editorial annotation pinned to a point (or short span) of a clip, track, or
    /// timeline. <see cref="StartSec"/> is relative to the object it sits on: for a clip
    /// marker it is an offset from the clip start; for track and timeline markers it is
    /// an absolute position on the timeline.
    /// </summary>
    public sealed class Marker
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "";
        public double StartSec { get; set; }
        public double DurSec { get; set; }
        public string Color { get; set; } = "#ffd60a";

        /// <summary>Optional source-format provenance (e.g. cmx_3600 reel / color).</summary>
        public Dictionary<string, JsonElement>? Metadata { get; set; }

        public Marker Clone()
        {
            return new Marker
            {
                Id = Guid.NewGuid().ToString(),
                Name = Name,
                StartSec = StartSec,
                DurSec = DurSec,
                Color = Color,
                Metadata = Metadata is null ? null : new Dictionary<string, JsonElement>(Metadata),
            };
        }
    }
}
