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
            if (_undo.Count > MaxDepth)
                _undo.Pop();
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
