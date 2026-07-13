namespace NexusShot.Render;

/// <summary>
/// Views of a COM object as one of its base interfaces.
///
/// DirectN's extension methods are declared against base interfaces (ID2D1Geometry,
/// ID2D1SimplifiedGeometrySink, ID2D1RenderTarget), but the factory hands back derived ones
/// (ID2D1PathGeometry, ID2D1GeometrySink, ID2D1HwndRenderTarget). These wrap the same pointer
/// without taking a reference, so disposing the view never releases the object it borrows.
/// </summary>
internal static class ComCasts
{
    public static IComObject<ID2D1Geometry> AsGeometry(this IComObject<ID2D1PathGeometry> geometry) =>
        new ComObject<ID2D1Geometry>(geometry.Object, releaseOnDispose: false);

    public static IComObject<ID2D1Geometry> AsGeometry(
        this IComObject<ID2D1RoundedRectangleGeometry> geometry) =>
        new ComObject<ID2D1Geometry>(geometry.Object, releaseOnDispose: false);

    public static IComObject<ID2D1SimplifiedGeometrySink> AsSimplifiedSink(this IComObject<ID2D1GeometrySink> sink) =>
        new ComObject<ID2D1SimplifiedGeometrySink>(sink.Object, releaseOnDispose: false);

    public static IComObject<ID2D1RenderTarget> AsRenderTarget(this IComObject<ID2D1HwndRenderTarget> target) =>
        new ComObject<ID2D1RenderTarget>(target.Object, releaseOnDispose: false);

    /// <summary>The render target as a device context, or null when it does not support one.
    /// Effects (blur, pixelate) are a device-context feature.</summary>
    public static IComObject<ID2D1DeviceContext>? AsDeviceContext(this IComObject<ID2D1RenderTarget> target) =>
        target.Object is ID2D1DeviceContext context
            ? new ComObject<ID2D1DeviceContext>(context, releaseOnDispose: false)
            : null;

    /// <summary>A bitmap as an effect input. Every ID2D1Bitmap is an ID2D1Image.</summary>
    public static IComObject<ID2D1Image> AsImage(this IComObject<ID2D1Bitmap> bitmap) =>
        new ComObject<ID2D1Image>(bitmap.Object, releaseOnDispose: false);

    /// <summary>A device context as the plain render target the renderer draws against.</summary>
    public static IComObject<ID2D1RenderTarget> AsRenderTarget2(this IComObject<ID2D1DeviceContext> context) =>
        new ComObject<ID2D1RenderTarget>(context.Object, releaseOnDispose: false);
}
