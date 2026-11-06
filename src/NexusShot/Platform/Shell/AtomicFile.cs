using NexusShot.Core;

namespace NexusShot.Platform;

internal static class AtomicFile
{
    public static void Copy(string source, string destination)
    {
        destination = Path.GetFullPath(destination);
        if (string.Equals(Path.GetFullPath(source), destination, StringComparison.OrdinalIgnoreCase)) return;
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".nexusshot-{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(source, temporary, overwrite: false);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { Log.Error("file.temp_cleanup", exception); }
        }
    }
}
