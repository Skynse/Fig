using System.Collections.Generic;

namespace Fig.Core.Timeline
{
    public class CommandHistory
    {
        private readonly Stack<IEditCommand> _undo = new();
        private readonly Stack<IEditCommand> _redo = new();

        public int MaxDepth { get; }

        public CommandHistory(int maxDepth = 100)
        {
            MaxDepth = maxDepth;
        }

        public bool CanUndo => _undo.Count > 0;

        public bool CanRedo => _redo.Count > 0;

        public void Execute(IEditCommand command)
        {
            command.Execute();
            _undo.Push(command);
            _redo.Clear();
            TrimToMaxDepth();
        }

        /// <summary>
        /// Like <see cref="Execute"/>, but if the top undo entry can coalesce with
        /// <paramref name="command"/>, merges into it instead of pushing a new step.
        /// Used for scrubbing continuous properties (opacity, crop, volume).
        /// </summary>
        public void ExecuteCoalescing(ICoalescingEditCommand command)
        {
            if (_undo.Count > 0 && _undo.Peek() is ICoalescingEditCommand prev && prev.CanCoalesceWith(command))
            {
                prev.CoalesceFrom(command);
                _redo.Clear();
                return;
            }

            Execute(command);
        }

        private void TrimToMaxDepth()
        {
            if (_undo.Count <= MaxDepth)
                return;
            // drop oldest: rebuild without the bottom entry
            var keep = _undo.ToArray(); // top-first
            _undo.Clear();
            for (var i = Math.Min(keep.Length, MaxDepth) - 1; i >= 0; i--)
                _undo.Push(keep[i]);
        }

        public bool Undo()
        {
            if (_undo.Count == 0)
                return false;
            var command = _undo.Pop();
            command.Undo();
            _redo.Push(command);
            return true;
        }

        public bool Redo()
        {
            if (_redo.Count == 0)
                return false;
            var command = _redo.Pop();
            command.Redo();
            _undo.Push(command);
            return true;
        }
    }
}
