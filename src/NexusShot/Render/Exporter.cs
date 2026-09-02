using NexusShot.Core;

namespace NexusShot.Render;

/// <summary>
/// Flattens a document to a file.
///
/// This is the same <see cref="AnnotationRenderer"/> the screen uses, pointed at an offscreen
/// target. That is the whole design: the export cannot drift from the preview, because there is
/// only one piece of drawing code, so the export cannot drift from the preview.
///
/// Adorners are deliberately not drawn: the file gets the annotations, never the selection grips.
/// </summary>
public static class Exporter
{
    /// <summary>One device for every export: building D3D11 + DXGI + D2D costs tens of
    /// milliseconds, which is the whole latency budget for a Copy. Lives for the process.</summary>
    private static readonly object _deviceLock = new();
    private static IComObject<ID2D1Device>? _sharedDevice;

    private static IComObject<ID2D1Device> GetSharedDevice()
    {
        lock (_deviceLock)
        {
            if (_sharedDevice is not null) return _sharedDevice;
            var (device, factory) = D2DDevice.Create();

            // The device holds its own reference to the factory that made it.
            factory.Dispose();
            return _sharedDevice = device;
        }
    }

    /// <summary>
    /// Renders the image plus its annotations, honouring the crop, and writes a PNG.
    ///
    /// Rendering happens on a D3D device context (not a WIC render target) so the GPU effects the
    /// preview uses are available here too - a WIC target cannot host ID2D1Effect, and falling back
    /// to the placeholder would mean the exported blur silently differed from the one on screen.
    /// The result is copied out to a WIC bitmap only to be encoded.
    /// </summary>
    /// <summary><paramref name="cropOverride"/> crops without the document committing to it - a copy
    /// shows what is on screen, but must not silently discard the uncropped original.</summary>
    public static void SavePng(
        EditorDocument document, string sourcePath, string path, Rect? cropOverride = null)
    {
        var crop = cropOverride
            ?? document.CropBounds
            ?? new Rect(0, 0, document.ImageWidth, document.ImageHeight);
        var width = (uint)Math.Max(1, Math.Round(crop.Width));
        var height = (uint)Math.Max(1, Math.Round(crop.Height));

        var device = GetSharedDevice();
        using var context = device.CreateDeviceContext();

        // D2D bitmaps are device resources: one realized on the editor's window target cannot be
        // drawn by this one. Decode against the target that will draw it.
        using var image = ImageSurface.Load(sourcePath, context);

        using var surface = context.CreateBitmap<ID2D1Bitmap1>(
            new D2D_SIZE_U { width = width, height = height },
            new D2D1_BITMAP_PROPERTIES1
            {
                pixelFormat = PremultipliedBgra,
                dpiX = 96,
                dpiY = 96,
                bitmapOptions = D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET,
            });

        // A CPU-readable staging bitmap: the GPU cannot be mapped directly.
        using var staging = context.CreateBitmap<ID2D1Bitmap1>(
            new D2D_SIZE_U { width = width, height = height },
            new D2D1_BITMAP_PROPERTIES1
            {
                pixelFormat = PremultipliedBgra,
                dpiX = 96,
                dpiY = 96,
                bitmapOptions = D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CPU_READ
                    | D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
            });

        var target = context.AsRenderTarget2();
        using var resources = new D2DResources(target);
        using var renderer = new AnnotationRenderer(resources);
        using var effects = new PixelEffectSource(image, resources);

        context.Object.SetTarget(surface.Object);
        context.BeginDraw();
        context.Clear(new D3DCOLORVALUE(0, 0, 0, 0));

        // Shift so the crop's top-left becomes the file's origin.
        context.Object.SetTransform(D2D_MATRIX_3X2_F.Translation((float)-crop.X, (float)-crop.Y));

        target.DrawBitmap(
            image.Bitmap, 1f,
            D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR,
            new D2D_RECT_F(0, 0, image.Width, image.Height));

        renderer.DrawAnnotations(target, document, effects);

        context.Object.SetTransform(D2D_MATRIX_3X2_F.Identity());
        context.EndDraw();
        context.Object.SetTarget(null);

        staging.Object.CopyFromBitmap(nint.Zero, surface.Object, nint.Zero).ThrowOnError();
        // Encode beside the destination and replace only after success. A failed encoder or a
        // full disk must not truncate the screenshot the editor is currently working on.
        var destination = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".nexusshot-{Guid.NewGuid():N}.tmp");
        try
        {
            WritePng(staging, (int)width, (int)height, temporary);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static readonly D2D1_PIXEL_FORMAT PremultipliedBgra = new()
    {
        format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
        alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED,
    };

    /// <summary>Maps the rendered pixels back to the CPU and encodes them as PNG.</summary>
    private static void WritePng(IComObject<ID2D1Bitmap1> staging, int width, int height, string path)
    {
        staging.Object.Map(D2D1_MAP_OPTIONS.D2D1_MAP_OPTIONS_READ, out var mapped).ThrowOnError();
        try
        {
            // Encoded straight out of the mapped rows. The pitch is the GPU's rather than width*4,
            // so it is passed through as the stride instead of repacking into a second buffer.
            PngWriter.Write(path, mapped.bits, width, height, (int)mapped.pitch);
        }
        finally
        {
            staging.Object.Unmap().ThrowOnError();
        }
    }
}
