using System.Runtime.InteropServices;
using NexusShot.Core;

namespace NexusShot.Render;

/// <summary>
/// Blur and pixelate, rendered on the GPU.
public sealed class PixelEffectSource(ImageSurface image, D2DResources resources)
    : IPixelEffectSource, IDisposable
{
    private IComObject<ID2D1Effect>? _blur;
    private IComObject<ID2D1Effect>? _downscale;
    private IComObject<ID2D1Effect>? _upscale;
    /// <summary>One mask per annotation, stamped with the geometry version it was built from.</summary>
    private readonly Dictionary<Guid, (long Stamp, IComObject<ID2D1PathGeometry> Mask)> _maskCache = [];

    /// <summary>The generation this cache was last pruned against.</summary>
    private long _prunedGeneration = -1;

    /// <summary>Drops masks for annotations that are no longer in the document; without this a
    /// deleted stroke's geometry lives until the whole source is disposed. Skipped while the
    /// document's annotation set is unchanged, which is every frame of a plain drag.</summary>
    public void PruneMasks(IReadOnlyList<Annotation> annotations, long generation)
    {
        if (_prunedGeneration == generation) return;
        _prunedGeneration = generation;

        if (_maskCache.Count == 0) return;

        var live = new HashSet<Guid>(annotations.Count);
        foreach (var annotation in annotations) live.Add(annotation.Id);

        List<Guid>? dead = null;
        foreach (var id in _maskCache.Keys) if (!live.Contains(id)) (dead ??= []).Add(id);
        if (dead is null) return;

        foreach (var id in dead)
        {
            _maskCache[id].Mask.Dispose();
            _maskCache.Remove(id);
        }
    }

    public void DrawBrushEffect(IComObject<ID2D1DeviceContext> context, Annotation annotation)
    {
        if (annotation.Points.Count == 0) return;

        var output = annotation.Tool == EditorTool.Pixelate
            ? Pixelate(context, annotation)
            : Blur(context, annotation);
        if (output is null) return;

        var mask = WidenedStroke(annotation);
        if (mask is null) return;

        // The painted path is the layer's geometric mask, so the effect shows only where the brush
        // actually went. The GPU clips it; nothing is computed per pixel on our side.
        //
        // geometricMask is a raw COM pointer in the AOT-generated struct. GetOrCreateComInstance
        // hands back an AddRef'd pointer, so it is released once the layer is popped.
        var maskPointer = ComObject.GetOrCreateComInstance(mask.Object);
        try
        {
            context.PushLayer(new D2D1_LAYER_PARAMETERS1
            {
                contentBounds = InfiniteRect,
                geometricMask = maskPointer,
                maskAntialiasMode = D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE,
                maskTransform = D2D_MATRIX_3X2_F.Identity(),
                opacity = 1,
                layerOptions = D2D1_LAYER_OPTIONS1.D2D1_LAYER_OPTIONS1_NONE,
            });

            using var effectOutput = output.GetOutput();
            context.DrawImage(effectOutput);

            context.PopLayer();
        }
        finally
        {
            if (maskPointer != 0) Marshal.Release(maskPointer);
        }
    }

    /// <summary>Gaussian blur. The radius tracks the brush size, so a bigger brush obscures more
    /// aggressively - the behaviour the software version had.</summary>
    private IComObject<ID2D1Effect>? Blur(IComObject<ID2D1DeviceContext> context, Annotation annotation)
    {
        _blur ??= context.CreateEffect(Constants.CLSID_D2D1GaussianBlur);
        if (_blur is null) return null;

        _blur.SetInput(image.Bitmap.AsImage());
        _blur.SetValue(
            (uint)D2D1_GAUSSIANBLUR_PROP.D2D1_GAUSSIANBLUR_PROP_STANDARD_DEVIATION,
            (float)Math.Max(2, annotation.BrushRadius / 2));
        return _blur;
    }

    /// <summary>
    /// Pixelate: shrink with nearest-neighbour, then blow it back up the same way. Two GPU passes
    /// reproduce the blocky output the software loop computed pixel by pixel.
    /// </summary>
    private IComObject<ID2D1Effect>? Pixelate(IComObject<ID2D1DeviceContext> context, Annotation annotation)
    {
        _downscale ??= context.CreateEffect(Constants.CLSID_D2D1Scale);
        _upscale ??= context.CreateEffect(Constants.CLSID_D2D1Scale);
        if (_downscale is null || _upscale is null) return null;

        var cell = (float)Math.Max(4, annotation.BrushRadius / 2);

        _downscale.SetInput(image.Bitmap.AsImage());
        SetScale(_downscale, 1f / cell);

        _upscale.SetInput(_downscale);
        SetScale(_upscale, cell);
        return _upscale;

        static void SetScale(IComObject<ID2D1Effect> effect, float factor)
        {
            effect.SetValue((uint)D2D1_SCALE_PROP.D2D1_SCALE_PROP_SCALE, new D2D_VECTOR_2F(factor, factor));

            // The property is typed as its enum, not a raw uint: D2D validates the variant type and
            // rejects a plain integer with E_INVALIDARG.
            effect.SetValue(
                (uint)D2D1_SCALE_PROP.D2D1_SCALE_PROP_INTERPOLATION_MODE,
                D2D1_SCALE_INTERPOLATION_MODE.D2D1_SCALE_INTERPOLATION_MODE_NEAREST_NEIGHBOR);
        }
    }

    /// <summary>The painted path as a closed region: the effect's mask. Same widening the renderer
    /// uses for the stroke itself, so the effect lands exactly where paint would have.</summary>
    private IComObject<ID2D1PathGeometry>? WidenedStroke(Annotation annotation)
    {
        var points = annotation.Points;
        var stamp = annotation.GeometryVersion;
        if (_maskCache.TryGetValue(annotation.Id, out var cached))
        {
            if (cached.Stamp == stamp) return cached.Mask;
            cached.Mask.Dispose();
            _maskCache.Remove(annotation.Id);
        }

        using var line = resources.CreatePathGeometry();
        using (var sink = line.Open())
        {
            sink.Object.BeginFigure(
                AnnotationRenderer.ToPoint(points[0]), D2D1_FIGURE_BEGIN.D2D1_FIGURE_BEGIN_HOLLOW);
            for (var i = 1; i < points.Count; i++) sink.Object.AddLine(AnnotationRenderer.ToPoint(points[i]));
            sink.Object.EndFigure(D2D1_FIGURE_END.D2D1_FIGURE_END_OPEN);
            sink.Object.Close();
        }

        var widened = resources.CreatePathGeometry();
        using (var sink = widened.Open())
        {
            line.AsGeometry().Widen(
                sink,
                (float)(annotation.BrushRadius * 2),
                resources.RoundStroke);
            sink.Object.Close();
        }
        _maskCache[annotation.Id] = (stamp, widened);
        return widened;
    }

    internal static readonly D2D_RECT_F InfiniteRect =
        new(-float.MaxValue, -float.MaxValue, float.MaxValue, float.MaxValue);

    public void Dispose()
    {
        foreach (var entry in _maskCache.Values) entry.Mask.Dispose();
        _maskCache.Clear();
        _blur?.Dispose();
        _downscale?.Dispose();
        _upscale?.Dispose();
        _blur = null;
        _downscale = null;
        _upscale = null;
    }
}
