using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>Prepares independent exports on the UI thread and adopts successful saves there.
/// Encoding never mutates the live document, so a failed write leaves crop and undo intact.</summary>
public sealed class EditorFiles(EditorDocument document)
{
    public string Path { get; private set; } = string.Empty;
    public string FileName => System.IO.Path.GetFileName(Path);
    public void OpenedAt(string path) => Path = System.IO.Path.GetFullPath(path);
    public event Action? Committing;

    public ExportRequest PrepareSave()
    {
        Committing?.Invoke();
        return new(document.CreateExportSnapshot(), Path, Path);
    }

    public ExportRequest? PrepareSaveAs(Func<string, string?, string?> chooseDestination)
    {
        var suggested = $"{System.IO.Path.GetFileNameWithoutExtension(Path)}_edited.png";
        if (chooseDestination(suggested, System.IO.Path.GetDirectoryName(Path)) is not { } destination)
            return null;
        Committing?.Invoke();
        return new(document.CreateExportSnapshot(), Path, System.IO.Path.GetFullPath(destination));
    }

    public void CompleteSave(ExportRequest request)
    {
        Path = request.Destination;
        document.ResetAfterSave();
    }
}

/// <summary>Owned by one worker after preparation; contains no live view state.</summary>
public sealed record ExportRequest(EditorDocument Document, string Source, string Destination)
{
    public void Save() => Exporter.Save(Document, Source, Destination);

    public void CopyToClipboard() => WithFlattened(ClipboardImage.Copy);

    /// <summary>The number of lines of text copied; zero when there was none.</summary>
    public int CopyText()
    {
        var lines = 0;
        WithFlattened(path =>
        {
            using var pixels = ImageSurface.Decode(path);
            lines = TextRecognition.CopyText(pixels);
        });
        return lines;
    }

    private void WithFlattened(Action<string> use)
    {
        var temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nexusshot-{Guid.NewGuid():N}.png");
        try
        {
            Exporter.Save(Document, Source, temporary);
            use(temporary);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { Log.Error("clipboard.temp_cleanup", exception); }
        }
    }
}
