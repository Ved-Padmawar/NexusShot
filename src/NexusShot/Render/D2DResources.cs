using NexusShot.Core;

namespace NexusShot.Render;

/// <summary>
/// Per-render-target cache for brushes, stroke styles and text formats, so a frame does not
/// allocate: a colour maps to one brush that lives as long as the render target. Resources are
/// keyed by value, so callers ask for what they want and get a cached instance.
///
/// The render target owns device resources, so this is rebuilt whenever the target is.
/// </summary>
public sealed unsafe class D2DResources : IDisposable
{
    /// <summary>Cache bounds. Dragging through the colour picker mints a distinct colour per frame,
    /// and typed text a distinct measurement per keystroke, so these caches are capped rather than
    /// left to grow with the length of the session.</summary>
    private const int MaxBrushes = 128;
    private const int MaxFormats = 64;
    private const int MaxMeasurements = 512;

    private readonly IComObject<ID2D1RenderTarget> _target;
    private readonly LruCache<Rgba, IComObject<ID2D1SolidColorBrush>> _brushes = new(MaxBrushes);
    private readonly LruCache<(
        string Family,
        float Size,
        DWRITE_FONT_WEIGHT Weight,
        bool Italic,
        DWRITE_TEXT_ALIGNMENT Alignment,
        DWRITE_PARAGRAPH_ALIGNMENT ParagraphAlignment,
        DWRITE_WORD_WRAPPING WordWrapping), IComObject<IDWriteTextFormat>> _formats = new(MaxFormats);

    private IComObject<ID2D1StrokeStyle>? _roundStroke;
    private IComObject<IDWriteFactory>? _dwrite;

    public D2DResources(IComObject<ID2D1RenderTarget> target) => _target = target;

    public IComObject<IDWriteFactory> DWrite => _dwrite ??=
        DWriteFunctions.DWriteCreateFactory<IDWriteFactory>(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED);

    /// <summary>A cached solid brush for this colour.</summary>
    public IComObject<ID2D1SolidColorBrush> Brush(Rgba color)
    {
        if (_brushes.TryGetValue(color, out var cached)) return cached;

        var brush = _target.CreateSolidColorBrush(ToD3D(color));
        if (_brushes.Add(color, brush, out var evicted)) evicted.Dispose();
        return brush;
    }

    /// <summary>One reusable brush, recoloured in place, for a caller that paints thousands of
    /// distinct colours per frame: caching those would evict everything else. Valid only for the
    /// draw it is fetched for.</summary>
    public IComObject<ID2D1SolidColorBrush> ScratchBrush(Rgba color)
    {
        _scratchBrush ??= _target.CreateSolidColorBrush(ToD3D(color));
        _scratchBrush.Object.SetColor(ToD3D(color));
        return _scratchBrush;
    }

    private IComObject<ID2D1SolidColorBrush>? _scratchBrush;

    /// <summary>
    /// A two-stop linear gradient brush, recoloured and re-aimed in place. One brush serves every
    /// gradient in the frame, so the colour picker's field costs two draws rather than a fill per
    /// pixel column.
    ///
    /// The stop collection is immutable once created, so a colour change rebuilds the brush; the
    /// endpoints are brush properties and are simply set. Valid only for the draw it is fetched for.
    /// </summary>
    public IComObject<ID2D1LinearGradientBrush> GradientBrush(Rgba from, Rgba to, Point start, Point end)
    {
        if (_gradientBrush is null || _gradientStops != (from, to))
        {
            _gradientBrush?.Dispose();

            using var stops = _target.CreateGradientStopCollection(
            [
                new D2D1_GRADIENT_STOP { position = 0, color = ToD3D(from) },
                new D2D1_GRADIENT_STOP { position = 1, color = ToD3D(to) },
            ]);

            _gradientBrush = _target.CreateLinearGradientBrush(
                new D2D1_LINEAR_GRADIENT_BRUSH_PROPERTIES(), stops);
            _gradientStops = (from, to);
        }

        _gradientBrush.Object.SetStartPoint(AnnotationRenderer.ToPoint(start));
        _gradientBrush.Object.SetEndPoint(AnnotationRenderer.ToPoint(end));
        return _gradientBrush;
    }

    private IComObject<ID2D1LinearGradientBrush>? _gradientBrush;
    private (Rgba From, Rgba To)? _gradientStops;

    /// <summary>The radial counterpart of <see cref="GradientBrush"/>: one brush, recoloured and
    /// re-centred in place, valid only for the draw it is fetched for.</summary>
    public IComObject<ID2D1RadialGradientBrush> RadialBrush(Rgba from, Rgba to, Point center, double radius)
    {
        if (_radialBrush is null || _radialStops != (from, to))
        {
            _radialBrush?.Dispose();

            using var stops = _target.CreateGradientStopCollection(
            [
                new D2D1_GRADIENT_STOP { position = 0, color = ToD3D(from) },
                new D2D1_GRADIENT_STOP { position = 1, color = ToD3D(to) },
            ]);

            _radialBrush = _target.CreateRadialGradientBrush(new D2D1_RADIAL_GRADIENT_BRUSH_PROPERTIES(), stops);
            _radialStops = (from, to);
        }

        _radialBrush.Object.SetCenter(AnnotationRenderer.ToPoint(center));
        _radialBrush.Object.SetRadiusX((float)radius);
        _radialBrush.Object.SetRadiusY((float)radius);
        return _radialBrush;
    }

    private IComObject<ID2D1RadialGradientBrush>? _radialBrush;
    private (Rgba From, Rgba To)? _radialStops;

    /// <summary>Round caps and joins: what a paint stroke and every grip is drawn with.</summary>
    public IComObject<ID2D1StrokeStyle> RoundStroke => _roundStroke ??= CreateStroke(new D2D1_STROKE_STYLE_PROPERTIES
    {
        startCap = D2D1_CAP_STYLE.D2D1_CAP_STYLE_ROUND,
        endCap = D2D1_CAP_STYLE.D2D1_CAP_STYLE_ROUND,
        lineJoin = D2D1_LINE_JOIN.D2D1_LINE_JOIN_ROUND,
        dashCap = D2D1_CAP_STYLE.D2D1_CAP_STYLE_ROUND,
        dashStyle = D2D1_DASH_STYLE.D2D1_DASH_STYLE_SOLID,
        miterLimit = 10,
    });

    /// <summary>
    /// A text format for the given font and layout settings. Alignment and wrapping are part of the
    /// key and applied once here: a cached format is shared, so mutating one afterwards would leak
    /// those settings into unrelated draws, and into any layout built from it - CreateTextLayout
    /// snapshots the format's state.
    /// </summary>
    public IComObject<IDWriteTextFormat> TextFormat(
        string family,
        float size,
        DWRITE_FONT_WEIGHT weight,
        bool italic,
        DWRITE_TEXT_ALIGNMENT alignment = DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_LEADING,
        DWRITE_PARAGRAPH_ALIGNMENT paragraphAlignment = DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_NEAR,
        DWRITE_WORD_WRAPPING wordWrapping = DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_WRAP)
    {
        var key = (family, size, weight, italic, alignment, paragraphAlignment, wordWrapping);
        if (_formats.TryGetValue(key, out var cached)) return cached;

        var format = DWrite.CreateTextFormat(
            family,
            size,
            weight: weight,
            style: italic ? DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_ITALIC : DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL);

        format.Object.SetTextAlignment(alignment);
        format.Object.SetParagraphAlignment(paragraphAlignment);
        format.Object.SetWordWrapping(wordWrapping);

        if (_formats.Add(key, format, out var evicted)) evicted.Dispose();
        return format;
    }

    /// <summary>The rendered width of a string. Cached because measuring realises an
    /// IDWriteTextLayout, and callers measure to centre or size a widget every frame.</summary>
    public double MeasureText(string text, string family, float size, DWRITE_FONT_WEIGHT weight)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var key = (text, family, size, weight);
        if (_measurements.TryGetValue(key, out var cached)) return cached;

        var format = TextFormat(family, size, weight, italic: false);
        using var layout = DWrite.CreateTextLayout(format, text);
        layout.Object.GetMetrics(out var metrics);

        _measurements.Add(key, metrics.width, out _);
        return metrics.width;
    }

    private readonly LruCache<(string Text, string Family, float Size, DWRITE_FONT_WEIGHT Weight), double> _measurements =
        new(MaxMeasurements);

    /// <summary>
    /// <paramref name="preferred"/> if it is installed, otherwise <paramref name="fallback"/>.
    ///
    /// DirectWrite does not fail on a missing family; it substitutes silently, and the substitute for
    /// a monospace face is a proportional one - so the digits a mono face exists for would jitter.
    /// Checked once per name: installing a font mid-session is not worth a lookup per draw.
    /// </summary>
    public string Family(string preferred, string fallback)
    {
        if (FamilyCache.TryGetValue(preferred, out var resolved)) return resolved;

        DWrite.Object.GetSystemFontCollection(out var collection, false).ThrowOnError();
        using var fonts = new ComObject<IDWriteFontCollection>(collection);
        using Pwstr name = preferred;
        fonts.Object.FindFamilyName(name, out _, out var exists).ThrowOnError();

        resolved = exists ? preferred : fallback;
        FamilyCache[preferred] = resolved;
        return resolved;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> FamilyCache = new();

    /// <summary>
    /// An icon's stroked and filled geometry, on its design grid. Built once per icon and kept for the
    /// life of the target: there are a few dozen icons, and rebuilding a path per draw would be most
    /// of a toolbar's frame.
    /// </summary>
    public (IComObject<ID2D1PathGeometry> Stroke, IComObject<ID2D1PathGeometry>? Fill) IconGeometry(Icon icon)
    {
        if (_icons.TryGetValue(icon, out var cached)) return cached;

        var built = (Build(icon.Stroke, filled: false), icon.Fill is { } fill ? Build(fill, filled: true) : null);
        _icons[icon] = built;
        return built;
    }

    private readonly Dictionary<Icon, (IComObject<ID2D1PathGeometry> Stroke, IComObject<ID2D1PathGeometry>? Fill)> _icons = [];

    private IComObject<ID2D1PathGeometry> Build(IReadOnlyList<IconFigure> figures, bool filled)
    {
        var geometry = CreatePathGeometry();
        using (var sink = geometry.Open())
        {
            // Arcs and curves need the full sink, which this already is.
            var full = (ID2D1GeometrySink)sink.Object;
            foreach (var figure in figures)
            {
                full.BeginFigure(AnnotationRenderer.ToPoint(figure.Start),
                    filled ? D2D1_FIGURE_BEGIN.D2D1_FIGURE_BEGIN_FILLED : D2D1_FIGURE_BEGIN.D2D1_FIGURE_BEGIN_HOLLOW);

                foreach (var segment in figure.Segments)
                {
                    switch (segment)
                    {
                        case IconLine line:
                            full.AddLine(AnnotationRenderer.ToPoint(line.To));
                            break;
                        case IconCubic cubic:
                            var bezier = new D2D1_BEZIER_SEGMENT
                            {
                                point1 = AnnotationRenderer.ToPoint(cubic.Control1),
                                point2 = AnnotationRenderer.ToPoint(cubic.Control2),
                                point3 = AnnotationRenderer.ToPoint(cubic.To),
                            };
                            full.AddBezier(in bezier);
                            break;
                        case IconArc arc:
                            var arcSegment = new D2D1_ARC_SEGMENT
                            {
                                point = AnnotationRenderer.ToPoint(arc.To),
                                size = new D2D_SIZE_F((float)arc.RadiusX, (float)arc.RadiusY),
                                rotationAngle = (float)arc.Rotation,
                                sweepDirection = arc.Clockwise
                                    ? D2D1_SWEEP_DIRECTION.D2D1_SWEEP_DIRECTION_CLOCKWISE
                                    : D2D1_SWEEP_DIRECTION.D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE,
                                arcSize = arc.Large ? D2D1_ARC_SIZE.D2D1_ARC_SIZE_LARGE : D2D1_ARC_SIZE.D2D1_ARC_SIZE_SMALL,
                            };
                            full.AddArc(in arcSegment);
                            break;
                    }
                }

                full.EndFigure(figure.Closed ? D2D1_FIGURE_END.D2D1_FIGURE_END_CLOSED : D2D1_FIGURE_END.D2D1_FIGURE_END_OPEN);
            }
            full.Close();
        }
        return geometry;
    }

    /// <summary>
    /// The stage's dot grid as a tiling bitmap brush: one dot on a transparent tile, repeated by the
    /// GPU. Filling a window with individual dots would be thousands of ellipses a frame. Rebuilt when
    /// the colour or pitch changes, which is a theme switch or a DPI change.
    /// </summary>
    public IComObject<ID2D1BitmapBrush> DotGrid(Rgba dot, float pitch)
    {
        if (_dotGrid is { } cached && _dotGridKey == (dot, pitch)) return cached;
        _dotGrid?.Dispose();

        using var tile = _target.CreateCompatibleRenderTarget(new D2D_SIZE_F(pitch, pitch));
        tile.Object.BeginDraw();
        tile.Object.Clear(new D3DCOLORVALUE(0, 0, 0, 0));
        using (var brush = tile.CreateSolidColorBrush(ToD3D(dot)))
        {
            var ellipse = new D2D1_ELLIPSE
            {
                point = new D2D_POINT_2F(pitch / 2, pitch / 2),
                radiusX = pitch / 18,
                radiusY = pitch / 18,
            };
            tile.Object.FillEllipse(in ellipse, brush.Object);
        }
        tile.Object.EndDraw(0, 0).ThrowOnError();
        using var bitmap = tile.GetBitmap();

        var properties = new D2D1_BITMAP_BRUSH_PROPERTIES
        {
            extendModeX = D2D1_EXTEND_MODE.D2D1_EXTEND_MODE_WRAP,
            extendModeY = D2D1_EXTEND_MODE.D2D1_EXTEND_MODE_WRAP,
            interpolationMode = D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR,
        };
        _target.Object.CreateBitmapBrush(bitmap.Object, (nint)(&properties), 0, out var dotBrush).ThrowOnError();
        _dotGrid = new ComObject<ID2D1BitmapBrush>(dotBrush);
        _dotGridKey = (dot, pitch);
        return _dotGrid;
    }

    private IComObject<ID2D1BitmapBrush>? _dotGrid;
    private (Rgba, float) _dotGridKey;

    /// <summary>The grey-and-white checkerboard that shows through a translucent colour, as a tiling
    /// brush of one 2x2 tile. Anchored to the target's origin, like any tiled brush.</summary>
    public IComObject<ID2D1BitmapBrush> Checker(float cell)
    {
        if (_checker is { } cached && _checkerCell == cell) return cached;
        _checker?.Dispose();

        using var tile = _target.CreateCompatibleRenderTarget(new D2D_SIZE_F(cell * 2, cell * 2));
        tile.Object.BeginDraw();
        tile.Object.Clear(ToD3D(Rgba.White));
        using (var grey = tile.CreateSolidColorBrush(ToD3D(new Rgba(0xBB, 0xBB, 0xBB))))
        {
            var first = new D2D_RECT_F(0, 0, cell, cell);
            var second = new D2D_RECT_F(cell, cell, cell * 2, cell * 2);
            tile.Object.FillRectangle(in first, grey.Object);
            tile.Object.FillRectangle(in second, grey.Object);
        }
        tile.Object.EndDraw(0, 0).ThrowOnError();
        using var bitmap = tile.GetBitmap();

        var properties = new D2D1_BITMAP_BRUSH_PROPERTIES
        {
            extendModeX = D2D1_EXTEND_MODE.D2D1_EXTEND_MODE_WRAP,
            extendModeY = D2D1_EXTEND_MODE.D2D1_EXTEND_MODE_WRAP,
            interpolationMode = D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_NEAREST_NEIGHBOR,
        };
        _target.Object.CreateBitmapBrush(bitmap.Object, (nint)(&properties), 0, out var brush).ThrowOnError();
        _checker = new ComObject<ID2D1BitmapBrush>(brush);
        _checkerCell = cell;
        return _checker;
    }

    private IComObject<ID2D1BitmapBrush>? _checker;
    private float _checkerCell;

    /// <summary>A brush that paints <paramref name="bitmap"/> once, clamped at its edges. Not cached:
    /// it is cheap, and holding one per thumbnail would pin bitmaps the cache has evicted.</summary>
    public IComObject<ID2D1BitmapBrush> ImageBrush(IComObject<ID2D1Bitmap> bitmap)
    {
        var properties = new D2D1_BITMAP_BRUSH_PROPERTIES
        {
            extendModeX = D2D1_EXTEND_MODE.D2D1_EXTEND_MODE_CLAMP,
            extendModeY = D2D1_EXTEND_MODE.D2D1_EXTEND_MODE_CLAMP,
            interpolationMode = D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR,
        };
        _target.Object.CreateBitmapBrush(bitmap.Object, (nint)(&properties), 0, out var brush).ThrowOnError();
        return new ComObject<ID2D1BitmapBrush>(brush);
    }

    private IComObject<ID2D1StrokeStyle> CreateStroke(D2D1_STROKE_STYLE_PROPERTIES properties, float[]? dashes = null) =>
        Factory.CreateStrokeStyle(properties, dashes);

    /// <summary>
    /// The factory that created this render target.
    ///
    /// D2D refuses to use resources together that came from different factories ("Objects used
    /// together must be created from the same factory instance"), and a stroke style or path
    /// geometry is a factory resource. So geometries must come from whichever factory owns the
    /// target being drawn into - not from a convenient process-wide one.
    /// </summary>
    public IComObject<ID2D1Factory> Factory => _factory ??= GetFactory();
    private IComObject<ID2D1Factory>? _factory;

    private IComObject<ID2D1Factory> GetFactory()
    {
        _target.Object.GetFactory(out var factory);
        return new ComObject<ID2D1Factory>(factory);
    }

    /// <summary>A path geometry from the right factory. Everything that builds geometry goes
    /// through here, so it cannot accidentally use a foreign factory.</summary>
    public IComObject<ID2D1PathGeometry> CreatePathGeometry() => Factory.CreatePathGeometry();

    /// <summary>
    /// A polyline as a path geometry. <paramref name="closed"/> both closes the figure and marks it
    /// filled, which is the same distinction either way: an open figure is a line to stroke, a
    /// closed one is a region to fill.
    ///
    /// Geometries are cheap to create and are freed by the caller; the expensive resources (brushes,
    /// stroke styles) are the cached ones. They come from the shared factory rather than a render
    /// target, so one instance serves every window and the exporter.
    /// </summary>
    public IComObject<ID2D1PathGeometry> CreatePath(IReadOnlyList<Point> points, bool closed)
    {
        var geometry = CreatePathGeometry();
        using (var sink = geometry.Open())
        {
            sink.Object.BeginFigure(
                AnnotationRenderer.ToPoint(points[0]),
                closed ? D2D1_FIGURE_BEGIN.D2D1_FIGURE_BEGIN_FILLED
                       : D2D1_FIGURE_BEGIN.D2D1_FIGURE_BEGIN_HOLLOW);

            for (var i = 1; i < points.Count; i++)
                sink.Object.AddLine(AnnotationRenderer.ToPoint(points[i]));

            sink.Object.EndFigure(closed ? D2D1_FIGURE_END.D2D1_FIGURE_END_CLOSED
                                         : D2D1_FIGURE_END.D2D1_FIGURE_END_OPEN);
            sink.Object.Close();
        }
        return geometry;
    }

    public static D3DCOLORVALUE ToD3D(Rgba color) =>
        new(color.A / 255f, color.R / 255f, color.G / 255f, color.B / 255f);

    public void Dispose()
    {
        foreach (var brush in _brushes.Values) brush.Dispose();
        foreach (var format in _formats.Values) format.Dispose();
        _brushes.Clear();
        _formats.Clear();
        _measurements.Clear();
        _scratchBrush?.Dispose();
        _scratchBrush = null;
        _gradientBrush?.Dispose();
        _gradientBrush = null;
        _gradientStops = null;
        _radialBrush?.Dispose();
        _radialBrush = null;
        _radialStops = null;
        foreach (var (stroke, fill) in _icons.Values) { stroke.Dispose(); fill?.Dispose(); }
        _icons.Clear();
        _dotGrid?.Dispose();
        _dotGrid = null;
        _checker?.Dispose();
        _checker = null;
        _roundStroke?.Dispose();
        _roundStroke = null;
        _dwrite?.Dispose();
        _dwrite = null;
        _factory?.Dispose();
        _factory = null;
    }
}
