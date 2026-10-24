namespace Fig.Core.Timeline;

public partial class TimelineEditor
{
    /// <summary>Copies an explicit drag selection, preserving originals and the clipboard.</summary>
    public IReadOnlyList<Clip> DuplicateClipsAt(IReadOnlyCollection<string> ids, string anchorId,
        double anchorStartSec, string targetTrackId)
    {
        var anchor = FindClip(anchorId);
        var target = FindTrack(targetTrackId);
        var anchorTrack = anchor is null ? null : FindClipTrack(anchorId);
        if (anchor is null || target is null || anchorTrack is null || target.Kind != anchorTrack.Kind
            || !double.IsFinite(anchorStartSec)) return Array.Empty<Clip>();
        var sources = ids.Distinct().Select(FindClip).Where(c => c is not null).Cast<Clip>().ToArray();
        if (sources.Length == 0 || !sources.Contains(anchor)) return Array.Empty<Clip>();
        double delta = SnapTime(Math.Max(0, anchorStartSec)) - anchor.StartSec;
        delta = Math.Max(delta, -sources.Min(c => c.StartSec));
        var sameKind = Document.Tracks.Where(t => t.Kind == anchorTrack.Kind).ToList();
        int trackOffset = sameKind.IndexOf(target) - sameKind.IndexOf(anchorTrack);
        var tracks = Document.Tracks.ToList();
        var destinations = new Dictionary<string, Track>();
        var placements = new List<(Clip Source, Track Target, double Start)>();

        foreach (var source in sources)
        {
            var originalTrack = FindClipTrack(source.Id)!;
            double start = SnapTime(source.StartSec + delta);
            if (!destinations.TryGetValue(originalTrack.Id, out var destination))
            {
                if (originalTrack.Kind == anchorTrack.Kind)
                {
                    int index = sameKind.IndexOf(originalTrack) + trackOffset;
                    if (index < 0 || index >= sameKind.Count) return Array.Empty<Clip>();
                    destination = sameKind[index];
                }
                else
                {
                    // Linked audio needs a free lane when the video is copied above itself.
                    destination = tracks.Where(t => t.Kind == originalTrack.Kind)
                        .OrderBy(t => t.Id == originalTrack.Id ? 0 : 1)
                        .FirstOrDefault(t => sources.Where(c => FindClipTrack(c.Id)?.Id == originalTrack.Id)
                            .All(c => !WouldOverlap(t.Id, SnapTime(c.StartSec + delta), c.DurSec))
                            && placements.All(p => p.Target.Id != t.Id))!;
                    if (destination is null)
                    {
                        int count = tracks.Count(t => t.Kind == originalTrack.Kind);
                        destination = new Track { Kind = originalTrack.Kind, Name = $"{(originalTrack.Kind == TrackKind.Audio ? "A" : "V")}{count + 1}" };
                        tracks.Add(destination);
                    }
                }
                destinations[originalTrack.Id] = destination;
            }
            // the magnetic main track makes room instead (below)
            if (!IsMagnetic(destination) && (WouldOverlap(destination.Id, start, source.DurSec)
                || placements.Any(p => p.Target.Id == destination.Id
                    && p.Start < start + source.DurSec && start < p.Start + p.Source.DurSec)))
                return Array.Empty<Clip>();
            placements.Add((source, destination, start));
        }

        // copies for the magnetic main track go in at the nearest cut; everything copied with them shifts to match
        var main = MainTrack;
        var intoMain = placements.Where(p => IsMagnetic(p.Target)).ToList();
        if (intoMain.Count > 0)
        {
            var first = intoMain.Min(p => p.Start);
            var order = InTimeOrder(main!);
            var index = InsertIndex(order, first);
            var cut = index == 0 ? 0 : order[index - 1].StartSec + order[index - 1].DurSec;
            placements = placements.Select(p => (p.Source, p.Target, Math.Max(0, p.Start + cut - first))).ToList();
        }

        var commands = new List<IEditCommand>();
        if (tracks.Count != Document.Tracks.Count)
            commands.Add(new SetTrackListCommand(this, "Add copied audio tracks", Document.Tracks.ToList(), tracks));
        var groups = new Dictionary<string, string>();
        var copies = new List<Clip>();
        var mainCopies = new List<Clip>();
        foreach (var placement in placements)
        {
            var copy = ClipFactory.Clone(placement.Source);
            copy.TransitionIn = null;
            copy.TransitionOut = null;
            if (!string.IsNullOrEmpty(copy.LinkGroupId))
            {
                if (!groups.TryGetValue(copy.LinkGroupId, out var group))
                    groups[copy.LinkGroupId] = group = Guid.NewGuid().ToString("N");
                copy.LinkGroupId = group;
            }
            copy.StartSec = placement.Start;
            if (IsMagnetic(placement.Target))
                mainCopies.Add(copy);
            else
                commands.Add(new OverwriteInsertCommand(this, placement.Target.Id, copy, placement.Start));
            copies.Add(copy);
        }
        foreach (var copy in copies.Where(c => c.LinkGroupId is not null).GroupBy(c => c.LinkGroupId)
            .Where(group => group.Count() == 1).SelectMany(group => group))
            copy.LinkGroupId = null;
        using (History.Group())
        {
            // the main track makes room first, so the copies beside it land in the room it made
            if (mainCopies.Count > 0)
                EditLayout("Copy clips", () =>
                {
                    var order = InTimeOrder(main!);
                    var index = InsertIndex(order, mainCopies.Min(c => c.StartSec) + 1e-6);
                    foreach (var copy in mainCopies)
                        InsertClip(main!, copy);
                    order.InsertRange(index, mainCopies.OrderBy(c => c.StartSec));
                    PackTrack(main!, order);
                    return true;
                });
            if (commands.Count > 0)
                History.Execute(new CompositeCommand(commands.ToArray()));
        }
        Selection.SelectClips(copies.Select(c => c.Id));
        RaiseChanged();
        return copies;
    }

    private static bool IsAudioOwner(IEffectOwner owner) => owner is AudioClip || owner is Track { Kind: TrackKind.Audio };

    /// <summary>Copies an effect and shifts its range and keyframes into the destination clip.</summary>
    public EffectInstance? DuplicateEffectAt(string sourceId, string effectId, string targetId, double localDelta)
    {
        var source = FindEffectOwner(sourceId);
        var target = FindEffectOwner(targetId);
        var effect = source?.Effects.FirstOrDefault(fx => fx.Id == effectId);
        if (source is null || target is null || effect is null || EffectTimeLimit(target) <= 0
            || IsAudioOwner(source) != IsAudioOwner(target) || !double.IsFinite(localDelta)) return null;
        var copy = effect.Clone();
        if (effect.HasRange)
        {
            double duration = Math.Min(effect.DurSec!.Value, EffectTimeLimit(target));
            double start = Math.Clamp(effect.StartSec!.Value + localDelta, 0, EffectTimeLimit(target) - duration);
            localDelta = start - effect.StartSec.Value;
            copy.StartSec = start;
            copy.DurSec = duration;
        }
        else
        {
            var times = copy.Keyframes.Values.SelectMany(keys => keys).Select(key => key.TimeSec).ToArray();
            if (times.Length > 0 && times.Max() - times.Min() <= EffectTimeLimit(target))
                localDelta = Math.Clamp(localDelta, -times.Min(), EffectTimeLimit(target) - times.Max());
        }
        foreach (var key in copy.Keyframes.Keys.ToArray())
            copy.Keyframes[key] = copy.Keyframes[key]
                .Select(point => point with { TimeSec = Math.Clamp(point.TimeSec + localDelta, 0, EffectTimeLimit(target)) })
                .GroupBy(point => point.TimeSec).Select(group => group.Last()).OrderBy(point => point.TimeSec).ToList();
        AddEffect(target.Id, copy);
        return copy;
    }
}
