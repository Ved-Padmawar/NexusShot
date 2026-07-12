namespace NexusShot.Render;

/// <summary>
/// PNG encoding, via WIC. Shared by the exporter and the capture path so there is exactly one
/// place that knows how a NexusShot PNG is written.
/// </summary>
public static class PngWriter
{
    /// <summary>Writes premultiplied BGRA pixels, top-down, as a PNG.</summary>
    public static void Write(string path, byte[] premultipliedBgra, int width, int height)
    {
        using var bitmap = WicImagingFactory.CreateBitmapFromMemory(
            (uint)width,
            (uint)height,
            Constants.GUID_WICPixelFormat32bppPBGRA,
            (uint)(width * 4),
            premultipliedBgra);

        using var file = File.Create(path);
        using var stream = new ManagedIStream(file);
        using var encoder = WicImagingFactory.CreateEncoder(Constants.GUID_ContainerFormatPng);
        encoder.Initialize(stream, WICBitmapEncoderCacheOption.WICBitmapEncoderNoCache);

        using var frame = encoder.CreateNewFrame();
        frame.Initialize();
        frame.SetSize((uint)width, (uint)height);
        frame.SetPixelFormat(Constants.GUID_WICPixelFormat32bppPBGRA);
        frame.WriteSource(bitmap);
        frame.Commit();
        encoder.Commit();
    }
}
