namespace NexusShot.Core;

/// <summary>Undo and redo: whole-document snapshots, with the creation of an annotation and its
/// first edits folded into one step. Snapshots share the stored copy of every annotation a step left
/// alone, so history grows with what each step changed, not with the whole document.</summary>
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
        EndAdjustment();
        _createdUndoOwner = null;
        _undo.Push(Snapshot());
        Revision = _nextRevision++;
        if (_undo.Count > MaxUndo)
        {
            // Take keeps the newest (stack order); Reverse re-pushes them oldest-first.
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
        Revision = history.Revision;
        _creationHistory = null;
    }

    /// <summary>The newest snapshot's stored copies by id, for the next snapshot to reuse.</summary>
    private Dictionary<Guid, Annotation> _stored = [];

    private DocumentSnapshot Snapshot()
    {
        var annotations = _annotations
            .Select(live => _stored.TryGetValue(live.Id, out var copy) && copy.SameContent(live) ? copy : live.Clone())
            .ToList();
        Remember(annotations);
        return new(annotations, CropBounds, PendingCrop, Revision);
    }

    private void Remember(IReadOnlyList<Annotation> stored)
    {
        _stored = new Dictionary<Guid, Annotation>(stored.Count);
        foreach (var copy in stored) _stored[copy.Id] = copy;
    }

    /// <summary>Restores as fresh clones: stored copies are shared, so one edited live would change
    /// every step holding it.</summary>
    private void Restore(DocumentSnapshot snapshot)
    {
        _creationHistory = null;
        EndAdjustment();
        var selectedId = Selected?.Id;
        ReplaceAnnotations(snapshot.Annotations.Select(stored => stored.Clone()).ToList());
        Remember(snapshot.Annotations);
        CropBounds = snapshot.CropBounds;
        PendingCrop = snapshot.PendingCrop;
        Revision = snapshot.Revision;

        // Restored annotations are clones: reselect by id, and drop the editor holding the old instance.
        EditingText = null;
        Selected = selectedId is null ? null : _annotations.FirstOrDefault(a => a.Id == selectedId);
        _draft = null;
        _createdUndoOwner = null;
        _gesture = GestureKind.None;
        Notify();
    }
}
