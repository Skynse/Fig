namespace Fig.Core.Timeline
{
    public interface IEditCommand
    {
        string Description { get; }

        void Execute();

        void Undo();

        void Redo();
    }
}
