using System;
using System.Collections.Generic;

namespace Fig.Core.Timeline
{
    /// <summary>
    /// A resolved transition across an abutting cut. The underlying data lives on the
    /// clips as <see cref="Clip.TransitionOut"/> (left) and <see cref="Clip.TransitionIn"/>
    /// (right); the effective duration is the max of the two edges (matching the engine).
    /// </summary>
    public sealed record CutTransition(
        string LeftClipId,
        string RightClipId,
        Clip Left,
        Clip Right,
        string TypeId,
        double DurationSec,
        double CutSec)
    {
        public string Key => $"{LeftClipId}|{RightClipId}";
    }

    /// <summary>Removes the transition across an abutting cut (clears both clip edges).</summary>
    public sealed class RemoveTransitionCommand : IEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _leftClipId;
        private readonly string _rightClipId;
        private TransitionRef? _oldOut;
        private TransitionRef? _oldIn;
        private bool _done;

        public string Description => "Remove transition";

        public RemoveTransitionCommand(TimelineEditor editor, string leftClipId, string rightClipId)
        {
            _editor = editor;
            _leftClipId = leftClipId;
            _rightClipId = rightClipId;
        }

        public void Execute()
        {
            var left = _editor.FindClip(_leftClipId);
            var right = _editor.FindClip(_rightClipId);
            if (left is null || right is null || (left.TransitionOut is null && right.TransitionIn is null))
                return;
            _oldOut = left.TransitionOut?.Clone();
            _oldIn = right.TransitionIn?.Clone();
            left.TransitionOut = null;
            right.TransitionIn = null;
            _done = true;
        }

        public void Undo()
        {
            if (!_done)
                return;
            var left = _editor.FindClip(_leftClipId);
            var right = _editor.FindClip(_rightClipId);
            if (left is not null)
                left.TransitionOut = _oldOut?.Clone();
            if (right is not null)
                right.TransitionIn = _oldIn?.Clone();
        }

        public void Redo() => Execute();
    }

    /// <summary>Sets a typed parameter on a cut transition (writes both clip edges). Coalesces.</summary>
    public sealed class SetTransitionParamCommand : ICoalescingEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _leftClipId;
        private readonly string _rightClipId;
        private readonly string _key;
        private ParamValue _newValue;
        private ParamValue _oldValue;
        private bool _done;

        public string Description => $"Set transition param {_key}";
        public string CoalesceKey => $"{_leftClipId}|{_rightClipId}|{_key}";

        public SetTransitionParamCommand(TimelineEditor editor, string leftClipId, string rightClipId, string key, ParamValue newValue)
        {
            _editor = editor;
            _leftClipId = leftClipId;
            _rightClipId = rightClipId;
            _key = key;
            _newValue = newValue;
        }

        public void Execute()
        {
            if (Resolve() is not { } pair)
                return;
            _oldValue = pair.Any.Params.TryGetValue(_key, out var v) ? v : default;
            Write(pair, _key, _newValue);
            _done = true;
        }

        public void Undo()
        {
            if (!_done || Resolve() is not { } pair)
                return;
            Write(pair, _key, _oldValue);
        }

        public void Redo()
        {
            if (Resolve() is not { } pair)
                return;
            Write(pair, _key, _newValue);
        }

        public bool CanCoalesceWith(IEditCommand other)
            => other is SetTransitionParamCommand o && o.CoalesceKey == CoalesceKey;

        public void CoalesceFrom(IEditCommand other)
        {
            if (other is SetTransitionParamCommand o && o.CoalesceKey == CoalesceKey)
            {
                _newValue = o._newValue;
                Redo();
            }
        }

        private (TransitionRef Any, TransitionRef? Left, TransitionRef? Right)? Resolve()
        {
            var left = _editor.FindClip(_leftClipId);
            var right = _editor.FindClip(_rightClipId);
            if (left is null || right is null)
                return null;
            TransitionRef? any = left.TransitionOut ?? right.TransitionIn;
            return any is null ? null : (any, left.TransitionOut, right.TransitionIn);
        }

        private static void Write((TransitionRef Any, TransitionRef? Left, TransitionRef? Right) pair, string key, ParamValue value)
        {
            if (pair.Left is not null)
                pair.Left.Params[key] = value;
            if (pair.Right is not null)
                pair.Right.Params[key] = value;
        }
    }

    /// <summary>Resizes a cut transition by writing the same duration onto both clip edges.</summary>
    public sealed class SetTransitionDurationCommand : ICoalescingEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _leftClipId;
        private readonly string _rightClipId;
        private double _newValue;
        private TransitionRef? _oldOut;
        private TransitionRef? _oldIn;
        private bool _done;

        public string Description => "Resize transition";
        public string Key => $"{_leftClipId}|{_rightClipId}";

        public SetTransitionDurationCommand(TimelineEditor editor, string leftClipId, string rightClipId, double durationSec)
        {
            _editor = editor;
            _leftClipId = leftClipId;
            _rightClipId = rightClipId;
            _newValue = Math.Max(0, durationSec);
        }

        public void Execute()
        {
            var left = _editor.FindClip(_leftClipId);
            var right = _editor.FindClip(_rightClipId);
            if (left is null || right is null || (left.TransitionOut is null && right.TransitionIn is null))
                return;
            _oldOut = left.TransitionOut?.Clone();
            _oldIn = right.TransitionIn?.Clone();
            Apply(_newValue);
            _done = true;
        }

        public void Undo()
        {
            if (!_done)
                return;
            var left = _editor.FindClip(_leftClipId);
            var right = _editor.FindClip(_rightClipId);
            if (left is not null)
                left.TransitionOut = _oldOut?.Clone();
            if (right is not null)
                right.TransitionIn = _oldIn?.Clone();
        }

        public void Redo()
        {
            if (!_done)
                return;
            var left = _editor.FindClip(_leftClipId);
            var right = _editor.FindClip(_rightClipId);
            if (left is not null && right is not null)
                Apply(_newValue);
        }

        public bool CanCoalesceWith(IEditCommand other)
            => other is SetTransitionDurationCommand t && t.Key == Key;

        public void CoalesceFrom(IEditCommand other)
        {
            if (other is SetTransitionDurationCommand t)
            {
                _newValue = t._newValue;
                Redo();
            }
        }

        private void Apply(double durationSec)
        {
            var left = _editor.FindClip(_leftClipId);
            var right = _editor.FindClip(_rightClipId);
            if (left is null || right is null)
                return;
            if (left.TransitionOut is not null)
                left.TransitionOut.DurationSec = durationSec;
            if (right.TransitionIn is not null)
                right.TransitionIn.DurationSec = durationSec;
        }
    }
}
