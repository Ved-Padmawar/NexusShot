using NexusShot.Core;
using NexusShot.Render;
using static NexusShot.Tests.Editing;

namespace NexusShot.Tests;

/// <summary>Selection frames, endpoint handles and the crop frame, drawn on screen but never exported.</summary>
public sealed class AdornerRenderTests : IDisposable
{
    private static readonly Rgba Green = Palette.Parse("#34C759");
    private readonly Offscreen _screen = new((int)ImageWidth, (int)ImageHeight);
    private readonly AnnotationRenderer _renderer;

    public AdornerRenderTests() => _renderer = new AnnotationRenderer(_screen.Resources);

    public void Dispose()
    {
        _renderer.Dispose();
        _screen.Dispose();
    }

    private int Count(Rgba color) =>
        (int)(ImageWidth * ImageHeight) - _screen.CountOther(new Rect(0, 0, ImageWidth, ImageHeight), color);

    private void DrawAdorners(EditorDocument document) =>
        _screen.Draw(Rgba.White, target => _renderer.DrawAdorners(target, document, 1));

    [Fact]
    public void ASelectedTextBoxAndLineAreFramedInTheirOwnColour()
    {
        var document = NewDocument();
        document.ColorHex = Green.ToHex();
        var line = Draw(document, EditorTool.Line, new Point(500, 500), new Point(800, 700));
        document.SelectAnnotation(line);
        DrawAdorners(document);
        var ring = Count(Green);

        var text = Draw(document, EditorTool.Text, new Point(100, 100), new Point(400, 200));
        document.SetTextContent(text, "note", text.Bounds);
        document.SelectAnnotation(text);
        DrawAdorners(document);

        Assert.True(ring > 20, "the line's endpoint handles are not in its colour");
        Assert.True(Count(Green) > 200, "the text box frame is not in its colour");
    }

    [Fact]
    public void TheCropFrameIsWhite()
    {
        var document = NewDocument();
        document.BeginCropSession();
        Drag(document, new Point(ImageWidth, ImageHeight), new Point(600, 500));
        _screen.Draw(Rgba.Black, target => _renderer.DrawAdorners(target, document, 1));

        Assert.Equal(Rgba.White, _screen.Pixel(300, 1));   // top edge, away from the grips
    }
}
