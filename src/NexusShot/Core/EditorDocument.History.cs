namespace NexusShot.Core;

/// <summary>Undo and redo: whole-document snapshots, with the creation of an annotation and its
/// first edits folded into one step.</summary>
public sealed partial class EditorDocument
{
    public void Undo()
    {
        if (!_undo.TryPop(out var previous)) return;
        _redo.Push(Snapshot());
        Restore(previous);
    }

    public void Redo()
    {
        if (!_redo.TryPop(out var next)) return;
        _undo.Push(Snapshot());
        Restore(next);
    }

    /// <summary>Pushes the undo snapshot for a move/resize on its first actual movement.</summary>
    private void EnsureGestureUndo()
    {
        if (_gestureUndoPushed) return;
        _gestureUndoPushed = true;
        PushUndo();
    }

    private void PushUndo()
    {
        _creationHistory = null;
        EndThicknessAdjustment();
        _createdUndoOwner = null;
        _undo.Push(Snapshot());
        if (_undo.Count > MaxUndo)
        {
            // Stack enumerates newest-first, so Take keeps the newest entries; the reverse then
            // restores oldest-first for re-pushing, which is what puts the newest back on top.
            var kept = _undo.Take(MaxUndo).Reverse().ToList();
            _undo.Clear();
            foreach (var snapshot in kept) _undo.Push(snapshot);
        }
        _redo.Clear();
    }

    private void RestoreCreationHistory()
    {
        if (_creationHistory is not { } history) return;
        _undo.Clear();
        foreach (var snapshot in history.Undo.Reverse()) _undo.Push(snapshot);
        _redo.Clear();
        foreach (var snapshot in history.Redo.Reverse()) _redo.Push(snapshot);
        _creationHistory = null;
    }

    private DocumentSnapshot Snapshot() => new(_annotations.Select(a => a.Clone()).ToList(), CropBounds, PendingCrop);

    private void Restore(DocumentSnapshot snapshot)
    {
        _creationHistory = null;
        EndThicknessAdjustment();
        var selectedId = Selected?.Id;
        ReplaceAnnotations(snapshot.Annotations);
        CropBounds = snapshot.CropBounds;
        PendingCrop = snapshot.PendingCrop;

        // The restored annotations are fresh clones, so any open editor refers to an instance the
        // document no longer holds. Reselecting by id installs the clone and closes the editor.
        EditingText = null;
        Selected = selectedId is null ? null : _annotations.FirstOrDefault(a => a.Id == selectedId);
        _draft = null;
        _createdUndoOwner = null;
        _gesture = GestureKind.None;
        Notify();
    }
}
