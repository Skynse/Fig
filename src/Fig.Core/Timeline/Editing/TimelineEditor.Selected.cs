using System.Collections.Generic;
using System.Linq;

namespace Fig.Core.Timeline
{
    public partial class TimelineEditor
    {
        /// <summary>
        /// The live object behind a selection key, or null when it no longer exists (deleted since it was
        /// selected). How each group is stored is in <see cref="SelectionGroups"/>.
        /// </summary>
        public ISelectable? Resolve(SelectionKey key) => SelectionGroups.Of(key.Group).Resolve(this, key);

        internal EffectTarget? ResolveEffectTarget(SelectionKey key)
        {
            if (key.OwnerId is null || FindEffectOwner(key.OwnerId) is not { } owner)
                return null;
            return owner.Effects.FirstOrDefault(e => e.Id == key.Id) is { } effect ? new EffectTarget(owner, effect) : null;
        }

        /// <summary>
        /// Deletes what is selected, whatever it is: each kind of thing says how (see <see cref="SelectionGroups"/>).
        /// Returns what was removed (a count and the noun for the message), or null when nothing could be.
        /// </summary>
        public (int Count, string Noun)? DeleteSelection() => KeepMainTrackClosed(DeleteSelectionLeavingGaps);

        private (int Count, string Noun)? DeleteSelectionLeavingGaps()
        {
            // a picked keyframe diamond goes, not the clip or effect it sits on
            if (Selection.Keyframe is { } keyframe)
            {
                RemoveKeyframes(keyframe);
                Selection.ClearKeyframe();
                return (1, "keyframe");
            }
            var keys = Selection.Items;
            if (keys.Count == 0)
                return null;
            // things that sit on clips and tracks go before the clips and tracks that hold them
            var groups = keys.GroupBy(k => k.Group)
                .Where(g => SelectionGroups.Of(g.Key).Delete is not null)
                .OrderBy(g => DeleteOrder.IndexOf(g.Key))
                .ToList();
            if (groups.Count == 0)
                return null;

            var total = 0;
            var nouns = new List<string>();
            using (History.Group())
            {
                foreach (var group in groups)
                {
                    var behavior = SelectionGroups.Of(group.Key);
                    // each group's delete sees just its own items selected (some read the selection)
                    Selection.Replace(group);
                    var removed = behavior.Delete!(this, group.ToList());
                    total += removed;
                    if (removed > 0)
                        nouns.Add(behavior.Noun);
                }
            }
            Selection.Clear();
            return total > 0 ? (total, nouns.Count == 1 ? nouns[0] : "item") : null;
        }

        /// <summary>Selects everything on the timeline: every clip, every effect bar (on tracks and clips) and every marker.</summary>
        public void SelectAll()
        {
            var keys = new List<SelectionKey>();
            foreach (var track in Document.Tracks)
            {
                keys.AddRange(track.Effects.Select(fx => new SelectionKey(SelectionGroup.Effect, fx.Id, track.Id)));
                foreach (var clip in track.Clips)
                    keys.AddRange(clip.Effects.Select(fx => new SelectionKey(SelectionGroup.Effect, fx.Id, clip.Id)));
            }
            keys.AddRange(AllMarkers().Select(m => new SelectionKey(SelectionGroup.Marker, m.Marker.Id)));
            // clips last, so the primary item (what single-item panels show) is a clip
            keys.AddRange(Document.Tracks.SelectMany(t => t.Clips).Select(c => c.Key));
            Selection.Replace(keys);
        }

        private static readonly List<SelectionGroup> DeleteOrder =
        [
            SelectionGroup.Effect, SelectionGroup.Marker, SelectionGroup.Transition,
            SelectionGroup.Clip, SelectionGroup.Track, SelectionGroup.Media,
        ];

        /// <summary>What the status bar says about the selection, and a hint for what to do next.</summary>
        public (string Summary, string Hint) DescribeSelection()
        {
            var items = Selected;
            if (items.Count == 0)
                return ("Nothing selected", "Click a clip to select it · drag empty space to box-select · Space plays · M adds a marker");
            var groups = items.GroupBy(item => item.Key.Group).ToList();
            if (groups.Count == 1)
            {
                var behavior = SelectionGroups.Of(groups[0].Key);
                return (behavior.Summary(items), behavior.Hint(items.Count));
            }
            // a mixed selection: say what is in it, group by group
            var summary = string.Join(" · ", groups.Select(g => SelectionGroups.Of(g.Key).Summary(g.ToList())));
            return (summary, "Delete removes everything selected · Ctrl-click adds or removes · Esc clears");
        }

        /// <summary>Everything selected that still exists, in the order it was picked.</summary>
        public IReadOnlyList<ISelectable> Selected => Selection.Items.Select(Resolve).OfType<ISelectable>().ToList();

        /// <summary>The selected items that are of a given type (for the few edits that only make sense on one).</summary>
        public IEnumerable<T> SelectedOf<T>() where T : class, ISelectable => Selected.OfType<T>();

        /// <summary>
        /// What every selected item can be edited as: the facets they all have. An empty selection has none.
        /// </summary>
        public Facet SharedFacets
        {
            get
            {
                var selected = Selected;
                if (selected.Count == 0)
                    return Facet.None;
                return selected.Select(s => s.Facets).Aggregate((a, b) => a & b);
            }
        }
    }
}
