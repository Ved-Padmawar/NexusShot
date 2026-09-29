using NexusShot.Core;
using NexusShot.Render;

namespace NexusShot.Platform;

/// <summary>Disk reads happen on a worker; <see cref="HistorySync"/> applies the result on the UI
/// thread.</summary>
internal static class HistoryScanner
{
    public static HistoryScan Scan(string folder, IReadOnlyCollection<string> known,
        IReadOnlyDictionary<string, FileVersion> versions)
    {
        var changed = new List<(ScreenshotHistoryItem, FileVersion)>();
        var missing = new List<string>();
        var unreadable = new List<(string, FileVersion)>();

        // Remembered paths too, so a broken file that is later deleted is reported missing.
        var paths = new HashSet<string>(known.Concat(versions.Keys), StringComparer.OrdinalIgnoreCase);
        try { paths.UnionWith(Directory.EnumerateFiles(folder).Where(ImageFiles.CanOpen)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { Log.Error("history.scan", exception); }
        foreach (var path in paths)
        {
            FileVersion? read = null;
            try
            {
                var version = FileVersion.Read(path);
                read = version;
                if (versions.TryGetValue(path, out var previous) && previous == version) continue;
                var (width, height) = ImageSurface.ReadSize(path);
                // A file still being written will be retried on the next watcher event.
                if (FileVersion.Read(path) != version) continue;
                changed.Add((new ScreenshotHistoryItem
                {
                    FilePath = path, Width = width, Height = height,
                    CapturedAt = CaptureName.TryParseTime(path, out var time) ? time : File.GetCreationTime(path),
                }, version));
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            { missing.Add(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
            {
                Log.Error("history.read", exception, path);
                if (read is { } failed) unreadable.Add((path, failed));
            }
        }
        return new(changed, missing, unreadable);
    }
}
