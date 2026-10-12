using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>Prepares independent exports on the UI thread and adopts successful saves there.
/// Encoding never mutates the live document, so a failed write leaves crop and undo intact.
/// <paramref name="pixels"/> reads the image on screen, which every export flattens.</summary>
public sealed class EditorFiles(EditorDocument document, Func<DecodedImage> pixels)
{
    public string Path { get; private set; } = string.Empty;
    public string FileName => System.IO.Path.GetFileName(Path);
    public void OpenedAt(string path) => Path = System.IO.Path.GetFullPath(path);
    public event Action? Committing;

    public ExportRequest PrepareSave()
    {
        Committing?.Invoke();
        return new(document.CreateExportSnapshot(), pixels(), Path, Path, document.Revision);
    }

    /// <summary><paramref name="chooseDestination"/> gets a suggested name, folder and the formats to
    /// offer, the image's own first: an export re-encodes, so any of them can be written.</summary>
    public ExportRequest? PrepareSaveAs(Func<string, string?, IReadOnlyList<ImageFormat>, string?> chooseDestination)
    {
        var format = ImageFiles.FormatOf(Path);
        var suggested = System.IO.Path.GetFileNameWithoutExtension(Path) + "_edited" + ImageFiles.ExtensionOf(format);
        ImageFormat[] formats = [format, .. Enum.GetValues<ImageFormat>().Where(other => other != format)];
        if (chooseDestination(suggested, System.IO.Path.GetDirectoryName(Path), formats) is not { } destination)
            return null;
        Committing?.Invoke();
        return new(document.CreateExportSnapshot(), pixels(), Path, System.IO.Path.GetFullPath(destination), document.Revision);
    }

    public void CompleteSave(ExportRequest request)
    {
        Path = request.Destination;
        document.MarkSaved(request.Revision);
    }
}

/// <summary>Owned by one worker after preparation; contains no live view state. <paramref name="Source"/>
/// names the image; <paramref name="Pixels"/> are what is flattened, and go with the request.
/// <paramref name="Revision"/> is the document state the export captured.</summary>
public sealed record ExportRequest(EditorDocument Document, DecodedImage Pixels, string Source, string Destination, long Revision)
    : IDisposable
{
    public void Save() => Exporter.Save(Document, Pixels, Destination);

    public void Dispose() => Pixels.Dispose();

    public void CopyToClipboard() => WithFlattened(ClipboardImage.Copy);

    /// <summary>The number of lines of text copied; zero when there was none.</summary>
    public int CopyText(string? language)
    {
        var lines = 0;
        WithFlattened(path =>
        {
            using var pixels = ImageSurface.Decode(path);
            lines = TextRecognition.CopyText(pixels, language);
        });
        return lines;
    }

    /// <summary>A shared file outlives its share: the receiving app reads it on its own schedule.</summary>
    private static readonly TimeSpan ShareLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// Writes the flattened image for the share sheet and returns its path. Named after the capture,
    /// because the receiving app shows the name; each share gets its own folder, so a second share of
    /// the same capture never replaces a file the first receiver has yet to read.
    /// </summary>
    public string SaveShareCopy()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NexusShot share");
        ClearExpiredShares(root);
        var folder = Directory.CreateDirectory(System.IO.Path.Combine(root, Guid.NewGuid().ToString("N"))).FullName;

        var path = System.IO.Path.Combine(folder, System.IO.Path.GetFileNameWithoutExtension(Source) + ".png");
        Exporter.Save(Document, Pixels, path);
        return path;
    }

    private static void ClearExpiredShares(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var share in new DirectoryInfo(root).EnumerateDirectories())
        {
            if (DateTime.UtcNow - share.CreationTimeUtc < ShareLifetime) continue;
            try { share.Delete(recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { Log.Error("share.temp_cleanup", exception); }
        }
    }

    private void WithFlattened(Action<string> use)
    {
        var temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nexusshot-{Guid.NewGuid():N}.png");
        try
        {
            Exporter.Save(Document, Pixels, temporary);
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
