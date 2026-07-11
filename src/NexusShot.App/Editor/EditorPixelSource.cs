using System.Drawing;
using System.Drawing.Imaging;

namespace NexusShot.App.Editor;

/// <summary>
/// The decoded BGRA32 pixels of the screenshot being edited. The live preview reads from this to
/// render blur and pixelate with the real effect instead of a stand-in, using the same
/// <see cref="PixelEffects"/> code the export path runs.
/// </summary>
public sealed class EditorPixelSource
{
    public required byte[] Pixels { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Stride { get; init; }

    /// <summary>Decodes the file into a BGRA buffer. CPU-bound; call from a background thread.</summary>
    public static EditorPixelSource Load(string path)
    {
        using var source = Image.FromFile(path);
        using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.DrawImageUnscaled(source, 0, 0);

        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[data.Stride * data.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            return new EditorPixelSource
            {
                Pixels = pixels,
                Width = bitmap.Width,
                Height = bitmap.Height,
                Stride = data.Stride,
            };
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
