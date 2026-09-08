using System;
using System.Collections.Generic;

namespace QuickCapture.Models;

public interface IUndoableAction { void Execute(); void Undo(); }

public sealed class UndoRedoManager
{
    private readonly List<IUndoableAction> undo = new();
    private readonly Stack<IUndoableAction> redo = new();
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public void Execute(IUndoableAction action)
    {
        action.Execute(); undo.Add(action); redo.Clear();
        if (undo.Count > 100) undo.RemoveAt(0);
    }
    public void Undo()
    {
        if (!CanUndo) return;
        var action = undo[^1]; undo.RemoveAt(undo.Count - 1); action.Undo(); redo.Push(action);
    }
    public void Redo()
    {
        if (!CanRedo) return;
        var action = redo.Pop(); action.Execute(); undo.Add(action);
    }
    public void Reset() { undo.Clear(); redo.Clear(); }
}
