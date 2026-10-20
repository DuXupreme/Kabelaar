namespace Kabelaar.Core;

public interface IEditCommand
{
    string Name { get; }
    void Execute();
    void Undo();
}

public sealed class DelegateCommand(string name, Action execute, Action undo) : IEditCommand
{
    public string Name { get; } = name;
    public void Execute() => execute();
    public void Undo() => undo();
}

public sealed class CompositeCommand(string name, params IEditCommand[] commands) : IEditCommand
{
    public string Name { get; } = name;
    public void Execute()
    {
        var completed = 0;
        try
        {
            for (; completed < commands.Length; completed++) commands[completed].Execute();
        }
        catch
        {
            for (var i = completed - 1; i >= 0; i--) commands[i].Undo();
            throw;
        }
    }

    public void Undo()
    {
        var undone = commands.Length;
        try
        {
            for (var i = commands.Length - 1; i >= 0; i--)
            {
                commands[i].Undo();
                undone = i;
            }
        }
        catch
        {
            for (var i = undone; i < commands.Length; i++) commands[i].Execute();
            throw;
        }
    }
}

public sealed class CommandHistory
{
    private sealed record Entry(IEditCommand Command, Guid Before, Guid After);
    private readonly Stack<Entry> undo = new();
    private readonly Stack<Entry> redo = new();
    private Guid current = Guid.NewGuid();
    private Guid saved;

    public CommandHistory() => saved = current;
    public event EventHandler? Changed;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public bool IsDirty => current != saved;
    public Guid StateToken => current;
    public string? UndoName => undo.TryPeek(out var entry) ? entry.Command.Name : null;
    public string? RedoName => redo.TryPeek(out var entry) ? entry.Command.Name : null;

    public void Execute(IEditCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Execute();
        var next = Guid.NewGuid();
        undo.Push(new(command, current, next));
        current = next;
        redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (!undo.TryPeek(out var entry)) return;
        entry.Command.Undo();
        undo.Pop();
        redo.Push(entry);
        current = entry.Before;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (!redo.TryPeek(out var entry)) return;
        entry.Command.Execute();
        redo.Pop();
        undo.Push(entry);
        current = entry.After;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void MarkSaved() { saved = current; Changed?.Invoke(this, EventArgs.Empty); }
    public void Clear()
    {
        undo.Clear(); redo.Clear();
        current = saved = Guid.NewGuid();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
