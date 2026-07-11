using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using NexusShot.App.Enums;
using NexusShot.App.Models;
using Rect = Windows.Foundation.Rect;

namespace NexusShot.App.Editor;

/// <summary>
/// Composites annotations onto the captured bitmap with GDI+ at true pixel resolution.
/// Deliberately not a screen capture of the editor canvas: exports must be independent of
/// window size, scroll position and display DPI.
/// </summary>
public sealed class AnnotationFlattener : IAnnotationFlattener
{
    public Task FlattenAsync(
        string sourcePath,
        string destinationPath,
        IReadOnlyList<Annotation> annotations,
        Rect? cropBounds,
        CancellationToken cancellationToken) =>
        Task.Run(() => Flatten(sourcePath, destinationPath, annotations, cropBounds, cancellationToken), cancellationToken);

    private static void Flatten(
        string sourcePath,
        string destinationPath,
        IReadOnlyList<Annotation> annotations,
        Rect? cropBounds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Copy off the decoded source so the file handle is released before we may overwrite it.
        using var canvas = LoadDetachedCopy(sourcePath);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            foreach (var annotation in annotations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Draw(graphics, canvas, annotation);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (cropBounds is { } crop)
        {
            using var cropped = Crop(canvas, crop);
            Save(cropped, destinationPath);
            return;
        }
        Save(canvas, destinationPath);
    }

    private static Bitmap LoadDetachedCopy(string path)
    {
        using var source = Image.FromFile(path);
        var copy = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(copy);
        graphics.DrawImageUnscaled(source, 0, 0);
        return copy;
    }

    private static void Save(Bitmap bitmap, string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        bitmap.Save(destinationPath, ImageFormat.Png);
    }

    private static void Draw(Graphics graphics, Bitmap canvas, Annotation annotation)
    {
        var color = ParseColor(annotation.ColorHex);
        var thickness = (float)Math.Max(1, annotation.StrokeThickness);
        var bounds = ToRectangle(annotation.Bounds);

        switch (annotation.Tool)
        {
            case EditorTool.Rectangle:
                using (var pen = new Pen(color, thickness)) graphics.DrawRectangle(pen, bounds);
                break;

            case EditorTool.Ellipse:
                using (var pen = new Pen(color, thickness)) graphics.DrawEllipse(pen, bounds);
                break;

            case EditorTool.Line:
                using (var pen = new Pen(color, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    graphics.DrawLine(pen, ToPointF(annotation.Start), ToPointF(annotation.End));
                break;

            case EditorTool.Arrow:
                DrawArrow(graphics, color, thickness, ToPointF(annotation.Start), ToPointF(annotation.End));
                break;

            case EditorTool.Pen:
            case EditorTool.Brush:
                if (annotation.Points.Count == 0) break;
                DrawPaintStroke(graphics, annotation, color, thickness);
                break;

            case EditorTool.Highlight:
                // Multiply-style wash: a translucent fill reads as a marker over the underlying pixels.
                using (var brush = new SolidBrush(Color.FromArgb(90, color)))
                    graphics.FillRectangle(brush, bounds);
                break;

            case EditorTool.Blur:
            case EditorTool.Pixelate:
                DrawBrushEffect(graphics, canvas, annotation);
                break;

            case EditorTool.Spotlight:
                DrawSpotlight(graphics, canvas, bounds);
                break;

            case EditorTool.Counter:
                DrawCounter(graphics, color, annotation);
                break;

            case EditorTool.Text:
                DrawText(graphics, color, annotation);
                break;

            case EditorTool.Select:
            case EditorTool.Eraser:
            case EditorTool.Crop:
                break;
        }
    }

    private static void DrawPaintStroke(Graphics graphics, Annotation annotation, Color color, float thickness)
    {
        if (annotation.Erasures.Count == 0)
        {
            using var pen = new Pen(color, thickness)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            DrawRoundStroke(graphics, pen, annotation.Points.Select(ToPointF).ToArray());
            return;
        }

        var radius = annotation.StrokeThickness / 2 + 2;
        var left = (int)Math.Floor(annotation.Points.Min(p => p.X) - radius);
        var top = (int)Math.Floor(annotation.Points.Min(p => p.Y) - radius);
        var right = (int)Math.Ceiling(annotation.Points.Max(p => p.X) + radius);
        var bottom = (int)Math.Ceiling(annotation.Points.Max(p => p.Y) + radius);
        using var layer = new Bitmap(Math.Max(1, right - left), Math.Max(1, bottom - top), PixelFormat.Format32bppPArgb);
        using (var layerGraphics = Graphics.FromImage(layer))
        {
            layerGraphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var paint = new Pen(color, thickness)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            DrawRoundStroke(layerGraphics, paint, annotation.Points
                .Select(p => new PointF((float)(p.X - left), (float)(p.Y - top))).ToArray());

            layerGraphics.CompositingMode = CompositingMode.SourceCopy;
            foreach (var mask in annotation.Erasures)
            {
                using var erase = new Pen(Color.Transparent, (float)(mask.Radius * 2))
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round,
                };
                var points = mask.Points
                    .Select(p => new PointF((float)(p.X - left), (float)(p.Y - top))).ToArray();
                DrawRoundStroke(layerGraphics, erase, points);
            }
        }
        graphics.DrawImageUnscaled(layer, left, top);
    }

    private static void DrawRoundStroke(Graphics graphics, Pen pen, PointF[] points)
    {
        if (points.Length == 0) return;
        if (points.Length > 1 && points.Any(point => point != points[0]))
        {
            graphics.DrawLines(pen, points);
            return;
        }

        using var dab = new SolidBrush(pen.Color);
        var radius = pen.Width / 2;
        graphics.FillEllipse(dab, points[0].X - radius, points[0].Y - radius, pen.Width, pen.Width);
    }

    private static void DrawArrow(Graphics graphics, Color color, float thickness, PointF start, PointF end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 1) return;

        var headLength = Math.Min(length, thickness * 5);
        var angle = MathF.Atan2(dy, dx);

        // Stop the shaft short of the tip so the filled head does not blunt the point.
        var shaftEnd = new PointF(end.X - MathF.Cos(angle) * headLength * 0.8f, end.Y - MathF.Sin(angle) * headLength * 0.8f);
        using (var pen = new Pen(color, thickness) { StartCap = LineCap.Round })
            graphics.DrawLine(pen, start, shaftEnd);

        const float spread = 0.45f;
        var head = new[]
        {
            end,
            new PointF(end.X - MathF.Cos(angle - spread) * headLength, end.Y - MathF.Sin(angle - spread) * headLength),
            new PointF(end.X - MathF.Cos(angle + spread) * headLength, end.Y - MathF.Sin(angle + spread) * headLength),
        };
        using var brush = new SolidBrush(color);
        graphics.FillPolygon(brush, head);
    }

    private static void DrawSpotlight(Graphics graphics, Bitmap canvas, Rectangle bounds)
    {
        // Dim everything except the region, by filling the four bands around it.
        using var shade = new SolidBrush(Color.FromArgb(140, 0, 0, 0));
        var full = new Rectangle(0, 0, canvas.Width, canvas.Height);
        var region = Rectangle.Intersect(bounds, full);
        if (region.IsEmpty)
        {
            graphics.FillRectangle(shade, full);
            return;
        }
        graphics.FillRectangle(shade, new Rectangle(0, 0, full.Width, region.Top));
        graphics.FillRectangle(shade, new Rectangle(0, region.Bottom, full.Width, full.Height - region.Bottom));
        graphics.FillRectangle(shade, new Rectangle(0, region.Top, region.Left, region.Height));
        graphics.FillRectangle(shade, new Rectangle(region.Right, region.Top, full.Width - region.Right, region.Height));
    }

    private static void DrawCounter(Graphics graphics, Color color, Annotation annotation)
    {
        var diameter = (float)Math.Max(28, annotation.StrokeThickness * 8);
        var circle = new RectangleF((float)annotation.Start.X - diameter / 2, (float)annotation.Start.Y - diameter / 2, diameter, diameter);

        using var fill = new SolidBrush(color);
        graphics.FillEllipse(fill, circle);

        using var font = new Font("Segoe UI", diameter * 0.45f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(ContrastingForeground(color));
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(annotation.CounterValue.ToString(), font, text, circle, format);
    }

    private static void DrawText(Graphics graphics, Color color, Annotation annotation)
    {
        if (string.IsNullOrEmpty(annotation.Text)) return;
        var size = (float)Math.Max(8, annotation.FontSize);
        var style = FontStyle.Regular;
        if (annotation.IsBold) style |= FontStyle.Bold;
        if (annotation.IsItalic) style |= FontStyle.Italic;
        if (annotation.IsUnderline) style |= FontStyle.Underline;
        using var font = new Font("Segoe UI", size, style, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);

        var bounds = annotation.Bounds;
        if (bounds.Width > 1)
        {
            // Wrap to the annotation's box, matching the editor preview. Height is left open so
            // text that outgrew the box during editing is not clipped out of the export.
            var layout = new RectangleF((float)bounds.X, (float)bounds.Y, (float)bounds.Width, float.MaxValue);
            graphics.DrawString(annotation.Text, font, brush, layout);
            return;
        }
        graphics.DrawString(annotation.Text, font, brush, (float)annotation.Start.X, (float)annotation.Start.Y);
    }

    /// <summary>
    /// Composites a painted blur/pixelate stroke. Reads the canvas as it currently stands, so a
    /// stroke over an earlier annotation redacts the annotated pixels too, matching the preview.
    /// </summary>
    private static void DrawBrushEffect(Graphics graphics, Bitmap canvas, Annotation annotation)
    {
        PointF[] path = annotation.Points.Count > 0
            ? annotation.Points.Select(ToPointF).ToArray()
            : [ToPointF(annotation.Start), ToPointF(annotation.End)];

        var full = new Rectangle(0, 0, canvas.Width, canvas.Height);
        var data = canvas.LockBits(full, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        (Rectangle Region, byte[] PremultipliedBgra)? stroke;
        try
        {
            var pixels = new byte[Math.Abs(data.Stride) * data.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            stroke = PixelEffects.BrushStroke(
                pixels, data.Stride, path, (float)annotation.BrushRadius, annotation.Tool == EditorTool.Pixelate);
        }
        finally
        {
            canvas.UnlockBits(data);
        }

        if (stroke is not { } result) return;
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(
            result.PremultipliedBgra, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            using var overlay = new Bitmap(
                result.Region.Width, result.Region.Height, result.Region.Width * 4,
                PixelFormat.Format32bppPArgb, handle.AddrOfPinnedObject());
            graphics.DrawImageUnscaled(overlay, result.Region.X, result.Region.Y);
        }
        finally
        {
            handle.Free();
        }
    }

    private static Bitmap Crop(Bitmap canvas, Rect crop)
    {
        var region = Rectangle.Intersect(ToRectangle(crop), new Rectangle(0, 0, canvas.Width, canvas.Height));
        if (region.Width <= 0 || region.Height <= 0) return (Bitmap)canvas.Clone();
        return canvas.Clone(region, PixelFormat.Format32bppArgb);
    }

    private static Color ContrastingForeground(Color background) =>
        background.R * 0.299 + background.G * 0.587 + background.B * 0.114 > 150 ? Color.Black : Color.White;

    private static Rectangle ToRectangle(Rect rect) => new(
        (int)Math.Round(rect.X),
        (int)Math.Round(rect.Y),
        Math.Max(1, (int)Math.Round(rect.Width)),
        Math.Max(1, (int)Math.Round(rect.Height)));

    private static PointF ToPointF(Windows.Foundation.Point point) => new((float)point.X, (float)point.Y);

    private static Color ParseColor(string hex)
    {
        var value = hex.TrimStart('#');
        if (value.Length == 6 && int.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        return Color.Red;
    }
}
