using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>
/// Save, Save As and Copy for one open document, and the destination they share.
///
/// Every write commits the text, then the crop, then flattens - stated here once rather than at
/// three call sites. Reporting stays with the caller: the window owns the toast.
/// </summary>
public sealed class EditorFiles(EditorDocument document)
{
    /// <summary>The file being edited. Save As moves it, which is why no other class caches it.</summary>
    public string Path { get; private set; } = string.Empty;

    public string FileName => System.IO.Path.GetFileName(Path);

    public void OpenedAt(string path) => Path = path;

    /// <summary>Raised before a write, so the window can commit an open text box: the box lives in
    /// the window's text controller, not the document.</summary>
    public event Action? Committing;

    /// <summary>Writes the flattened image over the original.</summary>
    public void Save()
    {
        Committing?.Invoke();
        document.CommitCrop();
        Exporter.SavePng(document, Path, Path);
        document.ResetAfterSave();
    }

    /// <summary>
    /// Writes the flattened image somewhere new and continues editing it there. Null when the user
    /// cancelled: nothing is written and the crop stays uncommitted, so cancelling costs nothing.
    ///
    /// <paramref name="chooseDestination"/> takes the suggested name and starting folder. It is
    /// passed in so the cancel path can be tested without a modal dialog.
    /// </summary>
    public string? SaveAs(Func<string, string?, string?> chooseDestination)
    {
        Committing?.Invoke();

        var suggested = $"{System.IO.Path.GetFileNameWithoutExtension(Path)}_edited.png";
        if (chooseDestination(suggested, System.IO.Path.GetDirectoryName(Path)) is not { } destination)
            return null;

        document.CommitCrop();
        Exporter.SavePng(document, Path, destination);

        // The editor follows the file: further edits belong to the copy, not the original.
        Path = destination;
        document.ResetAfterSave();
        return destination;
    }

    /// <summary>
    /// Puts the flattened image on the clipboard, cropped as the user sees it but without the
    /// document committing to that crop: copying is not saving, and must not discard the original.
    /// </summary>
    public void CopyToClipboard()
    {
        Committing?.Invoke();

        var temporary = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"nexusshot-{Guid.NewGuid():N}.png");
        try
        {
            Exporter.SavePng(document, Path, temporary, document.PendingCrop);
            ClipboardImage.Copy(temporary);
        }
        finally
        {
            // Cleaned up even when the copy failed, or a failed clipboard grab would leave the file.
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
