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
}
