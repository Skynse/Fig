using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Fig.Core.Timeline
{
    /// <summary>One effect in a clip's ordered filter stack.</summary>
    public sealed class EffectInstance
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string TypeId { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public int Order { get; set; }

        /// <summary>Numeric parameters keyed by name (schema lives in the catalog).</summary>
        public Dictionary<string, double> Params { get; set; } = new();

        public EffectInstance Clone()
        {
            return new EffectInstance
            {
                Id = Guid.NewGuid().ToString(),
                TypeId = TypeId,
                Enabled = Enabled,
                Order = Order,
                Params = new Dictionary<string, double>(Params),
            };
        }

        /// <summary>Clone preserving the same id (for undo snapshots / clip clone).</summary>
        public EffectInstance CloneKeepId()
        {
            return new EffectInstance
            {
                Id = Id,
                TypeId = TypeId,
                Enabled = Enabled,
                Order = Order,
                Params = new Dictionary<string, double>(Params),
            };
        }
    }

    /// <summary>Optional transition attached to a clip edge (library / between-clip path).</summary>
    public sealed class TransitionRef
    {
        public string TypeId { get; set; } = "";
        public double DurationSec { get; set; } = 0.5;
        public Dictionary<string, double> Params { get; set; } = new();

        public TransitionRef Clone()
        {
            return new TransitionRef
            {
                TypeId = TypeId,
                DurationSec = DurationSec,
                Params = new Dictionary<string, double>(Params),
            };
        }
    }

    public enum EffectKind
    {
        Video,
        Audio,
        Both,
    }

    public sealed class ParamDef
    {
        public string Key { get; init; } = "";
        public string Label { get; init; } = "";
        public double Default { get; init; }
        public double Min { get; init; }
        public double Max { get; init; }
    }

    public sealed class EffectCatalogEntry
    {
        public string TypeId { get; init; } = "";
        public EffectKind Kind { get; init; }
        public string DisplayName { get; init; } = "";
        public string Icon { get; init; } = "wand-sparkles";
        public string Description { get; init; } = "";
        public IReadOnlyList<ParamDef> ParamSchema { get; init; } = Array.Empty<ParamDef>();

        public Dictionary<string, double> DefaultParams()
        {
            var map = new Dictionary<string, double>();
            foreach (var p in ParamSchema)
                map[p.Key] = p.Default;
            return map;
        }

        public EffectInstance CreateInstance()
        {
            return new EffectInstance
            {
                TypeId = TypeId,
                Enabled = true,
                Params = DefaultParams(),
            };
        }
    }

    public sealed class TransitionCatalogEntry
    {
        public string TypeId { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Icon { get; init; } = "blend";
        public string Description { get; init; } = "";
        public double DefaultDurationSec { get; init; } = 0.5;
        public IReadOnlyList<ParamDef> ParamSchema { get; init; } = Array.Empty<ParamDef>();

        public Dictionary<string, double> DefaultParams()
        {
            var map = new Dictionary<string, double>();
            foreach (var p in ParamSchema)
                map[p.Key] = p.Default;
            return map;
        }

        public TransitionRef CreateRef(double? durationSec = null)
        {
            return new TransitionRef
            {
                TypeId = TypeId,
                DurationSec = durationSec ?? DefaultDurationSec,
                Params = DefaultParams(),
            };
        }
    }

    /// <summary>Built-in effect catalog (metadata only — processors live in Media).</summary>
    public static class EffectCatalog
    {
        public const string Brightness = "brightness";
        public const string Grayscale = "grayscale";

        public static IReadOnlyList<EffectCatalogEntry> All { get; } =
        [
            new EffectCatalogEntry
            {
                TypeId = Brightness,
                Kind = EffectKind.Video,
                DisplayName = "Brightness",
                Icon = "sun",
                Description = "Lift or crush midtones.",
                ParamSchema =
                [
                    new ParamDef { Key = "amount", Label = "Amount", Default = 0.15, Min = -1, Max = 1 },
                ],
            },
            new EffectCatalogEntry
            {
                TypeId = Grayscale,
                Kind = EffectKind.Video,
                DisplayName = "Grayscale",
                Icon = "contrast",
                Description = "Desaturate to monochrome.",
                ParamSchema =
                [
                    new ParamDef { Key = "amount", Label = "Amount", Default = 1, Min = 0, Max = 1 },
                ],
            },
        ];

        public static EffectCatalogEntry? Find(string typeId)
        {
            foreach (var e in All)
                if (e.TypeId == typeId)
                    return e;
            return null;
        }
    }

    /// <summary>Built-in transition catalog (metadata only — blenders live in Media).</summary>
    public static class TransitionCatalog
    {
        public const string CrossDissolve = "cross-dissolve";
        public const string Wipe = "wipe";

        public static IReadOnlyList<TransitionCatalogEntry> All { get; } =
        [
            new TransitionCatalogEntry
            {
                TypeId = CrossDissolve,
                DisplayName = "Cross Dissolve",
                Icon = "blend",
                Description = "Blend outgoing and incoming clips over the cut.",
                DefaultDurationSec = 0.5,
            },
            new TransitionCatalogEntry
            {
                TypeId = Wipe,
                DisplayName = "Wipe",
                Icon = "arrow-right-left",
                Description = "Sweep the incoming clip in from the left.",
                DefaultDurationSec = 0.5,
                ParamSchema =
                [
                    new ParamDef { Key = "soft", Label = "Soft edge", Default = 0.1, Min = 0, Max = 0.5 },
                ],
            },
        ];

        public static TransitionCatalogEntry? Find(string typeId)
        {
            foreach (var e in All)
                if (e.TypeId == typeId)
                    return e;
            return null;
        }
    }
}
