using System.Runtime.InteropServices;
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

    private static string[] Recognize(DecodedImage image, string? language)
    {
        // A chosen language since uninstalled falls back to the profile's rather than failing.
        var engine = (language is null ? null : OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(language)))
            ?? OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException(
                "No text-recognition language is installed. Add one in Settings > Time & language > Language & region.");

        var limit = (int)OcrEngine.MaxImageDimension;
        if (image.Width > limit || image.Height > limit)
            throw new InvalidOperationException($"Text recognition reads images up to {limit} pixels on a side.");

        using var bitmap = ToSoftwareBitmap(image);
        var result = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();

        return [.. result.Lines.Select(line => line.Text)];
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
