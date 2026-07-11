using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using NexusShot.App.Enums;
using NexusShot.App.Models;
using Windows.Foundation;
using Windows.UI;

namespace NexusShot.App.Editor;

/// <summary>
/// Draws the document's annotations onto a XAML canvas for on-screen preview.
/// Export still goes through <see cref="IAnnotationFlattener"/>, but blur and pixelate previews
/// run the same <see cref="PixelEffects"/> code over <see cref="EditorPixelSource"/>, so the
/// preview shows the pixels the export will produce.
///
/// Every visual is tagged with its annotation's id, which lets <see cref="UpdateActive"/> replace
/// one annotation's visuals at pointer-move rate instead of rebuilding the whole canvas.
/// </summary>
public static class LiveAnnotationRenderer
{
    private const string SelectionAdornerTag = "selection-adorner";
    private const string CropAdornerTag = "crop-adorner";

    private static readonly Color SelectionColor = Color.FromArgb(255, 10, 132, 255);

    /// <summary>Rebuilds <paramref name="canvas"/> from scratch. Called on structural changes
    /// (add/remove/undo/redo), not per pointer move. <paramref name="adornerScale"/> is the
    /// inverse of the display scale, so handles keep a constant on-screen size however far the
    /// image is zoomed out.</summary>
    public static void Render(Canvas canvas, EditorDocument document, EditorPixelSource? source, double adornerScale = 1)
    {
        canvas.Children.Clear();
        foreach (var annotation in document.Annotations)
            AppendVisuals(canvas, annotation, source);

        if (document.Selected is { } selected) AppendSelectionAdorner(canvas, selected, adornerScale);
        AppendCropVisuals(canvas, document, adornerScale);
    }

    /// <summary>
    /// Replaces only <paramref name="annotation"/>'s visuals. The annotation temporarily paints
    /// on top of later ones while dragged; the full render on gesture end restores z-order.
    /// </summary>
    public static void UpdateActive(Canvas canvas, EditorDocument document, Annotation annotation, EditorPixelSource? source, double adornerScale = 1)
    {
        // A pen stroke only grows, so extend the polyline in place instead of recreating it.
        if (annotation.Tool is EditorTool.Pen or EditorTool.Brush or EditorTool.Eraser
            && TryExtendPolyline(canvas, annotation)) return;

        RemoveTagged(canvas, annotation.Id);
        RemoveTagged(canvas, SelectionAdornerTag);
        AppendVisuals(canvas, annotation, source);
        if (document.Selected == annotation) AppendSelectionAdorner(canvas, annotation, adornerScale);
    }

    /// <summary>Removes the selection grips; used while the inline text editor covers them.</summary>
    public static void RemoveSelectionAdorner(Canvas canvas) => RemoveTagged(canvas, SelectionAdornerTag);

    /// <summary>Refreshes only the crop adorner, at pointer-move rate during a crop drag.</summary>
    public static void UpdateCropAdorner(Canvas canvas, EditorDocument document, double adornerScale = 1)
    {
        RemoveTagged(canvas, CropAdornerTag);
        AppendCropVisuals(canvas, document, adornerScale);
    }

    private static void AppendVisuals(Canvas canvas, Annotation annotation, EditorPixelSource? source)
    {
        foreach (var visual in CreateVisuals(annotation, source, canvas.Width, canvas.Height))
        {
            visual.Tag = annotation.Id;
            canvas.Children.Add(visual);
        }
    }

    private static bool TryExtendPolyline(Canvas canvas, Annotation annotation)
    {
        var polyline = canvas.Children.OfType<Polyline>()
            .FirstOrDefault(p => p.Tag is Guid id && id == annotation.Id);
        if (polyline is null) return false;

        for (var i = polyline.Points.Count; i < annotation.Points.Count; i++)
            polyline.Points.Add(annotation.Points[i]);
        return true;
    }

    private static void RemoveTagged(Canvas canvas, object tag)
    {
        for (var i = canvas.Children.Count - 1; i >= 0; i--)
        {
            if (canvas.Children[i] is FrameworkElement element && Equals(element.Tag, tag))
                canvas.Children.RemoveAt(i);
        }
    }

    private static IEnumerable<FrameworkElement> CreateVisuals(
        Annotation annotation, EditorPixelSource? source, double canvasWidth, double canvasHeight)
    {
        var color = ParseColor(annotation.ColorHex);
        var bounds = annotation.Bounds;

        switch (annotation.Tool)
        {
            case EditorTool.Rectangle:
                yield return Place(new Rectangle
                {
                    Width = bounds.Width,
                    Height = bounds.Height,
                    Stroke = new SolidColorBrush(color),
                    StrokeThickness = annotation.StrokeThickness,
                }, bounds);
                break;

            case EditorTool.Ellipse:
                yield return Place(new Ellipse
                {
                    Width = bounds.Width,
                    Height = bounds.Height,
                    Stroke = new SolidColorBrush(color),
                    StrokeThickness = annotation.StrokeThickness,
                }, bounds);
                break;

            case EditorTool.Line:
                yield return CreateLine(annotation.Start, annotation.End, color, annotation.StrokeThickness);
                break;

            case EditorTool.Arrow:
                yield return CreateLine(annotation.Start, ShaftEnd(annotation), color, annotation.StrokeThickness);
                yield return CreateArrowHead(annotation, color);
                break;

            case EditorTool.Pen:
            case EditorTool.Brush:
            case EditorTool.Eraser:
                if (annotation.Points.Count == 0) break;
                if (annotation.Tool != EditorTool.Eraser && annotation.Erasures.Count > 0)
                {
                    yield return CreateMaskedStrokeVisual(annotation, color);
                    break;
                }
                if (IsPointDab(annotation.Points))
                {
                    var diameter = PaintStrokeGeometry.Diameter(annotation.StrokeThickness);
                    var center = annotation.Points[0];
                    yield return Place(new Ellipse
                    {
                        Width = diameter,
                        Height = diameter,
                        Fill = new SolidColorBrush(color),
                    }, new Rect(center.X - diameter / 2, center.Y - diameter / 2, diameter, diameter));
                    break;
                }
                var polyline = new Polyline
                {
                    Stroke = new SolidColorBrush(annotation.Tool == EditorTool.Eraser
                        ? Color.FromArgb(150, SelectionColor.R, SelectionColor.G, SelectionColor.B)
                        : color),
                    StrokeThickness = annotation.StrokeThickness,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                };
                foreach (var point in annotation.Points) polyline.Points.Add(point);
                yield return polyline;
                break;

            case EditorTool.Highlight:
                yield return Place(new Rectangle
                {
                    Width = bounds.Width,
                    Height = bounds.Height,
                    Fill = new SolidColorBrush(Color.FromArgb(90, color.R, color.G, color.B)),
                }, bounds);
                break;

            case EditorTool.Blur:
            case EditorTool.Pixelate:
                if (annotation.Points.Count == 0) break;
                if (source is not null && CreateBrushEffectVisual(annotation, source) is { } effect)
                {
                    yield return effect;
                    break;
                }
                // Pixels not decoded yet: a frosted stroke placeholder until the source arrives.
                var placeholder = new Polyline
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
                    StrokeThickness = annotation.BrushRadius * 2,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                };
                foreach (var point in annotation.Points) placeholder.Points.Add(point);
                yield return placeholder;
                break;

            case EditorTool.Spotlight:
                // Matches the export: everything outside the region is dimmed.
                foreach (var shade in CreateDimAround(bounds, canvasWidth, canvasHeight, 140))
                    yield return shade;
                break;

            case EditorTool.Counter:
                yield return CreateCounter(annotation, color);
                break;

            case EditorTool.Text:
                // The annotation's box is the layout: text wraps to its width, so the on-screen
                // preview matches what the editing TextBox showed and what the export will draw.
                var text = new TextBlock
                {
                    Text = annotation.Text,
                    Foreground = new SolidColorBrush(color),
                    FontSize = Math.Max(8, annotation.FontSize),
                    FontWeight = annotation.IsBold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                    FontStyle = annotation.IsItalic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
                    TextDecorations = annotation.IsUnderline ? Windows.UI.Text.TextDecorations.Underline : Windows.UI.Text.TextDecorations.None,
                    TextWrapping = TextWrapping.Wrap,
                    Width = Math.Max(10, bounds.Width),
                };
                yield return Place(text, bounds);
                break;

            case EditorTool.Select:
            case EditorTool.Crop: // Crop is a session over the document, never a drawn annotation.
                break;
        }
    }

    private static FrameworkElement CreateMaskedStrokeVisual(Annotation annotation, Color color)
    {
        var radius = annotation.StrokeThickness / 2 + 2;
        var left = Math.Floor(annotation.Points.Min(p => p.X) - radius);
        var top = Math.Floor(annotation.Points.Min(p => p.Y) - radius);
        var right = Math.Ceiling(annotation.Points.Max(p => p.X) + radius);
        var bottom = Math.Ceiling(annotation.Points.Max(p => p.Y) + radius);
        var width = Math.Max(1, (int)(right - left));
        var height = Math.Max(1, (int)(bottom - top));

        using var bitmap = new System.Drawing.Bitmap(
            width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var paint = new System.Drawing.Pen(
                System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B),
                (float)annotation.StrokeThickness)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
            };
            var paintPoints = annotation.Points
                .Select(p => new System.Drawing.PointF((float)(p.X - left), (float)(p.Y - top))).ToArray();
            DrawRoundStroke(graphics, paint, paintPoints);

            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            foreach (var mask in annotation.Erasures)
            {
                using var erase = new System.Drawing.Pen(
                    System.Drawing.Color.Transparent, (float)(mask.Radius * 2))
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.Round,
                    LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
                };
                var points = mask.Points
                    .Select(p => new System.Drawing.PointF((float)(p.X - left), (float)(p.Y - top))).ToArray();
                DrawRoundStroke(graphics, erase, points);
            }
        }

        var data = bitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            var pixels = new byte[data.Stride * height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            var source = new WriteableBitmap(width, height);
            using var buffer = source.PixelBuffer.AsStream();
            buffer.Write(pixels, 0, pixels.Length);
            source.Invalidate();
            return Place(new Image { Source = source, Width = width, Height = height },
                new Rect(left, top, width, height));
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static bool IsPointDab(IReadOnlyList<Point> points) =>
        points.Count == 1 || points.All(point => point == points[0]);

    private static void DrawRoundStroke(
        System.Drawing.Graphics graphics,
        System.Drawing.Pen pen,
        System.Drawing.PointF[] points)
    {
        if (points.Length == 0) return;
        if (points.Length > 1 && points.Any(point => point != points[0]))
        {
            graphics.DrawLines(pen, points);
            return;
        }

        using var dab = new System.Drawing.SolidBrush(pen.Color);
        var radius = pen.Width / 2;
        graphics.FillEllipse(dab, points[0].X - radius, points[0].Y - radius, pen.Width, pen.Width);
    }

    /// <summary>Runs the real painted blur/pixelate over the source pixels and shows the stroke
    /// as a masked image positioned exactly over its region. Same code path as the export.</summary>
    private static FrameworkElement? CreateBrushEffectVisual(Annotation annotation, EditorPixelSource source)
    {
        var path = annotation.Points
            .Select(p => new System.Drawing.PointF((float)p.X, (float)p.Y))
            .ToArray();

        var stroke = PixelEffects.BrushStroke(
            source.Pixels, source.Stride, path, (float)annotation.BrushRadius, annotation.Tool == EditorTool.Pixelate);
        if (stroke is not { } result) return null;

        var region = result.Region;
        var bitmap = new WriteableBitmap(region.Width, region.Height);
        using (var buffer = bitmap.PixelBuffer.AsStream())
            buffer.Write(result.PremultipliedBgra, 0, result.PremultipliedBgra.Length);
        bitmap.Invalidate();

        var image = new Image
        {
            Source = bitmap,
            Width = region.Width,
            Height = region.Height,
            Stretch = Stretch.Fill,
        };
        return Place(image, new Rect(region.X, region.Y, region.Width, region.Height));
    }

    private static void AppendCropVisuals(Canvas canvas, EditorDocument document, double adornerScale)
    {
        // A live crop session draws the interactive frame; otherwise the committed crop, if any,
        // keeps its passive dim-plus-dashed-frame presentation.
        if (document.PendingCrop is { } pending)
        {
            foreach (var visual in CreateCropSessionVisuals(pending, canvas.Width, canvas.Height, adornerScale))
            {
                visual.Tag = CropAdornerTag;
                canvas.Children.Add(visual);
            }
            return;
        }

        if (document.CropBounds is not { } crop) return;
        foreach (var visual in CreateCropRegionVisuals(crop, canvas.Width, canvas.Height))
        {
            visual.Tag = CropAdornerTag;
            canvas.Children.Add(visual);
        }
    }

    /// <summary>The interactive crop frame: dim outside, a thin frame, L-shaped corner grips and
    /// short edge bars, all drawn inside the frame so nothing extends past the image. Each grip
    /// is a white stroke over a dark underlay so it reads on light and dark content alike.</summary>
    private static IEnumerable<FrameworkElement> CreateCropSessionVisuals(
        Rect crop, double canvasWidth, double canvasHeight, double adornerScale)
    {
        foreach (var shade in CreateDimAround(crop, canvasWidth, canvasHeight, 150))
            yield return shade;

        // Frame stroke inset by half its thickness so it never paints outside the crop rect.
        var frameThickness = 1.5 * adornerScale;
        var inset = frameThickness / 2;
        yield return Place(new Rectangle
        {
            Width = Math.Max(0, crop.Width - frameThickness),
            Height = Math.Max(0, crop.Height - frameThickness),
            Stroke = new SolidColorBrush(SelectionColor),
            StrokeThickness = frameThickness,
            IsHitTestVisible = false,
        }, new Rect(crop.X + inset, crop.Y + inset, Math.Max(0, crop.Width - frameThickness), Math.Max(0, crop.Height - frameThickness)));

        foreach (var grip in CreateBoxGrips(crop, adornerScale))
            yield return grip;
    }

    /// <summary>L-shaped corner grips and short edge bars for a resizable box, drawn inside its
    /// bounds: a white stroke over a dark underlay, readable on any content.</summary>
    private static IEnumerable<FrameworkElement> CreateBoxGrips(
        Rect bounds, double adornerScale, bool onBorder = false)
    {
        var thickness = 4 * adornerScale;
        var underlayPad = 1.5 * adornerScale;
        // Arms shrink on tiny boxes so opposing grips never overlap or cross.
        var arm = Math.Min(18 * adornerScale, Math.Min(bounds.Width, bounds.Height) / 3);
        var bar = Math.Min(26 * adornerScale, Math.Min(bounds.Width, bounds.Height) / 3);

        foreach (var (handle, _) in EditorDocument.BoxHandlePositions(bounds))
        {
            var inset = onBorder ? 0 : thickness / 2 + underlayPad;
            var points = BoxGripPath(handle, bounds, arm, bar, inset);
            if (points is null) continue;

            yield return CreateGripStroke(points, Color.FromArgb(170, 0, 0, 0), thickness + underlayPad * 2);
            yield return CreateGripStroke(points, Microsoft.UI.Colors.White, thickness);
        }
    }

    /// <summary>The centreline of a grip, offset inward by <paramref name="inset"/> so the full
    /// stroke stays inside the box. Shared with the inline text editor's grips.</summary>
    public static Point[]? BoxGripPath(ResizeHandle handle, Rect bounds, double arm, double bar, double inset)
    {
        var (l, t, r, b) = (bounds.Left + inset, bounds.Top + inset, bounds.Right - inset, bounds.Bottom - inset);
        if (r < l || b < t) return null;
        var cx = bounds.Left + bounds.Width / 2;
        var cy = bounds.Top + bounds.Height / 2;
        var half = bar / 2;

        return handle switch
        {
            ResizeHandle.TopLeft => [new(l + arm, t), new(l, t), new(l, t + arm)],
            ResizeHandle.TopRight => [new(r - arm, t), new(r, t), new(r, t + arm)],
            ResizeHandle.BottomLeft => [new(l, b - arm), new(l, b), new(l + arm, b)],
            ResizeHandle.BottomRight => [new(r, b - arm), new(r, b), new(r - arm, b)],
            ResizeHandle.Top => [new(cx - half, t), new(cx + half, t)],
            ResizeHandle.Bottom => [new(cx - half, b), new(cx + half, b)],
            ResizeHandle.Left => [new(l, cy - half), new(l, cy + half)],
            ResizeHandle.Right => [new(r, cy - half), new(r, cy + half)],
            _ => null,
        };
    }

    /// <summary>A rounded polyline for one grip's centreline. Shared with the text editor.</summary>
    public static Polyline CreateGripStroke(Point[] points, Color color, double thickness)
    {
        var polyline = new Polyline
        {
            Stroke = new SolidColorBrush(color),
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        };
        foreach (var point in points) polyline.Points.Add(point);
        return polyline;
    }

    /// <summary>Dim outside the crop plus a dashed frame, so the kept region reads as the result.</summary>
    private static IEnumerable<FrameworkElement> CreateCropRegionVisuals(Rect crop, double canvasWidth, double canvasHeight)
    {
        foreach (var shade in CreateDimAround(crop, canvasWidth, canvasHeight, 150))
            yield return shade;

        yield return Place(new Rectangle
        {
            Width = crop.Width,
            Height = crop.Height,
            Stroke = new SolidColorBrush(SelectionColor),
            StrokeThickness = 2,
            StrokeDashArray = [5, 3],
            IsHitTestVisible = false,
        }, crop);
    }

    /// <summary>The four shade bands around <paramref name="region"/>, clipped to the canvas.</summary>
    private static IEnumerable<FrameworkElement> CreateDimAround(Rect region, double canvasWidth, double canvasHeight, byte alpha)
    {
        if (double.IsNaN(canvasWidth) || double.IsNaN(canvasHeight)) yield break;

        var brush = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
        var top = Math.Clamp(region.Top, 0, canvasHeight);
        var bottom = Math.Clamp(region.Bottom, 0, canvasHeight);
        var left = Math.Clamp(region.Left, 0, canvasWidth);
        var right = Math.Clamp(region.Right, 0, canvasWidth);

        var bands = new[]
        {
            new Rect(0, 0, canvasWidth, top),
            new Rect(0, bottom, canvasWidth, canvasHeight - bottom),
            new Rect(0, top, left, bottom - top),
            new Rect(right, top, canvasWidth - right, bottom - top),
        };

        foreach (var band in bands)
        {
            if (band.Width <= 0 || band.Height <= 0) continue;
            yield return Place(new Rectangle
            {
                Width = band.Width,
                Height = band.Height,
                Fill = brush,
                IsHitTestVisible = false,
            }, band);
        }
    }

    private static Point ShaftEnd(Annotation annotation)
    {
        var dx = annotation.End.X - annotation.Start.X;
        var dy = annotation.End.Y - annotation.Start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1) return annotation.End;
        var head = Math.Min(length, annotation.StrokeThickness * 5) * 0.8;
        return new Point(annotation.End.X - dx / length * head, annotation.End.Y - dy / length * head);
    }

    private static FrameworkElement CreateArrowHead(Annotation annotation, Color color)
    {
        var dx = annotation.End.X - annotation.Start.X;
        var dy = annotation.End.Y - annotation.Start.Y;
        var angle = Math.Atan2(dy, dx);
        var length = Math.Sqrt(dx * dx + dy * dy);
        var head = Math.Min(Math.Max(length, 1), annotation.StrokeThickness * 5);
        const double spread = 0.45;

        var polygon = new Polygon { Fill = new SolidColorBrush(color) };
        polygon.Points.Add(annotation.End);
        polygon.Points.Add(new Point(annotation.End.X - Math.Cos(angle - spread) * head, annotation.End.Y - Math.Sin(angle - spread) * head));
        polygon.Points.Add(new Point(annotation.End.X - Math.Cos(angle + spread) * head, annotation.End.Y - Math.Sin(angle + spread) * head));
        return polygon;
    }

    private static FrameworkElement CreateCounter(Annotation annotation, Color color)
    {
        var diameter = Math.Max(28, annotation.StrokeThickness * 8);
        var grid = new Grid { Width = diameter, Height = diameter };
        grid.Children.Add(new Ellipse { Fill = new SolidColorBrush(color) });
        grid.Children.Add(new TextBlock
        {
            Text = annotation.CounterValue.ToString(),
            Foreground = new SolidColorBrush(IsLight(color) ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White),
            FontSize = diameter * 0.45,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Canvas.SetLeft(grid, annotation.Start.X - diameter / 2);
        Canvas.SetTop(grid, annotation.Start.Y - diameter / 2);
        return grid;
    }

    private static Line CreateLine(Point start, Point end, Color color, double thickness) => new()
    {
        X1 = start.X,
        Y1 = start.Y,
        X2 = end.X,
        Y2 = end.Y,
        Stroke = new SolidColorBrush(color),
        StrokeThickness = thickness,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
    };

    /// <summary>Selection feedback, per shape family. Lines and arrows get endpoint grips only.
    /// Box shapes get the same L-corner and edge-bar grips as the crop frame; a dashed frame
    /// is added only for shapes with no visible outline of their own (highlight, spotlight, text,
    /// pen and brush strokes, counters), drawn at the exact bounds so nothing is offset.</summary>
    private static void AppendSelectionAdorner(Canvas canvas, Annotation annotation, double adornerScale)
    {
        if (annotation.IsLinear)
        {
            Add(canvas, CreateHandle(annotation.Start, adornerScale));
            Add(canvas, CreateHandle(annotation.End, adornerScale));
            return;
        }

        var bounds = annotation.Bounds;

        // Rectangles and ellipses draw their own stroke; a second frame around it only reads as
        // misalignment. Everything else gets a frame that hugs the exact bounds.
        if (annotation.Tool is not (EditorTool.Rectangle or EditorTool.Ellipse))
        {
            Add(canvas, Place(new Rectangle
            {
                Width = bounds.Width,
                Height = bounds.Height,
                Stroke = new SolidColorBrush(SelectionColor),
                StrokeThickness = 1.5 * adornerScale,
                StrokeDashArray = [3, 3],
                IsHitTestVisible = false,
            }, bounds));
        }

        if (EditorDocument.IsBoxResizable(annotation))
        {
            foreach (var grip in CreateBoxGrips(bounds, adornerScale, onBorder: true))
                Add(canvas, grip);
        }

        static void Add(Canvas canvas, FrameworkElement element)
        {
            element.Tag = SelectionAdornerTag;
            canvas.Children.Add(element);
        }
    }

    /// <summary>Endpoint grip for lines and arrows, which have no corners or edges to hang an
    /// L-grip on. Scaled against the display zoom like every adorner.</summary>
    private static FrameworkElement CreateHandle(Point center, double adornerScale)
    {
        var size = 14 * adornerScale;
        return Place(new Ellipse
        {
            Width = size,
            Height = size,
            Fill = new SolidColorBrush(Microsoft.UI.Colors.White),
            Stroke = new SolidColorBrush(SelectionColor),
            StrokeThickness = 2 * adornerScale,
            IsHitTestVisible = false,
        }, new Rect(center.X - size / 2, center.Y - size / 2, size, size));
    }

    private static T Place<T>(T element, Rect bounds) where T : FrameworkElement
    {
        Canvas.SetLeft(element, bounds.X);
        Canvas.SetTop(element, bounds.Y);
        return element;
    }

    private static bool IsLight(Color color) => color.R * 0.299 + color.G * 0.587 + color.B * 0.114 > 150;

    private static Color ParseColor(string hex)
    {
        var value = hex.TrimStart('#');
        if (value.Length == 6 && int.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return Color.FromArgb(255, (byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        return Color.FromArgb(255, 255, 59, 48);
    }
}
