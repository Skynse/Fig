using System;
using System.Collections.Generic;
using System.Linq;

namespace Fig.Core.Timeline
{
    /// <summary>Puts a new clip on a track (and takes it off again on undo).</summary>
    public sealed class AddClipCommand : IEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _trackId;
        private readonly Clip _clip;

        public AddClipCommand(TimelineEditor editor, string trackId, Clip clip)
        {
            _editor = editor;
            _trackId = trackId;
            _clip = clip;
        }

        public string Description => $"Add {_clip.DisplayName.ToLowerInvariant()}";

        public void Execute()
        {
            var track = _editor.FindTrack(_trackId) ?? throw new InvalidOperationException($"Track '{_trackId}' not found");
            _editor.InsertClip(track, _clip);
        }

        public void Undo()
        {
            if (_editor.FindTrack(_trackId) is { } track)
                _editor.RemoveClip(track, _clip);
            _editor.Selection.Deselect(_clip.Id);
        }

        public void Redo() => Execute();
    }

    /// <summary>
    /// Positions layers (a gizmo drag) at the playhead: each position value that is animated on a clip gets a
    /// keyframe there, the others change the clip's layout. Undo puts back the layout and those keyframes; the
    /// steps of one drag coalesce into one undo step.
    /// </summary>
    public sealed class SetLayoutsCommand : ICoalescingEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly double _playheadSec;
        private IReadOnlyDictionary<string, LayerTransform> _targets;
        private readonly Dictionary<string, (LayerTransform Layout, Dictionary<string, List<KeyframePoint>?> Keys)> _before = new();

        public SetLayoutsCommand(TimelineEditor editor, IReadOnlyDictionary<string, LayerTransform> targets, double playheadSec)
        {
            _editor = editor;
            _targets = targets;
            _playheadSec = playheadSec;
        }

        public string Description => "Move layer";

        private string CoalesceKey => string.Join("|", _targets.Keys.Order()) + "@" + Math.Round(_playheadSec, 4);

        public void Execute()
        {
            _before.Clear();
            foreach (var id in _targets.Keys)
                if (_editor.FindClip(id) is ILayerClip layer and Clip clip)
                    _before[id] = (layer.Layout, ClipProperties.LayoutParts.ToDictionary(p => p.Property.Key,
                        p => clip.Keyframes.TryGetValue(p.Property.Key, out var keys) ? keys.ToList() : null));
            Apply();
        }

        public void Undo()
        {
            foreach (var (id, (layout, keys)) in _before)
            {
                if (_editor.FindClip(id) is not ILayerClip layer || layer is not Clip clip)
                    continue;
                layer.Layout = layout;
                foreach (var (key, points) in keys)
                {
                    if (points is null)
                        clip.Keyframes.Remove(key);
                    else
                        clip.Keyframes[key] = points.ToList();
                }
            }
        }

        public void Redo()
        {
            Undo();
            Apply();
        }

        public bool CanCoalesceWith(IEditCommand other) => other is SetLayoutsCommand o && o.CoalesceKey == CoalesceKey;

        public void CoalesceFrom(IEditCommand other)
        {
            _targets = ((SetLayoutsCommand)other)._targets;
            Redo();
        }

        private void Apply()
        {
            foreach (var (id, target) in _targets)
            {
                if (!_before.ContainsKey(id) || _editor.FindClip(id) is not { } clip)
                    continue;
                var local = Math.Clamp(_playheadSec - clip.StartSec, 0, clip.DurSec);
                foreach (var (property, of) in ClipProperties.LayoutParts)
                {
                    var value = Math.Clamp(of(target), property.Def.Min, property.Def.Max);
                    if (!property.IsAnimated(clip))
                    {
                        property.Set(clip, ParamValue.OfDouble(value));
                        continue;
                    }
                    var keys = clip.Keyframes[property.Key];
                    var i = keys.FindIndex(k => Math.Abs(k.TimeSec - local) < 1e-6);
                    if (i >= 0)
                        keys[i] = keys[i] with { Value = ParamValue.OfDouble(value) };
                    else
                    {
                        keys.Add(new KeyframePoint(local, ParamValue.OfDouble(value), KeyframeEasing.Linear));
                        keys.Sort((a, b) => a.TimeSec.CompareTo(b.TimeSec));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Sets a value on several clips at once (the same one, or one each), as one undo step; dragging a slider or
    /// a gizmo coalesces into the same step. Used when an inspector edit applies to everything selected.
    /// </summary>
    public sealed class SetClipValueCommand<T> : ICoalescingEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _property;
        private readonly Func<Clip, T> _get;
        private readonly Action<Clip, T> _set;
        private readonly Dictionary<string, T> _old = new();
        private Dictionary<string, T> _values;

        public SetClipValueCommand(TimelineEditor editor, IEnumerable<string> clipIds, string property,
            Func<Clip, T> get, Action<Clip, T> set, T value)
            : this(editor, clipIds.Distinct().ToDictionary(id => id, _ => value), property, get, set)
        {
        }

        public SetClipValueCommand(TimelineEditor editor, IReadOnlyDictionary<string, T> values, string property,
            Func<Clip, T> get, Action<Clip, T> set)
        {
            _editor = editor;
            _values = new Dictionary<string, T>(values);
            _property = property;
            _get = get;
            _set = set;
        }

        public string Description => $"Set {_property}";

        public void Execute()
        {
            _old.Clear();
            foreach (var (id, value) in _values)
                if (_editor.FindClip(id) is { } clip)
                {
                    _old[id] = _get(clip);
                    _set(clip, value);
                }
        }

        public void Undo()
        {
            foreach (var (id, value) in _old)
                if (_editor.FindClip(id) is { } clip)
                    _set(clip, value);
        }

        public void Redo() => Apply();

        public bool CanCoalesceWith(IEditCommand other)
            => other is SetClipValueCommand<T> o && o._property == _property
               && o._values.Count == _values.Count && o._values.Keys.All(_values.ContainsKey);

        public void CoalesceFrom(IEditCommand other)
        {
            if (other is not SetClipValueCommand<T> o)
                return;
            _values = new Dictionary<string, T>(o._values);
            Apply();
        }

        private void Apply()
        {
            foreach (var id in _old.Keys)
                if (_editor.FindClip(id) is { } clip && _values.TryGetValue(id, out var value))
                    _set(clip, value);
        }
    }
}

namespace Fig.Core.Timeline
{
    /// <summary>
    /// Takes clips off a track and puts others on it, as one undo step: a merge (two captions out, one in) or a split
    /// (one out, two in). The clips put on are new objects, so undo puts the very clips taken off back.
    /// </summary>
    public sealed class ReplaceClipsCommand(TimelineEditor editor, string trackId, string description,
        IReadOnlyList<Clip> remove, IReadOnlyList<Clip> add) : IEditCommand
    {
        public string Description => description;

        public void Execute() => Swap(remove, add);

        public void Undo() => Swap(add, remove);

        public void Redo() => Execute();

        private void Swap(IReadOnlyList<Clip> off, IReadOnlyList<Clip> on)
        {
            var track = editor.FindTrack(trackId) ?? throw new InvalidOperationException($"Track '{trackId}' not found");
            foreach (var clip in off)
                editor.RemoveClip(track, clip);
            foreach (var clip in on)
                editor.InsertClip(track, clip);
        }
    }
}
