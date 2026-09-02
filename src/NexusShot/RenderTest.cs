using System.Diagnostics;
using NexusShot.Core;
using NexusShot.Render;

namespace NexusShot;

/// <summary>
/// Headless verification of the drawing pipeline.
///
/// Every annotation here is produced by driving real gestures through <see cref="EditorDocument"/>
/// - the exact path a mouse takes - so this exercises the document, the renderer and the exporter
/// together, without a window. It also times a rapid drag.
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

        // A freehand pen stroke, then erase through its middle: the geometry-subtraction path.
        Stroke(document, EditorTool.Pen, "#FF3B30", 8, Wave(120, 460, 400, 30));
        Stroke(document, EditorTool.Eraser, "#000000", 30, [new(250, 460), new(310, 460)]);

        // A brush dab nicked by a smaller eraser tap: it must survive as a circle with a bite,
        // not vanish. Its zero-length centreline cannot go through Widen.
        Stroke(document, EditorTool.Brush, "#0A84FF", 60, [new(1200, 380)]);
        Stroke(document, EditorTool.Eraser, "#000000", 16, [new(1225, 380)]);

        // Brush effects: the GPU blur and pixelate.
        Stroke(document, EditorTool.Blur, "#000000", 10, Wave(560, 470, 300, 10));
        Stroke(document, EditorTool.Pixelate, "#000000", 10, Wave(900, 470, 300, 10));

        document.ActiveTool = EditorTool.Counter;
        document.ColorHex = "#0A84FF";
        document.BeginGesture(new Point(1200, 470));
        document.EndGesture(new Point(1200, 470));

        Console.WriteLine($"annotations: {document.Annotations.Count}");

        // The regression test: a fast drag. A frame is one pass over the annotation list, so the
        // cost must not climb with pointer speed.
        var target = document.Annotations[0];
        document.SelectAnnotation(target);
        var frames = TimeDrag(document, context, image, target);

        // The first frame carries one-off GPU work (effect shader compilation), so it is reported
        // apart from the steady state rather than dominating the maximum.
        var steady = frames.Skip(1).ToList();
        Console.WriteLine(
            $"drag: {frames.Count} frames, median {Median(frames):F3} ms, "
            + $"p95 {Percentile(steady, 0.95):F3} ms, max {steady.Max():F3} ms "
            + $"(first frame {frames[0]:F3} ms)");

        var output = Path.Combine(Path.GetDirectoryName(imagePath)!, "render-test.png");
        Exporter.SavePng(document, imagePath, output);
        Console.WriteLine($"exported {output}");

        // Crop. The session opens on the whole image and is resized by its handles, so this drags
        // the bottom-right corner in to leave a 400x260 frame, which the export must apply.
        document.ActiveTool = EditorTool.Crop;
        document.BeginCropSession();

        document.BeginGesture(new Point(image.Width, image.Height), 8);   // the bottom-right handle
        document.ContinueGesture(new Point(400, 260));
        document.EndGesture(new Point(400, 260));
        document.CommitCrop();

        var cropped = Path.Combine(Path.GetDirectoryName(imagePath)!, "render-test-cropped.png");
        Exporter.SavePng(document, imagePath, cropped);

        using var croppedImage = ImageSurface.Decode(cropped);
        var (croppedWidth, croppedHeight) = (croppedImage.Width, croppedImage.Height);
        Console.WriteLine($"cropped to {croppedWidth}x{croppedHeight} (expected 400x260)");

        if (croppedWidth != 400 || croppedHeight != 260)
            throw new InvalidOperationException(
                $"Crop was not applied on export: got {croppedWidth}x{croppedHeight}.");

        image.Dispose();
        VerifyTools(imagePath);
        VerifyRenderSemantics(Path.GetDirectoryName(imagePath)!);
    }

    private static void VerifyRenderSemantics(string directory)
    {
        var source = Path.Combine(directory, "pixel-probe-source.png");
        var output = Path.Combine(directory, "pixel-probe-result.png");
        using var canvas = DecodedImage.Allocate(128, 128);
        canvas.Span.Fill(255);
        PngWriter.Write(source, canvas);

        foreach (var tool in new[] { EditorTool.Rectangle, EditorTool.Ellipse, EditorTool.Line })
        {
            var document = new EditorDocument();
            document.SetImageSize(128, 128);
            Draw(document, tool, "#FF0000", 8, (32, 32), (96, 96));
            Exporter.SavePng(document, source, output);
            using var probe = ImageSurface.Decode(output);
            var pixels = probe.Span;
            CheckPixel(pixels, 4, 4, false); // no effect outside the annotation
            CheckPixel(pixels, 64, 64, tool == EditorTool.Line); // shapes remain hollow
            CheckPixel(pixels, 36, 36, tool != EditorTool.Ellipse); // ellipse is not a box
        }

        var erased = new EditorDocument();
        erased.SetImageSize(128, 128);
        Stroke(erased, EditorTool.Pen, "#FF0000", 16, [new(20, 64), new(108, 64)]);
        Stroke(erased, EditorTool.Eraser, "#000000", 24, [new(64, 64)]);
        Exporter.SavePng(erased, source, output);
        using var erasedProbe = ImageSurface.Decode(output);
        var erasedPixels = erasedProbe.Span;
        CheckPixel(erasedPixels, 32, 64, true);
        CheckPixel(erasedPixels, 64, 64, false);
        CheckPixel(erasedPixels, 96, 64, true);

        // A coordinate-coded image checks crop origin as well as dimensions, without deriving the
        // expected pixels from the renderer or its geometry helpers.
        for (var y = 0; y < 128; y++)
        for (var x = 0; x < 128; x++)
        {
            var offset = (y * 128 + x) * 4;
            canvas.Span[offset] = (byte)x;
            canvas.Span[offset + 1] = (byte)y;
            canvas.Span[offset + 2] = 0;
        }
        PngWriter.Write(source, canvas);
        var crop = new EditorDocument();
        crop.SetImageSize(128, 128);
        crop.BeginCropSession();
        crop.BeginGesture(new(128, 128));
        crop.EndGesture(new(64, 64));
        crop.BeginGesture(new(32, 32));
        crop.EndGesture(new(48, 48));
        crop.CommitCrop();
        Exporter.SavePng(crop, source, output);
        using var cropProbe = ImageSurface.Decode(output);
        var cropped = cropProbe.Span;
        var (width, height) = (cropProbe.Width, cropProbe.Height);
        if (width != 64 || height != 64 || cropped[0] != 16 || cropped[1] != 16
            || cropped[^4] != 79 || cropped[^3] != 79)
            throw new InvalidOperationException("Crop did not preserve the selected source coordinates.");
        Console.WriteLine("independent pixel probes: hollow shapes, line, eraser gap, crop origin passed");

        static void CheckPixel(ReadOnlySpan<byte> pixels, int x, int y, bool red)
        {
            var offset = (y * 128 + x) * 4;
            var low = red ? 0 : 255;
            if (pixels[offset] != low || pixels[offset + 1] != low
                || pixels[offset + 2] != 255 || pixels[offset + 3] != 255)
                throw new InvalidOperationException($"Expected {(red ? "red" : "white")} at {x},{y}.");
        }
    }

    private static void VerifyTools(string source)
    {
        using var originalImage = ImageSurface.Decode(source);
        var (width, height) = (originalImage.Width, originalImage.Height);
        var output = Path.Combine(Path.GetDirectoryName(source)!, "tool-check.png");
        foreach (var tool in Enum.GetValues<EditorTool>())
        {
            if (tool is EditorTool.Select or EditorTool.Crop or EditorTool.Eraser) continue;
            var document = new EditorDocument();
            document.SetImageSize(width, height);
            Draw(document, tool, "#FF3B30", 24, (80, 80), (420, 280));
            if (tool == EditorTool.Text) document.Annotations[0].Text = "Text export verification";
            Exporter.SavePng(document, source, output);
            using var drawn = ImageSurface.Decode(output);
            if (drawn.Width != width || drawn.Height != height || drawn.Span.SequenceEqual(originalImage.Span))
                throw new InvalidOperationException($"{tool} did not produce visible exported pixels.");

            document.Undo();
            Exporter.SavePng(document, source, output);
            using (var undone = ImageSurface.Decode(output))
            if (!undone.Span.SequenceEqual(originalImage.Span))
                throw new InvalidOperationException($"Undo did not restore the source for {tool}.");
            document.Redo();
            Exporter.SavePng(document, source, output);
            using (var redone = ImageSurface.Decode(output))
            if (!redone.Span.SequenceEqual(drawn.Span))
                throw new InvalidOperationException($"Redo changed the exported pixels for {tool}.");
            Console.WriteLine($"tool pixels + undo/redo: {tool} passed");
        }

        foreach (var tool in new[] { EditorTool.Blur, EditorTool.Pixelate })
        {
            var document = new EditorDocument();
            document.SetImageSize(width, height);
            Stroke(document, tool, "#000000", 24, [new Point(120, 100)]);
            Exporter.SavePng(document, source, output);
            using var probe = ImageSurface.Decode(output);
            var pixels = probe.Span;
            if (pixels.SequenceEqual(originalImage.Span))
                throw new InvalidOperationException($"{tool} single-click dab produced no effect.");
            Console.WriteLine($"single-click effect: {tool} passed");
        }

        // A destination locked by another process must survive a failed overwrite byte for byte.
        File.Copy(source, output, true);
        var before = File.ReadAllBytes(output);
        var empty = new EditorDocument();
        empty.SetImageSize(width, height);
        using (var locked = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                Exporter.SavePng(empty, source, output);
                throw new InvalidOperationException("Locked destination unexpectedly overwritten.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        if (!File.ReadAllBytes(output).AsSpan().SequenceEqual(before))
            throw new InvalidOperationException("Failed save damaged the destination.");
        Console.WriteLine("locked destination preservation: passed");
    }

    /// <summary>Drags the selection fast and renders a full frame per step, timing each one.</summary>
    private static List<double> TimeDrag(
        EditorDocument document, IComObject<ID2D1DeviceContext> context, ImageSurface image, Annotation target)
    {
        using var surface = CreateOffscreen(context, image.Width, image.Height);
        context.Object.SetTarget(surface.Object);

        using var resources = new D2DResources(context.AsRenderTarget2());
        using var renderer = new AnnotationRenderer(resources);
        using var effects = new PixelEffectSource(image, resources);

        var timings = new List<double>();
        var origin = target.Bounds;
        document.BeginGesture(new Point(origin.X + 10, origin.Y + 10));

        var watch = new Stopwatch();
        for (var i = 0; i < 120; i++)
        {
            // A deliberately violent path: big jumps and direction reversals.
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

    private static double Median(List<double> values) => Percentile(values, 0.5);

    private static double Percentile(List<double> values, double fraction)
    {
        var sorted = values.Order().ToList();
        var index = Math.Clamp((int)(sorted.Count * fraction), 0, sorted.Count - 1);
        return sorted[index];
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
