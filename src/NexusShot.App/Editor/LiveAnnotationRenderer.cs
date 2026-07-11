using System.Runtime.CompilerServices;
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

    // Keyed by identity, not Id: undo restores deep clones that reuse the Id with fewer erasures,
    // so an Id-keyed cache would serve already-erased pixels for a stroke whose erasures were undone.
    private static readonly ConditionalWeakTable<Annotation, MaskedStrokeSurface> MaskedStrokeSurfaces = [];

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

        // Most interactive objects can update their retained XAML visual in place. Avoiding
        // remove/add cycles here prevents layout churn and GC pauses while a handle is dragged.
        // The shape is retained; only the lightweight selection adorner is refreshed so grips
        // remain visibly attached to their handles throughout the gesture.
        if (TryUpdateRetainedVisual(canvas, annotation))
        {
            RemoveTagged(canvas, SelectionAdornerTag);
            if (document.Selected == annotation)
                AppendSelectionAdorner(canvas, annotation, adornerScale);
            return;
        }

        RemoveTagged(canvas, annotation.Id);
        RemoveTagged(canvas, SelectionAdornerTag);
        // Pixel effects are expensive raster operations. During a live stroke render the cheap
        // footprint preview; the completed effect is produced once by Render at gesture end.
        AppendVisuals(canvas, annotation, annotation.IsBrushEffect ? null : source);
        if (document.Selected == annotation)
            AppendSelectionAdorner(canvas, annotation, adornerScale);
    }

    /// <summary>Removes the selection grips; used while the inline text editor covers them.</summary>
    public static void RemoveSelectionAdorner(Canvas canvas) => RemoveTagged(canvas, SelectionAdornerTag);

    /// <summary>Refreshes only the crop adorner, at pointer-move rate during a crop drag.</summary>
    public static void UpdateCropAdorner(Canvas canvas, EditorDocument document, double adornerScale = 1)
    {
        RemoveTagged(canvas, CropAdornerTag);
        AppendCropVisuals(canvas, document, adornerScale);
    }

    /// <summary>Refreshes only strokes changed by live erasing. Masked paint is represented by
    /// vector fragments here, so this path performs no bitmap allocation or pixel copying.</summary>
    public static void UpdateDirty(
        Canvas canvas,
        IReadOnlyCollection<Annotation> annotations,
        EditorPixelSource? source)
    {
        foreach (var annotation in annotations)
        {
            if (annotation.Tool is EditorTool.Pen or EditorTool.Brush && annotation.Erasures.Count > 0)
            {
                var surface = GetMaskedStrokeSurface(annotation, ParseColor(annotation.ColorHex));
                surface.ApplyPendingMasks(annotation);
                // Already hosted: the in-place pixel writes above are the whole update.
                if (surface.IsAttachedTo(canvas)) continue;
            }
            RemoveTagged(canvas, annotation.Id);
            AppendVisuals(canvas, annotation, source);
        }
    }

    private static void AppendVisuals(Canvas canvas, Annotation annotation, EditorPixelSource? source)
    {
        foreach (var visual in CreateVisuals(annotation, source, canvas.Width, canvas.Height))
        {
            visual.Tag = annotation.Id;
            canvas.Children.Add(visual);
            if (visual is Image image && MaskedStrokeSurfaces.TryGetValue(annotation, out var surface)
                && ReferenceEquals(image.Source, surface.Bitmap))
                surface.AttachTo(canvas);
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

    private static bool TryUpdateRetainedVisual(Canvas canvas, Annotation annotation)
    {
        var visuals = canvas.Children.OfType<FrameworkElement>()
            .Where(element => element.Tag is Guid id && id == annotation.Id)
            .ToArray();
        var bounds = annotation.Bounds;

        if (visuals.Length == 1 && visuals[0] is Rectangle rectangle
            && annotation.Tool is EditorTool.Rectangle or EditorTool.Highlight)
        {
            SetBounds(rectangle, annotation.Tool == EditorTool.Rectangle
                ? InsetForStroke(bounds, annotation.StrokeThickness)
                : bounds);
            return true;
        }
        if (visuals.Length == 1 && visuals[0] is Ellipse ellipse && annotation.Tool == EditorTool.Ellipse)
        {
            SetBounds(ellipse, InsetForStroke(bounds, annotation.StrokeThickness));
            return true;
        }
        if (visuals.Length == 1 && visuals[0] is Line line && annotation.Tool == EditorTool.Line)
        {
            SetLine(line, annotation.Start, annotation.End);
            return true;
        }
        if (visuals.Length == 2 && annotation.Tool == EditorTool.Arrow
            && visuals.OfType<Line>().FirstOrDefault() is { } shaft
            && visuals.OfType<Polygon>().FirstOrDefault() is { } head)
        {
            SetLine(shaft, annotation.Start, ShaftEnd(annotation));
            var replacement = (Polygon)CreateArrowHead(annotation, ParseColor(annotation.ColorHex));
            head.Points.Clear();
            foreach (var point in replacement.Points) head.Points.Add(point);
            return true;
        }
        if (visuals.Length == 1 && visuals[0] is TextBlock text && annotation.Tool == EditorTool.Text)
        {
            SetBounds(text, bounds);
            return true;
        }
        if (visuals.Length == 1 && visuals[0] is Grid counter && annotation.Tool == EditorTool.Counter)
        {
            var diameter = Math.Max(28, annotation.StrokeThickness * 8);
            Canvas.SetLeft(counter, annotation.Start.X - diameter / 2);
            Canvas.SetTop(counter, annotation.Start.Y - diameter / 2);
            return true;
        }
        return false;

        static void SetBounds(FrameworkElement element, Rect value)
        {
            element.Width = value.Width;
            element.Height = value.Height;
            Canvas.SetLeft(element, value.X);
            Canvas.SetTop(element, value.Y);
        }

        static void SetLine(Line line, Point start, Point end)
        {
            line.X1 = start.X;
            line.Y1 = start.Y;
            line.X2 = end.X;
            line.Y2 = end.Y;
        }
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
                var rectangleBounds = InsetForStroke(bounds, annotation.StrokeThickness);
                yield return Place(new Rectangle
                {
                    Width = rectangleBounds.Width,
                    Height = rectangleBounds.Height,
                    Stroke = new SolidColorBrush(color),
                    StrokeThickness = annotation.StrokeThickness,
                }, rectangleBounds);
                break;

            case EditorTool.Ellipse:
                var ellipseBounds = InsetForStroke(bounds, annotation.StrokeThickness);
                yield return Place(new Ellipse
                {
                    Width = ellipseBounds.Width,
                    Height = ellipseBounds.Height,
                    Stroke = new SolidColorBrush(color),
                    StrokeThickness = annotation.StrokeThickness,
                }, ellipseBounds);
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
        var surface = GetMaskedStrokeSurface(annotation, color);
        surface.ApplyPendingMasks(annotation);
        return Place(new Image
        {
            Source = surface.Bitmap,
            Width = surface.Width,
            Height = surface.Height,
            IsHitTestVisible = false,
        }, new Rect(surface.Left, surface.Top, surface.Width, surface.Height));
    }

    private static MaskedStrokeSurface GetMaskedStrokeSurface(Annotation annotation, Color color)
    {
        if (MaskedStrokeSurfaces.TryGetValue(annotation, out var surface) && surface.Matches(annotation, color))
            return surface;
        surface = MaskedStrokeSurface.Create(annotation, color);
        MaskedStrokeSurfaces.AddOrUpdate(annotation, surface);
        return surface;
    }

    private static bool IsPointDab(IReadOnlyList<Point> points) =>
        points.Count == 1 || points.All(point => point == points[0]);

    private sealed class MaskedStrokeSurface
    {
        private readonly byte[] _pixels;
        private readonly List<int> _processedMaskPoints = [];
        private readonly int _pointCount;
        private readonly double _thickness;
        private readonly Color _color;

        private WeakReference<Canvas>? _host;

        public required WriteableBitmap Bitmap { get; init; }
        public required int Left { get; init; }
        public required int Top { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }

        private MaskedStrokeSurface(byte[] pixels, int pointCount, double thickness, Color color)
        {
            _pixels = pixels;
            _pointCount = pointCount;
            _thickness = thickness;
            _color = color;
        }

        /// <summary>Weak, so a surface never keeps a closed editor's visual tree alive.</summary>
        public void AttachTo(Canvas canvas) => _host = new WeakReference<Canvas>(canvas);

        public bool IsAttachedTo(Canvas canvas) =>
            _host is not null && _host.TryGetTarget(out var host) && ReferenceEquals(host, canvas);

        public bool Matches(Annotation annotation, Color color)
        {
            if (_pointCount != annotation.Points.Count || _thickness != annotation.StrokeThickness || _color != color)
                return false;
            if (_processedMaskPoints.Count > annotation.Erasures.Count) return false;
            for (var i = 0; i < _processedMaskPoints.Count; i++)
                if (_processedMaskPoints[i] > annotation.Erasures[i].Points.Count) return false;
            return true;
        }

        public static MaskedStrokeSurface Create(Annotation annotation, Color color)
        {
            var padding = annotation.StrokeThickness / 2 + 2;
            var left = (int)Math.Floor(annotation.Points.Min(point => point.X) - padding);
            var top = (int)Math.Floor(annotation.Points.Min(point => point.Y) - padding);
            var right = (int)Math.Ceiling(annotation.Points.Max(point => point.X) + padding);
            var bottom = (int)Math.Ceiling(annotation.Points.Max(point => point.Y) + padding);
            var width = Math.Max(1, right - left);
            var height = Math.Max(1, bottom - top);
            var pixels = new byte[width * height * 4];

            using var source = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var graphics = System.Drawing.Graphics.FromImage(source))
            using (var pen = new System.Drawing.Pen(
                System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B),
                (float)annotation.StrokeThickness)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
            })
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var points = annotation.Points.Select(point =>
                    new System.Drawing.PointF((float)(point.X - left), (float)(point.Y - top))).ToArray();
                if (points.Length > 1) graphics.DrawLines(pen, points);
                else if (points.Length == 1)
                    graphics.DrawEllipse(pen, points[0].X, points[0].Y, 0.1f, 0.1f);
            }

            var data = source.LockBits(new System.Drawing.Rectangle(0, 0, width, height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try { System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length); }
            finally { source.UnlockBits(data); }

            var bitmap = new WriteableBitmap(width, height);
            using (var buffer = bitmap.PixelBuffer.AsStream()) buffer.Write(pixels, 0, pixels.Length);
            bitmap.Invalidate();
            return new MaskedStrokeSurface(pixels, annotation.Points.Count, annotation.StrokeThickness, color)
            {
                Bitmap = bitmap,
                Left = left,
                Top = top,
                Width = width,
                Height = height,
            };
        }

        public void ApplyPendingMasks(Annotation annotation)
        {
            var pending = new List<PendingEraseSegment>();
            for (var maskIndex = 0; maskIndex < annotation.Erasures.Count; maskIndex++)
            {
                var mask = annotation.Erasures[maskIndex];
                while (_processedMaskPoints.Count <= maskIndex) _processedMaskPoints.Add(0);
                var processed = _processedMaskPoints[maskIndex];
                if (mask.Points.Count == 1 && processed == 0)
                    pending.Add(new(mask.Points[0], mask.Points[0], mask.Radius));
                for (var i = Math.Max(1, processed); i < mask.Points.Count; i++)
                    pending.Add(new(mask.Points[i - 1], mask.Points[i], mask.Radius));
                _processedMaskPoints[maskIndex] = mask.Points.Count;
            }

            if (pending.Count == 0) return;
            var dirty = ApplyEraseBatch(pending);
            if (dirty.Right <= dirty.Left || dirty.Bottom <= dirty.Top) return;
            using var stream = Bitmap.PixelBuffer.AsStream();
            var rowBytes = (dirty.Right - dirty.Left) * 4;
            for (var y = dirty.Top; y < dirty.Bottom; y++)
            {
                var offset = (y * Width + dirty.Left) * 4;
                stream.Position = offset;
                stream.Write(_pixels, offset, rowBytes);
            }
            Bitmap.Invalidate();
        }

        /// <summary>
        /// Rasterizes every new pointer segment as one spatially bucketed operation. Pixels in
        /// overlapping brush circles are visited once per frame rather than once per sample.
        /// </summary>
        private (int Left, int Top, int Right, int Bottom) ApplyEraseBatch(
            IReadOnlyList<PendingEraseSegment> segments)
        {
            const int cellSize = 64;
            var cells = new Dictionary<(int X, int Y), List<LocalEraseSegment>>();
            foreach (var segment in segments)
            {
                var local = new LocalEraseSegment(
                    new Point(segment.Start.X - Left, segment.Start.Y - Top),
                    new Point(segment.End.X - Left, segment.End.Y - Top),
                    segment.Radius);
                var minCellX = Math.Max(0, (int)Math.Floor((Math.Min(local.Start.X, local.End.X) - local.Radius) / cellSize));
                var maxCellX = Math.Min((Width - 1) / cellSize, (int)Math.Floor((Math.Max(local.Start.X, local.End.X) + local.Radius) / cellSize));
                var minCellY = Math.Max(0, (int)Math.Floor((Math.Min(local.Start.Y, local.End.Y) - local.Radius) / cellSize));
                var maxCellY = Math.Min((Height - 1) / cellSize, (int)Math.Floor((Math.Max(local.Start.Y, local.End.Y) + local.Radius) / cellSize));
                for (var cellY = minCellY; cellY <= maxCellY; cellY++)
                for (var cellX = minCellX; cellX <= maxCellX; cellX++)
                {
                    if (!cells.TryGetValue((cellX, cellY), out var bucket))
                        cells[(cellX, cellY)] = bucket = [];
                    bucket.Add(local);
                }
            }

            var dirty = (Left: Width, Top: Height, Right: 0, Bottom: 0);
            foreach (var (cell, bucket) in cells)
            {
                var minX = cell.X * cellSize;
                var maxX = Math.Min(Width, minX + cellSize);
                var minY = cell.Y * cellSize;
                var maxY = Math.Min(Height, minY + cellSize);
                var changed = false;
                for (var y = minY; y < maxY; y++)
                for (var x = minX; x < maxX; x++)
                {
                    var erase = false;
                    foreach (var segment in bucket)
                    {
                        if (DistanceSquaredToSegment(x + 0.5, y + 0.5, segment.Start, segment.End)
                            > segment.Radius * segment.Radius) continue;
                        erase = true;
                        break;
                    }
                    if (!erase) continue;
                    var offset = (y * Width + x) * 4;
                    if (_pixels[offset + 3] == 0) continue;
                    _pixels[offset] = _pixels[offset + 1] = _pixels[offset + 2] = _pixels[offset + 3] = 0;
                    changed = true;
                }
                if (!changed) continue;
                dirty.Left = Math.Min(dirty.Left, minX);
                dirty.Top = Math.Min(dirty.Top, minY);
                dirty.Right = Math.Max(dirty.Right, maxX);
                dirty.Bottom = Math.Max(dirty.Bottom, maxY);
            }
            return dirty;
        }

        private static double DistanceSquaredToSegment(double x, double y, Point a, Point b)
        {
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var lengthSquared = dx * dx + dy * dy;
            if (lengthSquared == 0) return Square(x - a.X) + Square(y - a.Y);
            var t = Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / lengthSquared, 0, 1);
            return Square(x - (a.X + t * dx)) + Square(y - (a.Y + t * dy));
        }

        private static double Square(double value) => value * value;
        private readonly record struct PendingEraseSegment(Point Start, Point End, double Radius);
        private readonly record struct LocalEraseSegment(Point Start, Point End, double Radius);
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
        var frameBounds = InsetForStroke(crop, frameThickness);
        yield return Place(new Rectangle
        {
            Width = frameBounds.Width,
            Height = frameBounds.Height,
            Stroke = new SolidColorBrush(SelectionColor),
            StrokeThickness = frameThickness,
            IsHitTestVisible = false,
        }, frameBounds);

        // Grips share the frame stroke's actual centerline. The document crop remains the
        // authoritative hit-test geometry; this is presentation-only half-stroke alignment.
        var frameCenterline = InsetForStroke(frameBounds, frameThickness);
        foreach (var grip in CreateBoxGrips(frameCenterline, adornerScale, onBorder: true))
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
            // Selection handles belong to the authoritative geometry. Presentation overflow is
            // handled by the non-clipping editor frame, never by moving a handle off its border.
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

        const double thickness = 2;
        var frameBounds = InsetForStroke(crop, thickness);
        yield return Place(new Rectangle
        {
            Width = frameBounds.Width,
            Height = frameBounds.Height,
            Stroke = new SolidColorBrush(SelectionColor),
            StrokeThickness = thickness,
            StrokeDashArray = [5, 3],
            IsHitTestVisible = false,
        }, frameBounds);
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
        var gripBounds = bounds;

        // Rectangles and ellipses draw their own stroke; a second frame around it only reads as
        // misalignment. Everything else gets a frame that hugs the exact bounds.
        if (annotation.Tool is not (EditorTool.Rectangle or EditorTool.Ellipse))
        {
            var frameThickness = 1.5 * adornerScale;
            var frameBounds = InsetForStroke(bounds, frameThickness);
            // WinUI paints Shape strokes inside their layout bounds, so the visible centerline
            // sits another half stroke inward from the element edge.
            gripBounds = InsetForStroke(frameBounds, frameThickness);
            Add(canvas, Place(new Rectangle
            {
                Width = frameBounds.Width,
                Height = frameBounds.Height,
                Stroke = new SolidColorBrush(SelectionColor),
                StrokeThickness = frameThickness,
                StrokeDashArray = [3, 3],
                IsHitTestVisible = false,
            }, frameBounds));
        }
        else
        {
            // Rectangle/ellipse visuals render their centered stroke inside the document bounds.
            // Put grips on that rendered centerline, not on the raw outer geometry.
            var shapeElementBounds = InsetForStroke(bounds, annotation.StrokeThickness);
            gripBounds = InsetForStroke(shapeElementBounds, annotation.StrokeThickness);
        }

        if (EditorDocument.IsBoxResizable(annotation))
        {
            foreach (var grip in CreateBoxGrips(gripBounds, adornerScale, onBorder: true))
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

    /// <summary>Insets a centered XAML stroke so its complete footprint remains inside the
    /// document bounds. XAML shapes have no inside-stroke alignment option.</summary>
    private static Rect InsetForStroke(Rect bounds, double thickness)
    {
        var insetX = Math.Min(Math.Max(0, thickness / 2), bounds.Width / 2);
        var insetY = Math.Min(Math.Max(0, thickness / 2), bounds.Height / 2);
        return new Rect(
            bounds.X + insetX,
            bounds.Y + insetY,
            Math.Max(0, bounds.Width - insetX * 2),
            Math.Max(0, bounds.Height - insetY * 2));
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
