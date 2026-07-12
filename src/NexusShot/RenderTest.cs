using System.Diagnostics;
using NexusShot.Core;
using NexusShot.Render;

namespace NexusShot;

/// <summary>
/// Headless verification of the drawing pipeline.
///
/// Every annotation here is produced by driving real gestures through <see cref="EditorDocument"/>
/// - the exact path a mouse takes - so this exercises the document, the renderer and the exporter
/// together, and it can run without a window. It also times a rapid drag, which is the regression
/// this rewrite exists to fix.
/// </summary>
internal static class RenderTest
{
    public static void Run(string imagePath)
    {
        // An offscreen device context, so GPU effects (blur, pixelate) are available.
        var (device, factory) = D2DDevice.Create();
        using var _ = device;
        using var __ = factory;
        using var context = device.CreateDeviceContext();

        var image = ImageSurface.Load(imagePath, context);
        Console.WriteLine($"decoded {image.Width}x{image.Height}");

        var document = new EditorDocument();
        document.SetImageSize(image.Width, image.Height);

        Draw(document, EditorTool.Rectangle, "#FF3B30", 4, (120, 100), (520, 320));
        Draw(document, EditorTool.Ellipse, "#34C759", 4, (560, 100), (860, 320));
        Draw(document, EditorTool.Arrow, "#0A84FF", 5, (900, 120), (1290, 300));
        Draw(document, EditorTool.Line, "#FFCC00", 4, (120, 360), (520, 360));
        Draw(document, EditorTool.Highlight, "#FFCC00", 4, (560, 350), (860, 400));

        // A freehand pen stroke, then erase through its middle: exercises the geometry-subtraction
        // path that replaced the software pixel mask.
        Stroke(document, EditorTool.Pen, "#FF3B30", 8, Wave(120, 460, 400, 30));
        Stroke(document, EditorTool.Eraser, "#000000", 30, [new(250, 460), new(310, 460)]);

        // Brush effects: the GPU blur and pixelate.
        Stroke(document, EditorTool.Blur, "#000000", 10, Wave(560, 470, 300, 10));
        Stroke(document, EditorTool.Pixelate, "#000000", 10, Wave(900, 470, 300, 10));

        document.ActiveTool = EditorTool.Counter;
        document.ColorHex = "#0A84FF";
        document.BeginGesture(new Point(1200, 470));
        document.EndGesture(new Point(1200, 470));

        Console.WriteLine($"annotations: {document.Annotations.Count}");

        // The regression test: a fast drag. In the XAML build this got slower the faster you moved,
        // because each move patched a retained visual tree. Here a frame is a pass over the list.
        var target = document.Annotations[0];
        document.SelectAnnotation(target);
        var frames = TimeDrag(document, context, image, target);
        Console.WriteLine($"drag: {frames.Count} frames, median {Median(frames):F3} ms, max {frames.Max():F3} ms");

        var output = Path.Combine(Path.GetDirectoryName(imagePath)!, "render-test.png");
        Exporter.SavePng(document, imagePath, output);
        Console.WriteLine($"exported {output}");
        image.Dispose();
    }

    /// <summary>Drags the selection fast and renders a full frame per step, timing each one.</summary>
    private static List<double> TimeDrag(
        EditorDocument document, IComObject<ID2D1DeviceContext> context, ImageSurface image, Annotation target)
    {
        using var surface = CreateOffscreen(context, image.Width, image.Height);
        context.Object.SetTarget(surface.Object);

        using var resources = new D2DResources(context.AsRenderTarget2());
        var renderer = new AnnotationRenderer(resources);
        using var effects = new PixelEffectSource(image, resources);

        var timings = new List<double>();
        var origin = target.Bounds;
        document.BeginGesture(new Point(origin.X + 10, origin.Y + 10));

        var watch = new Stopwatch();
        for (var i = 0; i < 120; i++)
        {
            // A deliberately violent path: big jumps, direction reversals - the "move fast, stop
            // dead, move fast again" pattern that exposed the old lag.
            var x = origin.X + 10 + Math.Sin(i / 3.0) * 260;
            var y = origin.Y + 10 + Math.Cos(i / 5.0) * 160;
            document.ContinueGesture(new Point(x, y));

            watch.Restart();
            context.BeginDraw();
            context.Clear(new D3DCOLORVALUE(0, 0, 0, 0));
            renderer.DrawAnnotations(context.AsRenderTarget2(), document, effects);
            renderer.DrawAdorners(context.AsRenderTarget2(), document, 1);
            context.EndDraw();
            watch.Stop();

            timings.Add(watch.Elapsed.TotalMilliseconds);
        }
        document.EndGesture(new Point(origin.X + 10, origin.Y + 10));
        context.Object.SetTarget(null);
        return timings;
    }

    private static void Draw(
        EditorDocument document, EditorTool tool, string color, double thickness,
        (double X, double Y) from, (double X, double Y) to)
    {
        document.ActiveTool = tool;
        document.ColorHex = color;
        document.StrokeThickness = thickness;
        document.BeginGesture(new Point(from.X, from.Y));
        document.ContinueGesture(new Point(to.X, to.Y));
        document.EndGesture(new Point(to.X, to.Y));
    }

    private static void Stroke(
        EditorDocument document, EditorTool tool, string color, double thickness, IReadOnlyList<Point> path)
    {
        document.ActiveTool = tool;
        document.ColorHex = color;
        document.SetStrokeThickness(thickness);
        document.BeginGesture(path[0]);
        for (var i = 1; i < path.Count; i++) document.ContinueGesture(path[i]);
        document.EndGesture(path[^1]);
    }

    private static List<Point> Wave(double x, double y, double length, double amplitude)
    {
        var points = new List<Point>();
        for (var i = 0; i <= 60; i++)
        {
            var t = i / 60.0;
            points.Add(new Point(x + length * t, y + Math.Sin(t * Math.PI * 3) * amplitude));
        }
        return points;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    private static IComObject<ID2D1Bitmap1> CreateOffscreen(
        IComObject<ID2D1DeviceContext> context, int width, int height) =>
        context.CreateBitmap<ID2D1Bitmap1>(
            new D2D_SIZE_U { width = (uint)width, height = (uint)height },
            new D2D1_BITMAP_PROPERTIES1
            {
                pixelFormat = new D2D1_PIXEL_FORMAT
                {
                    format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                    alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED,
                },
                dpiX = 96,
                dpiY = 96,
                bitmapOptions = D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET,
            });
}
