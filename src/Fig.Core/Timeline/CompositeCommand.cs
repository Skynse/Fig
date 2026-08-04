using System;
using System.Collections.Generic;
using System.Linq;

namespace Fig.Core.Timeline
{
    public class CompositeCommand : IEditCommand
    {
        private readonly IReadOnlyList<IEditCommand> _commands;

        public string Description => string.Join("; ", _commands.Select(c => c.Description));

        public CompositeCommand(params IEditCommand[] commands)
        {
            _commands = commands;
        }

        public void Execute()
        {
            foreach (var command in _commands)
                command.Execute();
        }

        public void Undo()
        {
            for (var i = _commands.Count - 1; i >= 0; i--)
                _commands[i].Undo();
        }

        public void Redo()
        {
            foreach (var command in _commands)
                command.Redo();
        }
    }
}
