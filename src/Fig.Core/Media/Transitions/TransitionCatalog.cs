using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>
    /// The built-in transition catalog, discovered from every <see cref="ITransitionBlender"/>
    /// implementation in this assembly that carries a <see cref="TransitionAttribute"/>.
    /// Adding a transition is one step — write the class with its descriptor and it is picked
    /// up here automatically: metadata for the UI (<see cref="All"/> / <see cref="Find"/>)
    /// and the blender for rendering (<see cref="Resolve"/>).
    /// </summary>
    public static class TransitionCatalog
    {
        public const string CrossDissolve = "cross-dissolve";
        public const string Wipe = "wipe";
        public const string Slide = "slide";
        public const string Push = "push";
        public const string FadeToBlack = "fade-to-black";
        public const string Iris = "iris";
        public const string Curtain = "curtain";

        private sealed record TransitionEntry(Type Implementation, TransitionCatalogEntry Metadata);

        private static readonly Lazy<IReadOnlyList<TransitionEntry>> Entries = new(Discover);

        private static readonly Lazy<IReadOnlyList<TransitionCatalogEntry>> Catalog =
            new(() => Entries.Value.Select(e => e.Metadata).ToList());

        private static readonly Lazy<Dictionary<string, ITransitionBlender>> Blenders = new(() =>
        {
            var map = new Dictionary<string, ITransitionBlender>();
            foreach (var entry in Entries.Value)
                map[entry.Metadata.TypeId] = (ITransitionBlender)Activator.CreateInstance(entry.Implementation)!;
            return map;
        });

        public static IReadOnlyList<TransitionCatalogEntry> All => Catalog.Value;

        public static TransitionCatalogEntry? Find(string typeId)
        {
            foreach (var entry in All)
                if (entry.TypeId == typeId)
                    return entry;
            return null;
        }

        public static ITransitionBlender? Resolve(string typeId)
            => Blenders.Value.TryGetValue(typeId, out var blender) ? blender : null;

        private static IReadOnlyList<TransitionEntry> Discover()
        {
            var list = new List<TransitionEntry>();
            var assembly = typeof(ITransitionBlender).Assembly;
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || type.IsInterface || !typeof(ITransitionBlender).IsAssignableFrom(type))
                    continue;
                var descriptor = type.GetCustomAttribute<TransitionAttribute>();
                if (descriptor is null)
                    continue;

                var entry = new TransitionCatalogEntry
                {
                    TypeId = descriptor.TypeId,
                    DisplayName = descriptor.DisplayName,
                    Icon = descriptor.Icon,
                    Description = descriptor.Description,
                    DefaultDurationSec = descriptor.DefaultDurationSec,
                    ParamSchema = type.GetCustomAttributes<TransitionParamAttribute>()
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
                list.Add(new TransitionEntry(type, entry));
            }
            return list.OrderBy(e => e.Metadata.TypeId).ToList();
        }
    }
}
