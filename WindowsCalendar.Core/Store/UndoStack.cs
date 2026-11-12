namespace WindowsCalendar.Core.Store;

/// <summary>
/// One item's part of an undo step (undo R4): the stored row before and after the step. Before is null for a created
/// item, After for a deleted one. <see cref="DeleteMergedUpdate"/> marks a delete that took over an unsent update's
/// outbox entry, so cancelling the delete puts that update back.
/// </summary>
public sealed record UndoChange(string ItemId, Item? Before, Item? After, bool DeleteMergedUpdate = false);

/// <summary>
/// One user action on the undo stack (undo R1, R2): every local write made while its <see cref="UndoScope"/> was open.
/// A <see cref="Refusal"/> is the message shown instead of undoing, for an action that can't be put back (undo R5).
/// </summary>
public sealed class UndoStep(string label)
{
    private readonly List<UndoChange> _changes = [];

    /// <summary>What the action was, for the log (no titles or contents).</summary>
    public string Label { get; } = label;

    public string? Refusal { get; internal set; }

    public IReadOnlyList<UndoChange> Changes => _changes;

    /// <summary>A later write to the same item keeps the first before and takes the latest after.</summary>
    internal void Add(UndoChange change)
    {
        var i = _changes.FindIndex(c => c.ItemId == change.ItemId);
        if (i < 0)
        {
            _changes.Add(change);
            return;
        }
        _changes[i] = _changes[i] with { After = change.After, DeleteMergedUpdate = change.DeleteMergedUpdate };
    }

    /// <summary>
    /// An undo wrote <paramref name="written"/> back for <paramref name="itemId"/>: a change of this step that left the
    /// item as <paramref name="payload"/> now expects the written version (it may be re-serialised or have a new id).
    /// </summary>
    internal void Rebase(string itemId, string payload, Item written)
    {
        for (var i = 0; i < _changes.Count; i++)
        {
            if (_changes[i].ItemId == itemId && _changes[i].After?.RawPayload == payload)
            {
                _changes[i] = _changes[i] with { ItemId = written.Id, After = written };
            }
        }
    }
}

/// <summary>The session's undo steps, newest first, at most <see cref="Limit"/>; in memory only (undo R2).</summary>
public sealed class UndoStack
{
    public const int Limit = 20;

    private readonly List<UndoStep> _steps = [];

    /// <summary>Raised when a new step is put on top (its first write), e.g. so an older undo banner closes.</summary>
    public event Action? Pushed;

    public int Count => _steps.Count;

    public UndoStep? Peek() => _steps.Count > 0 ? _steps[0] : null;

    internal IReadOnlyList<UndoStep> All => _steps;

    internal void Push(UndoStep step)
    {
        _steps.Insert(0, step);
        if (_steps.Count > Limit)
        {
            _steps.RemoveAt(Limit);
        }
        Pushed?.Invoke();
    }

    internal void Pop()
    {
        if (_steps.Count > 0)
        {
            _steps.RemoveAt(0);
        }
    }
}

/// <summary>
/// Collects the local writes of one user action into one undo step (undo R2); disposing it ends the action. A scope
/// opened inside another joins it. Open it around synchronous writes only (no await inside).
/// </summary>
public sealed class UndoScope : IDisposable
{
    private readonly LocalStore _store;
    private bool _disposed;

    internal UndoScope(LocalStore store) => _store = store;

    /// <summary>The action can't be undone: undo shows <paramref name="message"/> and changes nothing (undo R5).</summary>
    public void Refuse(string message) => _store.RefuseUndo(message);

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _store.EndUndo();
        }
    }
}

public enum UndoOutcome
{
    /// <summary>The stack was empty.</summary>
    Nothing,
    Done,
    /// <summary>Done; an item deleted on the server was added again with a new id (undo R8).</summary>
    Recreated,
    /// <summary>Refused: an item changed on the server since the action (undo R6). The step is dropped.</summary>
    ChangedOnServer,
    /// <summary>Refused for now: the delete is being sent. The step stays.</summary>
    Sending,
    /// <summary>Refused: a deleted item can't be added again (an MS365 occurrence). The step is dropped.</summary>
    CannotRecreate,
    /// <summary>Refused with the step's <see cref="UndoStep.Refusal"/> (undo R5). The step is dropped.</summary>
    Refused,
}

public sealed record UndoResult(UndoOutcome Outcome, UndoStep? Step);
