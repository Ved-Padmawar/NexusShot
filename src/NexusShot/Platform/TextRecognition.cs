using System.Runtime.InteropServices;
using NexusShot.Core;
using NexusShot.Render;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using WinRT;

namespace NexusShot.Platform;

/// <summary>
/// OCR to the clipboard, with the engine built into Windows: offline, on every Windows 10/11 PC.
/// The Copilot+ TextRecognizer needs an MSIX package with systemAIModels; this app is unpackaged.
/// </summary>
internal static class TextRecognition
{
    /// <summary>The languages Windows can recognise on this PC, as (BCP-47 tag, display name).</summary>
    public static IReadOnlyList<(string Tag, string Name)> Languages() =>
        [.. OcrEngine.AvailableRecognizerLanguages.Select(language => (language.LanguageTag, language.DisplayName))];

    /// <summary>The number of lines copied; zero leaves the clipboard alone. Blocking: call it on
    /// the media worker. <paramref name="language"/> is a tag from <see cref="Languages"/>, or null
    /// for the user's profile languages.</summary>
    public static int CopyText(DecodedImage image, string? language)
    {
        var lines = Recognize(image, language);
        if (lines.Length > 0) ClipboardText.Copy(string.Join(Environment.NewLine, lines));
        return lines.Length;
    }

    /// <summary>Whether any recognition language can serve <paramref name="language"/>.</summary>
    public static bool CanRead(string? language) => Engine(language) is not null;

    /// <summary>The lines of text in the image, as the engine writes them - without spaces between
    /// words in the languages that use none. Blocking.</summary>
    public static string[] Recognize(DecodedImage image, string? language) =>
        [.. Read(image, language, out _).Lines.Select(line => line.Text)];

    /// <summary>Each line's words and where they sit, in <paramref name="image"/>'s pixels. Blocking.</summary>
    public static List<IReadOnlyList<TextWord>> Words(DecodedImage image, string? language)
    {
        var result = Read(image, language, out var scale);
        return [.. result.Lines.Select(line => (IReadOnlyList<TextWord>)[.. line.Words.Select(word => new TextWord(word.Text,
            new Rect(word.BoundingRect.X * scale, word.BoundingRect.Y * scale,
                word.BoundingRect.Width * scale, word.BoundingRect.Height * scale)))])];
    }

    /// <summary>
    /// The engine refuses anything larger than its limit outright, so a bigger image is read shrunk,
    /// <paramref name="scale"/> being the factor back to its pixels. Shrunk text may read less well,
    /// but a long scroll or a multi-monitor capture still gives up most of it.
    /// </summary>
    private static OcrResult Read(DecodedImage image, string? language, out int scale)
    {
        var engine = Engine(language) ?? throw new InvalidOperationException(
            "No text-recognition language is installed. Add one in Settings > Time & language > Language & region.");

        scale = Downsample.FactorToFit(image.Width, image.Height, (int)OcrEngine.MaxImageDimension);
        if (scale == 1) return Run(engine, image);
        var shrunk = Downsample.Box(image.Span, image.Width, image.Height, scale, out var width, out var height);
        using var smaller = DecodedImage.CopyFrom(shrunk, width, height);
        return Run(engine, smaller);
    }

    /// <summary>A chosen language since uninstalled falls back to the profile's rather than failing.</summary>
    private static OcrEngine? Engine(string? language) =>
        (language is null ? null : OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(language)))
        ?? OcrEngine.TryCreateFromUserProfileLanguages();

    private static OcrResult Run(OcrEngine engine, DecodedImage image)
    {
        using var bitmap = ToSoftwareBitmap(image);
        return engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Native to native through a WinRT buffer: a byte[] of a full-screen capture would sit
    /// on the large object heap.</summary>
    private static unsafe SoftwareBitmap ToSoftwareBitmap(DecodedImage image)
    {
        var length = (uint)image.ByteLength;
        var buffer = new Windows.Storage.Streams.Buffer(length) { Length = length };
        using var reference = ((IWinRTObject)buffer).NativeObject;

        var iid = IID_IBufferByteAccess;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(reference.ThisPtr, in iid, out var access));
        try
        {
            byte* bytes;
            var getBuffer = (delegate* unmanaged[Stdcall]<IntPtr, byte**, int>)(*(void***)access)[3];
            Marshal.ThrowExceptionForHR(getBuffer(access, &bytes));
            image.Span.CopyTo(new Span<byte>(bytes, (int)length));
        }
        finally { Marshal.Release(access); }

        return SoftwareBitmap.CreateCopyFromBuffer(buffer,
            BitmapPixelFormat.Bgra8, image.Width, image.Height, BitmapAlphaMode.Premultiplied);
    }

    private static readonly Guid IID_IBufferByteAccess = new("905a0fef-bc53-11df-8c49-001e4fc686da");
}
