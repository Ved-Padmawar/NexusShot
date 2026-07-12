using NexusShot.Core;

namespace NexusShot.Render;

/// <summary>
/// Draws a document's annotations, in image-pixel space, onto any Direct2D target. Every frame draws
/// current state and nothing else, so a drag costs the same whether the pointer is still or fast.
///
/// The caller sets the world transform, so the exporter draws with the identity transform and
/// produces exactly what the screen shows.
/// </summary>
public sealed class AnnotationRenderer(D2DResources resources)
{
    /// <summary>Draws every annotation in paint order. Adorners are the caller's business, so
    /// the exporter can reuse this without drawing selection handles into the file.</summary>
    /// <summary><paramref name="skip"/> omits one annotation, for when a live editor is standing in
    /// for it on screen.</summary>
    public void DrawAnnotations(
        IComObject<ID2D1RenderTarget> target,
        EditorDocument document,
        IPixelEffectSource? effects,
        Annotation? skip = null)
    {
        foreach (var annotation in document.Annotations)
        {
            if (ReferenceEquals(annotation, skip)) continue;
            DrawAnnotation(target, annotation, document, effects);
        }
    }

    public void DrawAnnotation(
        IComObject<ID2D1RenderTarget> target,
        Annotation annotation,
        EditorDocument document,
        IPixelEffectSource? effects)
    {
        var color = Palette.Parse(annotation.ColorHex);

        switch (annotation.Tool)
        {
            case EditorTool.Rectangle:
            {
                var bounds = AdornerGeometry.InsetForStroke(annotation.Bounds, annotation.StrokeThickness);
                if (bounds.IsEmpty) break;
                target.DrawRectangle(ToRect(bounds), resources.Brush(color), (float)annotation.StrokeThickness);
                break;
            }

            case EditorTool.Ellipse:
            {
                var bounds = AdornerGeometry.InsetForStroke(annotation.Bounds, annotation.StrokeThickness);
                if (bounds.IsEmpty) break;
                target.DrawEllipse(ToEllipse(bounds), resources.Brush(color), (float)annotation.StrokeThickness);
                break;
            }

            case EditorTool.Line:
                target.DrawLine(
                    ToPoint(annotation.Start), ToPoint(annotation.End),
                    resources.Brush(color), (float)annotation.StrokeThickness, resources.RoundStroke);
                break;

            case EditorTool.Arrow:
                target.DrawLine(
                    ToPoint(annotation.Start), ToPoint(ArrowGeometry.ShaftEnd(annotation)),
                    resources.Brush(color), (float)annotation.StrokeThickness, resources.RoundStroke);
                FillPolygon(target, ArrowGeometry.Head(annotation), color);
                break;

            case EditorTool.Highlight:
                if (annotation.Bounds.IsEmpty) break;
                target.FillRectangle(ToRect(annotation.Bounds), resources.Brush(color.WithAlpha(90)));
                break;

            case EditorTool.Spotlight:
                // Matches the export: everything outside the region is dimmed.
                foreach (var band in AdornerGeometry.DimAround(
                    annotation.Bounds, document.ImageWidth, document.ImageHeight))
                    target.FillRectangle(ToRect(band), resources.Brush(Rgba.Black.WithAlpha(140)));
                break;

            case EditorTool.Pen:
            case EditorTool.Brush:
                DrawPaintStroke(target, annotation, color);
                break;

            case EditorTool.Eraser:
                // The eraser is never persisted as an annotation; its effect lives in the strokes
                // it masked. A live eraser drag is drawn as its cursor, not as a stroke.
                break;

            case EditorTool.Blur:
            case EditorTool.Pixelate:
                DrawBrushEffect(target, annotation, effects);
                break;

            case EditorTool.Counter:
                DrawCounter(target, annotation, color);
                break;

            case EditorTool.Text:
                DrawText(target, annotation, color);
                break;

            case EditorTool.Select:
            case EditorTool.Crop:
                break;
        }
    }

    private void DrawPaintStroke(IComObject<ID2D1RenderTarget> target, Annotation annotation, Rgba color)
    {
        if (annotation.Points.Count == 0) return;

        if (annotation.Erasures.Count == 0)
        {
            StrokePath(target, annotation.Points, color, annotation.StrokeThickness);
            return;
        }

        using var geometry = ErasedStrokeGeometry(annotation);
        if (geometry is null) return;
        target.FillGeometry(geometry, resources.Brush(color));
    }

    /// <summary>The stroke's footprint with its erasures subtracted, as one geometry: Widen turns
    /// each centreline into its real round-capped region, and EXCLUDE punches the eraser out. The
    /// hole is part of the shape, so the GPU fills it in one pass.</summary>
    private IComObject<ID2D1PathGeometry>? ErasedStrokeGeometry(Annotation annotation)
    {
        var stroke = WidenedPath(annotation.Points, PaintStrokeGeometry.Diameter(annotation.StrokeThickness));
        if (stroke is null) return null;

        foreach (var mask in annotation.Erasures)
        {
            var eraser = WidenedPath(mask.Points, mask.Radius * 2);
            if (eraser is null) continue;

            var combined = resources.CreatePathGeometry();
            using (var sink = combined.Open())
            {
                stroke.AsGeometry().CombineWithGeometry(
                    eraser.AsGeometry(),
                    sink,
                    D2D1_COMBINE_MODE.D2D1_COMBINE_MODE_EXCLUDE);
                sink.Object.Close();
            }
            eraser.Dispose();
            stroke.Dispose();
            stroke = combined;
        }
        return stroke;
    }

    /// <summary>A polyline centreline widened into the closed region a round-capped stroke of
    /// <paramref name="thickness"/> would actually paint.</summary>
    private IComObject<ID2D1PathGeometry>? WidenedPath(IReadOnlyList<Point> points, double thickness)
    {
        if (points.Count == 0) return null;

        using var line = CreatePath(points, filled: false);
        var widened = resources.CreatePathGeometry();
        using (var sink = widened.Open())
        using (var style = resources.Factory.CreateStrokeStyle(RoundStrokeProperties))
        {
            line.AsGeometry().Widen(
                sink,
                (float)Math.Max(1, thickness),
                style);
            sink.Object.Close();
        }
        return widened;
    }


    internal static readonly D2D1_STROKE_STYLE_PROPERTIES RoundStrokeProperties = new()
    {
        startCap = D2D1_CAP_STYLE.D2D1_CAP_STYLE_ROUND,
        endCap = D2D1_CAP_STYLE.D2D1_CAP_STYLE_ROUND,
        lineJoin = D2D1_LINE_JOIN.D2D1_LINE_JOIN_ROUND,
        dashCap = D2D1_CAP_STYLE.D2D1_CAP_STYLE_ROUND,
        dashStyle = D2D1_DASH_STYLE.D2D1_DASH_STYLE_SOLID,
        miterLimit = 10,
    };

    /// <summary>Strokes a polyline with round caps and joins. A single sample is a round dab whose
    /// diameter is the thickness, matching <see cref="PaintStrokeGeometry"/>.</summary>
    private void StrokePath(
        IComObject<ID2D1RenderTarget> target, IReadOnlyList<Point> points, Rgba color, double thickness)
    {
        if (points.Count == 0) return;
        var brush = resources.Brush(color);
        var width = (float)PaintStrokeGeometry.Diameter(thickness);

        if (points.Count == 1 || IsDab(points))
        {
            var radius = (float)PaintStrokeGeometry.Radius(thickness);
            target.FillEllipse(new D2D1_ELLIPSE
            {
                point = ToPoint(points[0]),
                radiusX = radius,
                radiusY = radius,
            }, brush);
            return;
        }

        using var geometry = CreatePath(points, filled: false);
        target.DrawGeometry(geometry, brush, width, resources.RoundStroke);
    }

    private void DrawBrushEffect(
        IComObject<ID2D1RenderTarget> target, Annotation annotation, IPixelEffectSource? effects)
    {
        if (annotation.Points.Count == 0) return;

        // No effect source (pixels not decoded yet, or the target cannot host effects): a frosted
        // stroke placeholder until the pixels arrive.
        using var context = target.AsDeviceContext();
        if (effects is null || context is null)
        {
            StrokePath(target, annotation.Points, Rgba.White.WithAlpha(70), annotation.BrushRadius * 2);
            return;
        }

        effects.DrawBrushEffect(context, annotation);
    }

    private void DrawCounter(IComObject<ID2D1RenderTarget> target, Annotation annotation, Rgba color)
    {
        var diameter = annotation.CounterDiameter;
        var radius = (float)(diameter / 2);
        var center = ToPoint(annotation.Start);

        target.FillEllipse(new D2D1_ELLIPSE { point = center, radiusX = radius, radiusY = radius },
            resources.Brush(color));

        var label = annotation.CounterValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var format = resources.TextFormat("Segoe UI", (float)(diameter * 0.45), bold: true, italic: false);
        format.Object.SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER);
        format.Object.SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);

        var box = new Rect(annotation.Start.X - diameter / 2, annotation.Start.Y - diameter / 2, diameter, diameter);
        ID2D1RenderTargetExtensions.DrawText(
            target, label, format, ToRect(box),
            resources.Brush(Palette.IsLight(color) ? Rgba.Black : Rgba.White));
    }

    /// <summary>Text wraps to the annotation's box, so the preview matches the editing box and
    /// the export.</summary>
    private void DrawText(IComObject<ID2D1RenderTarget> target, Annotation annotation, Rgba color)
    {
        if (string.IsNullOrEmpty(annotation.Text)) return;
        var bounds = annotation.Bounds;

        using var layout = TextLayout(annotation, bounds);
        target.DrawTextLayout(ToPoint(new Point(bounds.X, bounds.Y)), layout, resources.Brush(color));
    }

    /// <summary>The laid-out text for an annotation. Shared with the inline editor so the box the
    /// user types into wraps identically to what gets drawn.</summary>
    public IComObject<IDWriteTextLayout> TextLayout(Annotation annotation, Rect bounds)
    {
        var format = resources.TextFormat(
            "Segoe UI", (float)Math.Max(8, annotation.FontSize), annotation.IsBold, annotation.IsItalic);

        var layout = resources.DWrite.CreateTextLayout(
            format,
            annotation.Text,
            maxWidth: (float)Math.Max(10, bounds.Width),
            maxHeight: (float)Math.Max(10, bounds.Height));

        if (annotation.IsUnderline)
        {
            layout.Object.SetUnderline(true, new DWRITE_TEXT_RANGE
            {
                startPosition = 0,
                length = (uint)annotation.Text.Length,
            });
        }
        return layout;
    }

    // ============================  ADORNERS  ============================

    /// <summary>Selection grips and the crop frame. Never drawn by the exporter.</summary>
    public void DrawAdorners(
        IComObject<ID2D1RenderTarget> target, EditorDocument document, double adornerScale)
    {
        DrawEmptyTextFrames(target, document, adornerScale);
        if (document.Selected is { } selected) DrawSelectionAdorner(target, selected, adornerScale);
        DrawCropAdorner(target, document, adornerScale);
    }

    /// <summary>
    /// The outline of a text box that has no text yet.
    ///
    /// Text draws nothing until it has content, so dragging one out showed no feedback at all until
    /// the pointer came up. This is an adorner rather than part of the annotation so it stays out of
    /// the exported file, where an empty box would be a visible artefact.
    /// </summary>
    private void DrawEmptyTextFrames(
        IComObject<ID2D1RenderTarget> target, EditorDocument document, double adornerScale)
    {
        foreach (var annotation in document.Annotations)
        {
            if (annotation.Tool != EditorTool.Text || annotation.Text.Length > 0) continue;

            var thickness = 1.5 * adornerScale;
            var bounds = AdornerGeometry.InsetForStroke(annotation.Bounds, thickness);
            if (bounds.IsEmpty) continue;

            target.DrawRectangle(
                ToRect(bounds), resources.Brush(Palette.Parse(annotation.ColorHex).WithAlpha(180)),
                (float)thickness, resources.DashStroke(3, 3));
        }
    }

    private void DrawSelectionAdorner(
        IComObject<ID2D1RenderTarget> target, Annotation annotation, double adornerScale)
    {
        // Lines and arrows have no box to hang grips on: they get endpoint handles.
        if (annotation.IsLinear)
        {
            DrawEndpointHandle(target, annotation.Start, adornerScale);
            DrawEndpointHandle(target, annotation.End, adornerScale);
            return;
        }

        var adorner = AdornerGeometry.Selection(annotation, adornerScale);

        if (adorner.DashedFrame is { } frame && !frame.IsEmpty)
        {
            target.DrawRectangle(
                ToRect(frame), resources.Brush(Palette.Selection),
                (float)adorner.FrameThickness, resources.DashStroke(3, 3));
        }

        if (EditorDocument.IsBoxResizable(annotation))
            DrawBoxGrips(target, adorner.GripBounds, adornerScale);
    }

    private void DrawCropAdorner(
        IComObject<ID2D1RenderTarget> target, EditorDocument document, double adornerScale)
    {
        // A live session draws the interactive frame; a committed crop keeps its passive
        // dim-plus-dashed-frame presentation.
        if (document.PendingCrop is { } pending)
        {
            foreach (var band in AdornerGeometry.DimAround(pending, document.ImageWidth, document.ImageHeight))
                target.FillRectangle(ToRect(band), resources.Brush(Rgba.Black.WithAlpha(150)));

            var adorner = AdornerGeometry.Crop(pending, adornerScale);
            if (!adorner.Frame.IsEmpty)
            {
                target.DrawRectangle(ToRect(adorner.Frame), resources.Brush(Palette.Selection),
                    (float)adorner.FrameThickness);
            }
            DrawBoxGrips(target, adorner.GripBounds, adornerScale);
            return;
        }

        if (document.CropBounds is not { } crop) return;

        foreach (var band in AdornerGeometry.DimAround(crop, document.ImageWidth, document.ImageHeight))
            target.FillRectangle(ToRect(band), resources.Brush(Rgba.Black.WithAlpha(150)));

        const float thickness = 2;
        var frameBounds = AdornerGeometry.InsetForStroke(crop, thickness);
        if (frameBounds.IsEmpty) return;
        target.DrawRectangle(ToRect(frameBounds), resources.Brush(Palette.Selection),
            thickness, resources.DashStroke(5, 3));
    }

    /// <summary>L-shaped corner grips and short edge bars, drawn inside the bounds: a white stroke
    /// over a dark underlay, so they read on light and dark content alike.</summary>
    private void DrawBoxGrips(IComObject<ID2D1RenderTarget> target, Rect bounds, double adornerScale)
    {
        if (bounds.IsEmpty) return;
        var metrics = AdornerGeometry.Grips(bounds, adornerScale);

        foreach (var handle in BoxGeometry.Handles)
        {
            var path = AdornerGeometry.GripPath(handle, bounds, metrics.Arm, metrics.Bar, inset: 0);
            if (path is null) continue;

            using var geometry = CreatePath(path, filled: false);
            target.DrawGeometry(geometry, resources.Brush(Rgba.Black.WithAlpha(170)),
                (float)(metrics.Thickness + metrics.UnderlayPad * 2), resources.RoundStroke);
            target.DrawGeometry(geometry, resources.Brush(Rgba.White),
                (float)metrics.Thickness, resources.RoundStroke);
        }
    }

    /// <summary>Endpoint grip for lines and arrows, scaled against the display zoom.</summary>
    private void DrawEndpointHandle(IComObject<ID2D1RenderTarget> target, Point center, double adornerScale)
    {
        var radius = (float)(7 * adornerScale);
        var ellipse = new D2D1_ELLIPSE { point = ToPoint(center), radiusX = radius, radiusY = radius };
        target.FillEllipse(ellipse, resources.Brush(Rgba.White));
        target.DrawEllipse(ellipse, resources.Brush(Palette.Selection), (float)(2 * adornerScale));
    }

    /// <summary>The brush/eraser cursor preview: a ring showing the exact footprint that will be
    /// painted or erased. Drawn in image space so it scales with the zoom, like the stroke will.</summary>
    public void DrawBrushCursor(
        IComObject<ID2D1RenderTarget> target, Point center, double thickness, double adornerScale)
    {
        var radius = (float)PaintStrokeGeometry.Radius(thickness);
        var ellipse = new D2D1_ELLIPSE { point = ToPoint(center), radiusX = radius, radiusY = radius };
        target.DrawEllipse(ellipse, resources.Brush(Rgba.Black.WithAlpha(160)), (float)(2.5 * adornerScale));
        target.DrawEllipse(ellipse, resources.Brush(Rgba.White.WithAlpha(230)), (float)(1.2 * adornerScale));
    }

    // ============================  PRIMITIVES  ============================

    private void FillPolygon(IComObject<ID2D1RenderTarget> target, IReadOnlyList<Point> points, Rgba color)
    {
        using var geometry = CreatePath(points, filled: true);
        target.FillGeometry(geometry, resources.Brush(color));
    }

    /// <summary>Builds a path geometry. Geometries are cheap to create and are freed immediately;
    /// the expensive resources (brushes, stroke styles) are the cached ones.</summary>
    /// <summary>Path geometries come from the shared factory, not the render target: they are
    /// factory resources, so one instance serves every window and the exporter.</summary>
    private IComObject<ID2D1PathGeometry> CreatePath(IReadOnlyList<Point> points, bool filled)
    {
        var geometry = resources.CreatePathGeometry();
        using (var sink = geometry.Open())
        {
            sink.Object.BeginFigure(ToPoint(points[0]),
                filled ? D2D1_FIGURE_BEGIN.D2D1_FIGURE_BEGIN_FILLED : D2D1_FIGURE_BEGIN.D2D1_FIGURE_BEGIN_HOLLOW);
            for (var i = 1; i < points.Count; i++)
                sink.Object.AddLine(ToPoint(points[i]));
            sink.Object.EndFigure(filled ? D2D1_FIGURE_END.D2D1_FIGURE_END_CLOSED : D2D1_FIGURE_END.D2D1_FIGURE_END_OPEN);
            sink.Object.Close();
        }
        return geometry;
    }

    private static bool IsDab(IReadOnlyList<Point> points)
    {
        for (var i = 1; i < points.Count; i++)
            if (points[i] != points[0]) return false;
        return true;
    }

    public static D2D_POINT_2F ToPoint(Point point) => new((float)point.X, (float)point.Y);

    public static D2D_RECT_F ToRect(Rect rect) =>
        new((float)rect.Left, (float)rect.Top, (float)rect.Right, (float)rect.Bottom);

    public static D2D1_ELLIPSE ToEllipse(Rect bounds) => new()
    {
        point = new D2D_POINT_2F((float)(bounds.X + bounds.Width / 2), (float)(bounds.Y + bounds.Height / 2)),
        radiusX = (float)(bounds.Width / 2),
        radiusY = (float)(bounds.Height / 2),
    };
}

/// <summary>Arrow shaft and head geometry, shared by the renderer and the exporter.</summary>
public static class ArrowGeometry
{
    /// <summary>The shaft stops short of the tip so the head is not drawn over a line end.</summary>
    public static Point ShaftEnd(Annotation annotation)
    {
        var dx = annotation.End.X - annotation.Start.X;
        var dy = annotation.End.Y - annotation.Start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1) return annotation.End;
        var head = Math.Min(length, annotation.StrokeThickness * 5) * 0.8;
        return new Point(annotation.End.X - dx / length * head, annotation.End.Y - dy / length * head);
    }

    public static Point[] Head(Annotation annotation)
    {
        var dx = annotation.End.X - annotation.Start.X;
        var dy = annotation.End.Y - annotation.Start.Y;
        var angle = Math.Atan2(dy, dx);
        var length = Math.Sqrt(dx * dx + dy * dy);
        var head = Math.Min(Math.Max(length, 1), annotation.StrokeThickness * 5);
        const double spread = 0.45;

        return
        [
            annotation.End,
            new(annotation.End.X - Math.Cos(angle - spread) * head, annotation.End.Y - Math.Sin(angle - spread) * head),
            new(annotation.End.X - Math.Cos(angle + spread) * head, annotation.End.Y - Math.Sin(angle + spread) * head),
        ];
    }
}

/// <summary>
/// Supplies GPU-rendered blur/pixelate for a brush-effect stroke. Requires a device context:
/// ID2D1Effect is a device-context feature, not a plain render-target one. Kept as an interface so
/// the renderer degrades to a frosted placeholder before pixels have loaded, and so the exporter
/// can substitute a full-resolution source.
/// </summary>
public interface IPixelEffectSource
{
    void DrawBrushEffect(IComObject<ID2D1DeviceContext> context, Annotation annotation);
}
