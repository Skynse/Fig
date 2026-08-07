using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>
    /// The built-in effect catalog, discovered from every <see cref="IEffectProcessor"/>
    /// implementation in this assembly that carries an <see cref="EffectAttribute"/>.
    /// Adding an effect is one step — write the class with its descriptor and it is picked
    /// up here automatically: metadata for the UI (<see cref="All"/> / <see cref="Find"/>)
    /// and the processor for rendering (<see cref="Resolve"/>).
    /// </summary>
    public static class EffectCatalog
    {
        public const string Brightness = "brightness";
        public const string Grayscale = "grayscale";
        public const string Tint = "tint";
        public const string Contrast = "contrast";
        public const string Saturation = "saturation";
        public const string Hue = "hue";
        public const string Invert = "invert";
        public const string Sepia = "sepia";
        public const string Vignette = "vignette";
        public const string Sharpen = "sharpen";
        public const string Pixelate = "pixelate";
        public const string Flip = "flip";
        public const string Posterize = "posterize";

        private sealed record EffectEntry(Type Implementation, EffectCatalogEntry Metadata);

        private static readonly Lazy<IReadOnlyList<EffectEntry>> Entries = new(Discover);

        private static readonly Lazy<IReadOnlyList<EffectCatalogEntry>> Catalog =
            new(() => Entries.Value.Select(e => e.Metadata).ToList());

        private static readonly Lazy<Dictionary<string, IEffectProcessor>> Processors = new(() =>
        {
            var map = new Dictionary<string, IEffectProcessor>();
            foreach (var entry in Entries.Value)
                map[entry.Metadata.TypeId] = (IEffectProcessor)Activator.CreateInstance(entry.Implementation)!;
            return map;
        });

        public static IReadOnlyList<EffectCatalogEntry> All => Catalog.Value;

        public static EffectCatalogEntry? Find(string typeId)
        {
            foreach (var entry in All)
                if (entry.TypeId == typeId)
                    return entry;
            return null;
        }

        public static IEffectProcessor? Resolve(string typeId)
            => Processors.Value.TryGetValue(typeId, out var processor) ? processor : null;

        private static IReadOnlyList<EffectEntry> Discover()
        {
            var list = new List<EffectEntry>();
            var assembly = typeof(IEffectProcessor).Assembly;
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || type.IsInterface || !typeof(IEffectProcessor).IsAssignableFrom(type))
                    continue;
                var descriptor = type.GetCustomAttribute<EffectAttribute>();
                if (descriptor is null)
                    continue;

                var entry = new EffectCatalogEntry
                {
                    TypeId = descriptor.TypeId,
                    Kind = descriptor.Kind,
                    DisplayName = descriptor.DisplayName,
                    Icon = descriptor.Icon,
                    Description = descriptor.Description,
                    ParamSchema = type.GetCustomAttributes<EffectParamAttribute>()
                        .Select(p => new ParamDef
                        {
                            Key = p.Key,
                            Label = p.Label,
                            Kind = p.Kind,
                            Default = p.Default,
                            Min = p.Min,
                            Max = p.Max,
                            Choices = p.Choices,
                        })
                        .ToList(),
                };
                list.Add(new EffectEntry(type, entry));
            }
            return list.OrderBy(e => e.Metadata.TypeId).ToList();
        }
    }
}
