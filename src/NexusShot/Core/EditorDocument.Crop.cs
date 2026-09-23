namespace NexusShot.Core;

/// <summary>
/// The crop session. The crop tool never draws a fresh rectangle: engaging it opens a session whose
/// frame starts at the current crop (or the whole image) and is then moved/resized via handles; the
/// frame becomes CropBounds only on commit.
/// </summary>
public sealed partial class EditorDocument
{
    public void BeginCropSession()
    {
        if (ImageWidth <= 0 || ImageHeight <= 0) return;
        Selected = null;
        PendingCrop = CropBounds ?? new Rect(0, 0, ImageWidth, ImageHeight);
        Notify();
    }

    /// <summary>Applies the pending frame. A frame covering the whole image means "no crop".</summary>
    public void CommitCrop()
    {
        if (PendingCrop is not { } pending) return;
        var coversEverything = pending.X <= 0.5 && pending.Y <= 0.5
            && pending.Right >= ImageWidth - 0.5 && pending.Bottom >= ImageHeight - 0.5;
        PendingCrop = null;
        Rect? crop = coversEverything ? null : pending;
        if (crop != CropBounds)
        {
            // Snapshot the previous committed state, without reviving a pending session on undo.
            PushUndo();
            CropBounds = crop;
        }
        Notify();
    }

    public void CancelCropSession()
    {
        if (PendingCrop is null) return;
        PendingCrop = null;
        Notify();
    }

    /// <summary>The crop-frame handle under <paramref name="point"/>, if a session is active.</summary>
    public ResizeHandle? GetCropHandleAt(Point point, double tolerance)
    {
        if (PendingCrop is not { } crop) return null;
        return BoxGeometry.HitTestHandle(crop, point, tolerance);
    }

    /// <summary>Restores the full image, undoably.</summary>
    public void ClearCrop()
    {
        if (CropBounds is null) return;
        PushUndo();
        CropBounds = null;
        Notify();
    }
}
