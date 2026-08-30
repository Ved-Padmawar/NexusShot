using NexusShot.Core;

namespace NexusShot.Render;

/// <summary>
/// Per-render-target cache for brushes, stroke styles and text formats, so a frame does not
/// allocate: a colour maps to one brush that lives as long as the render target. Resources are
/// keyed by value, so callers ask for what they want and get a cached instance.
///
/// The render target owns device resources, so this is rebuilt whenever the target is.
/// </summary>
public sealed class D2DResources : IDisposable
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
        bool Bold,
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
        bool bold,
        bool italic,
        DWRITE_TEXT_ALIGNMENT alignment = DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_LEADING,
        DWRITE_PARAGRAPH_ALIGNMENT paragraphAlignment = DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_NEAR,
        DWRITE_WORD_WRAPPING wordWrapping = DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_WRAP)
    {
        var key = (family, size, bold, italic, alignment, paragraphAlignment, wordWrapping);
        if (_formats.TryGetValue(key, out var cached)) return cached;

        var format = DWrite.CreateTextFormat(
            family,
            size,
            weight: bold ? DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_BOLD : DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL,
            style: italic ? DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_ITALIC : DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL);

        format.Object.SetTextAlignment(alignment);
        format.Object.SetParagraphAlignment(paragraphAlignment);
        format.Object.SetWordWrapping(wordWrapping);

        if (_formats.Add(key, format, out var evicted)) evicted.Dispose();
        return format;
    }

    /// <summary>The rendered width of a string. Cached because measuring realises an
    /// IDWriteTextLayout, and callers measure to centre or size a widget every frame.</summary>
    public double MeasureText(string text, string family, float size, bool bold)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var key = (text, family, size, bold);
        if (_measurements.TryGetValue(key, out var cached)) return cached;

        var format = TextFormat(family, size, bold, italic: false);
        using var layout = DWrite.CreateTextLayout(format, text);
        layout.Object.GetMetrics(out var metrics);

        _measurements.Add(key, metrics.width, out _);
        return metrics.width;
    }

    private readonly LruCache<(string Text, string Family, float Size, bool Bold), double> _measurements =
        new(MaxMeasurements);

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
        _roundStroke?.Dispose();
        _roundStroke = null;
        _dwrite?.Dispose();
        _dwrite = null;
        _factory?.Dispose();
        _factory = null;
    }
}
