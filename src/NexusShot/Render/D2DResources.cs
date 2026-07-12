using NexusShot.Core;

namespace NexusShot.Render;

/// <summary>
/// Per-render-target cache for brushes, stroke styles and text formats.
///
/// This exists because the whole point of the rewrite is that a frame must not allocate. The XAML
/// renderer created a fresh SolidColorBrush for every shape on every pointer move; here a colour
/// maps to one brush that lives as long as the render target. Resources are keyed by value, so
/// callers just ask for what they want and get a cached instance.
///
/// The render target owns device resources, so this is rebuilt whenever the target is.
/// </summary>
public sealed class D2DResources : IDisposable
{
    private readonly IComObject<ID2D1RenderTarget> _target;
    private readonly Dictionary<Rgba, IComObject<ID2D1SolidColorBrush>> _brushes = [];
    private readonly Dictionary<(float On, float Off), IComObject<ID2D1StrokeStyle>> _dashStyles = [];
    private readonly Dictionary<(string Family, float Size, bool Bold, bool Italic), IComObject<IDWriteTextFormat>> _formats = [];

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
        _brushes[color] = brush;
        return brush;
    }

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

    /// <summary>A dashed stroke. The dash array is in stroke-width units, matching XAML's
    /// StrokeDashArray, so [3,3] and [5,3] keep the dash rhythm the old adorners had.</summary>
    public IComObject<ID2D1StrokeStyle> DashStroke(float on, float off)
    {
        if (_dashStyles.TryGetValue((on, off), out var cached)) return cached;
        var style = CreateStroke(new D2D1_STROKE_STYLE_PROPERTIES
        {
            startCap = D2D1_CAP_STYLE.D2D1_CAP_STYLE_FLAT,
            endCap = D2D1_CAP_STYLE.D2D1_CAP_STYLE_FLAT,
            lineJoin = D2D1_LINE_JOIN.D2D1_LINE_JOIN_MITER,
            dashCap = D2D1_CAP_STYLE.D2D1_CAP_STYLE_FLAT,
            dashStyle = D2D1_DASH_STYLE.D2D1_DASH_STYLE_CUSTOM,
            miterLimit = 10,
        }, [on, off]);
        _dashStyles[(on, off)] = style;
        return style;
    }

    public IComObject<IDWriteTextFormat> TextFormat(string family, float size, bool bold, bool italic)
    {
        var key = (family, size, bold, italic);
        if (_formats.TryGetValue(key, out var cached)) return cached;

        var format = DWrite.CreateTextFormat(
            family,
            size,
            weight: bold ? DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_BOLD : DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL,
            style: italic ? DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_ITALIC : DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL);
        _formats[key] = format;
        return format;
    }

    private IComObject<ID2D1StrokeStyle> CreateStroke(D2D1_STROKE_STYLE_PROPERTIES properties, float[]? dashes = null) =>
        D2DFactory.CreateStrokeStyle(properties, dashes);

    /// <summary>
    /// One process-wide factory. Stroke styles and path geometries are factory resources, not
    /// device resources: they survive a lost device and are shared by every window and the
    /// exporter, so there is no reason to make one per render target.
    /// </summary>
    public static readonly IComObject<ID2D1Factory1> D2DFactory =
        D2D1Functions.D2D1CreateFactory<ID2D1Factory1>(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_MULTI_THREADED);

    public static D3DCOLORVALUE ToD3D(Rgba color) =>
        new(color.A / 255f, color.R / 255f, color.G / 255f, color.B / 255f);

    public void Dispose()
    {
        foreach (var brush in _brushes.Values) brush.Dispose();
        foreach (var style in _dashStyles.Values) style.Dispose();
        foreach (var format in _formats.Values) format.Dispose();
        _brushes.Clear();
        _dashStyles.Clear();
        _formats.Clear();
        _roundStroke?.Dispose();
        _roundStroke = null;
        _dwrite?.Dispose();
        _dwrite = null;
    }
}
