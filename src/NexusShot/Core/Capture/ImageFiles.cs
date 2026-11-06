namespace NexusShot.Core;

public enum ImageFormat { Png, Jpeg, Bmp }

/// <summary>The image types the editor opens, and saves back in their own format.</summary>
public static class ImageFiles
{
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp"];

    public static bool CanOpen(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>An absolute path the file system accepts. An empty or relative one, or one holding a
    /// character such as NUL, fails every use of it - the save folder's at startup, or a history row's
    /// in every scan of the Library.</summary>
    public static bool IsUsablePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
        && path.IndexOfAny(Path.GetInvalidPathChars()) < 0;

    public static string ExtensionOf(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Bmp => ".bmp",
        _ => ".png",
    };

    /// <summary>The format a path is written in; anything unrecognised is PNG.</summary>
    public static ImageFormat FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => ImageFormat.Jpeg,
        ".bmp" => ImageFormat.Bmp,
        _ => ImageFormat.Png,
    };
}
