namespace NexusShot.Core;

public enum ImageFormat { Png, Jpeg, Bmp }

/// <summary>The image types the editor opens, and saves back in their own format.</summary>
public static class ImageFiles
{
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp"];

    public static bool CanOpen(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>The format a path is written in; anything unrecognised is PNG.</summary>
    public static ImageFormat FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => ImageFormat.Jpeg,
        ".bmp" => ImageFormat.Bmp,
        _ => ImageFormat.Png,
    };
}
