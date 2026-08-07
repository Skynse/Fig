using System;
using System.Collections.Generic;
using System.Linq;

namespace Fig.Core.Timeline
{
    /// <summary>Adds an effect instance to a clip's stack.</summary>
    public sealed class AddEffectCommand : IEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;
        private readonly EffectInstance _effect;
        private bool _added;

        public string Description => $"Add effect {_effect.TypeId}";

        public AddEffectCommand(TimelineEditor editor, string clipId, EffectInstance effect)
        {
            _editor = editor;
            _clipId = clipId;
            _effect = effect;
        }

        public void Execute()
        {
            var clip = _editor.FindClip(_clipId)
                ?? throw new InvalidOperationException($"Clip '{_clipId}' not found");
            if (clip.Effects.Any(e => e.Id == _effect.Id))
                return;
            _effect.Order = clip.Effects.Count == 0 ? 0 : clip.Effects.Max(e => e.Order) + 1;
            clip.Effects.Add(_effect);
            _added = true;
        }

        public void Undo()
        {
            if (!_added)
                return;
            var clip = _editor.FindClip(_clipId);
            if (clip is null)
                return;
            clip.Effects.RemoveAll(e => e.Id == _effect.Id);
        }

        public void Redo() => Execute();
    }

    /// <summary>Removes an effect from a clip by id.</summary>
    public sealed class RemoveEffectCommand : IEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;
        private readonly string _effectId;
        private EffectInstance? _removed;
        private int _index;

        public string Description => "Remove effect";

        public RemoveEffectCommand(TimelineEditor editor, string clipId, string effectId)
        {
            _editor = editor;
            _clipId = clipId;
            _effectId = effectId;
        }

        public void Execute()
        {
            var clip = _editor.FindClip(_clipId)
                ?? throw new InvalidOperationException($"Clip '{_clipId}' not found");
            _index = clip.Effects.FindIndex(e => e.Id == _effectId);
            if (_index < 0)
                throw new InvalidOperationException($"Effect '{_effectId}' not found");
            _removed = clip.Effects[_index];
            clip.Effects.RemoveAt(_index);
        }

        public void Undo()
        {
            if (_removed is null)
                return;
            var clip = _editor.FindClip(_clipId);
            if (clip is null)
                return;
            var i = Math.Clamp(_index, 0, clip.Effects.Count);
            clip.Effects.Insert(i, _removed);
        }

        public void Redo() => Execute();
    }

    /// <summary>Sets or clears transition-in on a clip.</summary>
    public sealed class SetTransitionInCommand : IEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;
        private readonly TransitionRef? _newValue;
        private TransitionRef? _oldValue;
        private bool _hasOld;

        public string Description => _newValue is null ? "Clear transition in" : $"Set transition in ({_newValue.TypeId})";

        public SetTransitionInCommand(TimelineEditor editor, string clipId, TransitionRef? value)
        {
            _editor = editor;
            _clipId = clipId;
            _newValue = value?.Clone();
        }

        public void Execute()
        {
            var clip = _editor.FindClip(_clipId)
                ?? throw new InvalidOperationException($"Clip '{_clipId}' not found");
            _oldValue = clip.TransitionIn?.Clone();
            _hasOld = true;
            clip.TransitionIn = _newValue?.Clone();
        }

        public void Undo()
        {
            if (!_hasOld)
                return;
            var clip = _editor.FindClip(_clipId);
            if (clip is not null)
                clip.TransitionIn = _oldValue?.Clone();
        }

        public void Redo() => Execute();
    }

    /// <summary>Sets or clears transition-out on a clip.</summary>
    public sealed class SetTransitionOutCommand : IEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;
        private readonly TransitionRef? _newValue;
        private TransitionRef? _oldValue;
        private bool _hasOld;

        public string Description => _newValue is null ? "Clear transition out" : $"Set transition out ({_newValue.TypeId})";

        public SetTransitionOutCommand(TimelineEditor editor, string clipId, TransitionRef? value)
        {
            _editor = editor;
            _clipId = clipId;
            _newValue = value?.Clone();
        }

        public void Execute()
        {
            var clip = _editor.FindClip(_clipId)
                ?? throw new InvalidOperationException($"Clip '{_clipId}' not found");
            _oldValue = clip.TransitionOut?.Clone();
            _hasOld = true;
            clip.TransitionOut = _newValue?.Clone();
        }

        public void Undo()
        {
            if (!_hasOld)
                return;
            var clip = _editor.FindClip(_clipId);
            if (clip is not null)
                clip.TransitionOut = _oldValue?.Clone();
        }

        public void Redo() => Execute();
    }

    /// <summary>
    /// Applies a catalog transition across an abutting cut: transitionOut on left,
    /// transitionIn on right (same type/duration).
    /// </summary>
    public sealed class ApplyTransitionAtCutCommand : IEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _outgoingClipId;
        private readonly string _incomingClipId;
        private readonly TransitionRef _ref;
        private TransitionRef? _oldOut;
        private TransitionRef? _oldIn;
        private bool _done;

        public string Description => $"Apply {_ref.TypeId} at cut";

        public ApplyTransitionAtCutCommand(
            TimelineEditor editor,
            string outgoingClipId,
            string incomingClipId,
            TransitionRef transition)
        {
            _editor = editor;
            _outgoingClipId = outgoingClipId;
            _incomingClipId = incomingClipId;
            _ref = transition.Clone();
        }

        public void Execute()
        {
            var left = _editor.FindClip(_outgoingClipId)
                ?? throw new InvalidOperationException($"Clip '{_outgoingClipId}' not found");
            var right = _editor.FindClip(_incomingClipId)
                ?? throw new InvalidOperationException($"Clip '{_incomingClipId}' not found");
            _oldOut = left.TransitionOut?.Clone();
            _oldIn = right.TransitionIn?.Clone();
            left.TransitionOut = _ref.Clone();
            right.TransitionIn = _ref.Clone();
            _done = true;
        }

        public void Undo()
        {
            if (!_done)
                return;
            var left = _editor.FindClip(_outgoingClipId);
            var right = _editor.FindClip(_incomingClipId);
            if (left is not null)
                left.TransitionOut = _oldOut?.Clone();
            if (right is not null)
                right.TransitionIn = _oldIn?.Clone();
        }

        public void Redo() => Execute();
    }

    /// <summary>Sets one typed parameter on an effect. Slider drags coalesce into one undo step.</summary>
    public sealed class SetEffectParamCommand : ICoalescingEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;
        private readonly string _effectId;
        private readonly string _key;
        private ParamValue _newValue;
        private ParamValue _oldValue;
        private bool _done;

        public string Description => $"Set effect param {_key}";
        public string CoalesceKey => $"{_clipId}|{_effectId}|{_key}";

        public SetEffectParamCommand(TimelineEditor editor, string clipId, string effectId, string key, ParamValue newValue)
        {
            _editor = editor;
            _clipId = clipId;
            _effectId = effectId;
            _key = key;
            _newValue = newValue;
        }

        public void Execute()
        {
            if (FindEffect() is not { } effect || !effect.Params.ContainsKey(_key))
                return;
            _oldValue = effect.Params[_key];
            effect.Params[_key] = _newValue;
            _done = true;
        }

        public void Undo()
        {
            if (!_done)
                return;
            var effect = FindEffect();
            if (effect is not null && effect.Params.ContainsKey(_key))
                effect.Params[_key] = _oldValue;
        }

        public void Redo()
        {
            var effect = FindEffect();
            if (effect is not null && effect.Params.ContainsKey(_key))
                effect.Params[_key] = _newValue;
        }

        public bool CanCoalesceWith(IEditCommand other)
            => other is SetEffectParamCommand o && o.CoalesceKey == CoalesceKey;

        public void CoalesceFrom(IEditCommand other)
        {
            if (other is SetEffectParamCommand o && o.CoalesceKey == CoalesceKey)
            {
                _newValue = o._newValue;
                Redo();
            }
        }

        private EffectInstance? FindEffect()
        {
            var clip = _editor.FindClip(_clipId);
            if (clip is null)
                return null;
            foreach (var effect in clip.Effects)
                if (effect.Id == _effectId)
                    return effect;
            return null;
        }
    }

    /// <summary>Flips an effect's enabled flag.</summary>
    public sealed class ToggleEffectCommand : IEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;
        private readonly string _effectId;
        private bool _old;
        private bool _done;

        public string Description => "Toggle effect";

        public ToggleEffectCommand(TimelineEditor editor, string clipId, string effectId)
        {
            _editor = editor;
            _clipId = clipId;
            _effectId = effectId;
        }

        public void Execute()
        {
            if (FindEffect() is not { } effect)
                return;
            _old = effect.Enabled;
            effect.Enabled = !effect.Enabled;
            _done = true;
        }

        public void Undo()
        {
            if (!_done)
                return;
            if (FindEffect() is { } effect)
                effect.Enabled = _old;
        }

        public void Redo() => Execute();

        private EffectInstance? FindEffect()
        {
            var clip = _editor.FindClip(_clipId);
            if (clip is null)
                return null;
            foreach (var effect in clip.Effects)
                if (effect.Id == _effectId)
                    return effect;
            return null;
        }
    }

    /// <summary>Adds or updates a keyframe on an effect parameter at a clip-relative time.</summary>
    public sealed class SetKeyframeCommand : IEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;
        private readonly string _effectId;
        private readonly string _key;
        private readonly double _timeSec;
        private readonly ParamValue _value;
        private List<KeyframePoint>? _savedTrack;
        private bool _done;

        public string Description => "Set keyframe";

        public SetKeyframeCommand(TimelineEditor editor, string clipId, string effectId, string key, double timeSec, ParamValue value)
        {
            _editor = editor;
            _clipId = clipId;
            _effectId = effectId;
            _key = key;
            _timeSec = timeSec;
            _value = value;
        }

        public void Execute()
        {
            var effect = FindEffect();
            if (effect is null)
                return;
            _savedTrack = effect.Keyframes.TryGetValue(_key, out var existing)
                ? new List<KeyframePoint>(existing)
                : null;
            var track = effect.Keyframes.TryGetValue(_key, out var t) ? t : (effect.Keyframes[_key] = new List<KeyframePoint>());
            Upsert(track, new KeyframePoint(_timeSec, _value));
            _done = true;
        }

        public void Undo()
        {
            if (!_done || FindEffect() is not { } effect)
                return;
            if (_savedTrack is null)
                effect.Keyframes.Remove(_key);
            else
                effect.Keyframes[_key] = _savedTrack;
        }

        public void Redo() => Execute();

        private static void Upsert(List<KeyframePoint> track, KeyframePoint point)
        {
            for (var i = 0; i < track.Count; i++)
            {
                if (Math.Abs(track[i].TimeSec - point.TimeSec) < 1e-6)
                {
                    track[i] = point;
                    return;
                }
                if (track[i].TimeSec > point.TimeSec)
                {
                    track.Insert(i, point);
                    return;
                }
            }
            track.Add(point);
        }

        private EffectInstance? FindEffect()
        {
            var clip = _editor.FindClip(_clipId);
            if (clip is null)
                return null;
            foreach (var effect in clip.Effects)
                if (effect.Id == _effectId)
                    return effect;
            return null;
        }
    }

    /// <summary>Removes the keyframe nearest a clip-relative time on an effect parameter.</summary>
    public sealed class RemoveKeyframeCommand : IEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;
        private readonly string _effectId;
        private readonly string _key;
        private readonly double _timeSec;
        private List<KeyframePoint>? _savedTrack;
        private bool _done;

        public string Description => "Remove keyframe";

        public RemoveKeyframeCommand(TimelineEditor editor, string clipId, string effectId, string key, double timeSec)
        {
            _editor = editor;
            _clipId = clipId;
            _effectId = effectId;
            _key = key;
            _timeSec = timeSec;
        }

        public void Execute()
        {
            var effect = FindEffect();
            if (effect is null || !effect.Keyframes.TryGetValue(_key, out var track))
                return;
            for (var i = 0; i < track.Count; i++)
            {
                if (Math.Abs(track[i].TimeSec - _timeSec) < 1e-6)
                {
                    _savedTrack = new List<KeyframePoint>(track);
                    track.RemoveAt(i);
                    if (track.Count == 0)
                        effect.Keyframes.Remove(_key);
                    _done = true;
                    return;
                }
            }
        }

        public void Undo()
        {
            if (!_done || FindEffect() is not { } effect || _savedTrack is null)
                return;
            effect.Keyframes[_key] = _savedTrack;
        }

        public void Redo() => Execute();

        private EffectInstance? FindEffect()
        {
            var clip = _editor.FindClip(_clipId);
            if (clip is null)
                return null;
            foreach (var effect in clip.Effects)
                if (effect.Id == _effectId)
                    return effect;
            return null;
        }
    }

    /// <summary>Clears every keyframe on one effect parameter.</summary>
    public sealed class ClearKeyframesCommand : IEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;
        private readonly string _effectId;
        private readonly string _key;
        private List<KeyframePoint>? _savedTrack;
        private bool _done;

        public string Description => "Clear keyframes";

        public ClearKeyframesCommand(TimelineEditor editor, string clipId, string effectId, string key)
        {
            _editor = editor;
            _clipId = clipId;
            _effectId = effectId;
            _key = key;
        }

        public void Execute()
        {
            var effect = FindEffect();
            if (effect is null || !effect.Keyframes.ContainsKey(_key))
                return;
            _savedTrack = new List<KeyframePoint>(effect.Keyframes[_key]);
            effect.Keyframes.Remove(_key);
            _done = true;
        }

        public void Undo()
        {
            if (!_done || FindEffect() is not { } effect || _savedTrack is null)
                return;
            effect.Keyframes[_key] = _savedTrack;
        }

        public void Redo() => Execute();

        private EffectInstance? FindEffect()
        {
            var clip = _editor.FindClip(_clipId);
            if (clip is null)
                return null;
            foreach (var effect in clip.Effects)
                if (effect.Id == _effectId)
                    return effect;
            return null;
        }
    }
}
