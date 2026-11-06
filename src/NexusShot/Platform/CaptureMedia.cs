using System.Diagnostics;
using System.Text;
using NexusShot.Core;
using NexusShot.Render;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace NexusShot.Platform;

internal static class CaptureMedia
{
    private const uint CF_HDROP = 15;
    private const int DROPFILES_SIZE = 20;
    private const int DROPFILES_FWIDE = 16;

    public static (int Width, int Height) ReadSize(string path) => CaptureFiles.IsVideo(path)
        ? VideoSize(path).GetAwaiter().GetResult() : ImageSurface.ReadSize(path);

    private static async Task<(int, int)> VideoSize(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var properties = await file.Properties.GetVideoPropertiesAsync();
        if (properties.Width == 0 || properties.Height == 0) throw new InvalidOperationException("The video has no picture.");
        return ((int)properties.Width, (int)properties.Height);
    }

    public static DecodedImage Thumbnail(string path, int width, int height) => CaptureFiles.IsVideo(path)
        ? VideoThumbnail(path, width).GetAwaiter().GetResult()
        : ImageSurface.DecodeScaled(path, width, height, cover: true);

    private static async Task<DecodedImage> VideoThumbnail(string path, int width)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.VideosView, (uint)width, ThumbnailOptions.ResizeThumbnail);
        if (thumbnail is null || thumbnail.Type != ThumbnailType.Image)
            throw new InvalidOperationException("No video thumbnail is available.");
        using var stream = thumbnail.AsStreamForRead();
        return ImageSurface.Decode(stream);
    }

    public static void Play(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    /// <summary>A CF_HDROP: a DROPFILES header, then the double-null-terminated wide path list.</summary>
    public static void CopyFile(string path)
    {
        var names = Encoding.Unicode.GetBytes(Path.GetFullPath(path) + "\0\0");
        ClipboardWriter.Write(() => ClipboardWriter.Place(CF_HDROP, DROPFILES_SIZE + names.Length, bytes =>
        {
            bytes.Clear();
            BitConverter.TryWriteBytes(bytes, DROPFILES_SIZE);
            BitConverter.TryWriteBytes(bytes[DROPFILES_FWIDE..], 1);
            names.CopyTo(bytes[DROPFILES_SIZE..]);
        }));
    }
}
