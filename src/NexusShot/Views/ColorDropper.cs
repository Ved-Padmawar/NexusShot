using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>
/// The eyedropper: the frozen desktop, a magnifier following the pointer, and the colour of the pixel
/// under it. A click takes the colour; Escape or a right-click leaves.
///
/// Windows has no system eyedropper, so this is built the way the region picker is: a snapshot taken
/// before the window appears, drawn full-screen. Sampling the snapshot rather than the live screen
/// means the loupe can never sample itself.
/// </summary>
public sealed unsafe class ColorDropper : D2DRenderWindow
{
    protected override void CreateRenderTarget()
    {
        RenderTarget?.Dispose();
        RenderTarget = null;
        RenderTarget = GraphicsBackend.CreateWindowTarget(Handle, ClientRect.Size.ToD2D_SIZE_U(), FactoryType, FactoryOptions);
    }
    protected override DirectN.Extensions.Utilities.Icon? LoadCreationIcon() => null;

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;

    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmRButtonDown = 0x0204;
    private const uint WmKeyDown = 0x0100;
    private const uint WmSetCursor = 0x0020;

    /// <summary>How many snapshot pixels the loupe shows across, and how big each is drawn. Odd, so
    /// one pixel is the centre.</summary>
    private const int LoupePixels = 11;
    private const int LoupeCell = 11;

    private readonly RectInt _desktop;
    private readonly DecodedImage _pixels;
    private readonly Theme _theme;

    private D2DResources? _resources;
    private Ui? _ui;
    private ImageSurface? _snapshot;
    private Point _cursor = new(-1, -1);

    public Rgba? Picked { get; private set; }

    private ColorDropper(RectInt desktop, DecodedImage pixels, Theme theme)
        : base("NexusShot eyedropper", (WINDOW_STYLE)WS_POPUP, (WINDOW_EX_STYLE)(WS_EX_TOPMOST | WS_EX_TOOLWINDOW))
    {
        _desktop = desktop;
        _pixels = pixels;
        _theme = theme;
    }

    private static bool _isPicking;

    /// <summary>Runs the eyedropper to completion. Blocking, like the region picker: choosing a pixel
    /// is a modal act.</summary>
    public static Rgba? Pick(Theme theme)
    {
        if (_isPicking) return null;
        _isPicking = true;
        try
        {
            var desktop = ScreenCapture.VirtualDesktop;
            using var pixels = ScreenCapture.Capture(desktop);
            using var dropper = new ColorDropper(desktop, pixels, theme);
            if (WindowInterop.GetCursorPos(out var start))
                dropper._cursor = new Point(start.X - desktop.X, start.Y - desktop.Y);
            ModalLoop.Run(dropper, desktop);
            return dropper.Picked;
        }
        finally
        {
            _isPicking = false;
        }
    }

    /// <summary>The snapshot pixel at a client point. The desktop is opaque, so the premultiplied
    /// bytes are the straight colour.</summary>
    private Rgba? Sample(Point point)
    {
        var x = (int)point.X;
        var y = (int)point.Y;
        if (x < 0 || y < 0 || x >= _pixels.Width || y >= _pixels.Height) return null;
        var pixel = _pixels.Span.Slice(y * _pixels.Stride + x * 4, 4);
        return new Rgba(pixel[2], pixel[1], pixel[0]);
    }

    protected override void Render(IComObject<ID2D1HwndRenderTarget> renderTarget)
    {
        using var target = renderTarget.AsRenderTarget();
        target.Object.SetDpi(96, 96);

        if (_resources is null)
        {
            _resources = new D2DResources(target);
            _ui = new Ui(_resources) { Theme = _theme };
            using var context = target.AsDeviceContext();
            if (context is not null) _snapshot = ImageSurface.Upload(_pixels, context);
        }
        if (_snapshot is null || _ui is null) return;

        var ui = _ui;
        ui.Scale = Functions.GetDpiForWindow(Handle) / 96.0;
        ui.BeginFrame(target, _cursor, false);

        var full = new D2D_RECT_F(0, 0, _desktop.Width, _desktop.Height);
        target.Object.DrawBitmap(_snapshot.Bitmap.Object, (nint)(&full), 1f,
            D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, 0);

        if (Sample(_cursor) is { } color) DrawLoupe(ui, target, color);
        ui.EndFrame();
    }

    /// <summary>The magnifier: the pixels around the pointer, drawn large and unsmoothed, with the
    /// centre one ringed and its hex beneath. It sits below-right of the pointer and flips at the
    /// screen edges, so it never covers what is being aimed at.</summary>
    private void DrawLoupe(Ui ui, IComObject<ID2D1RenderTarget> target, Rgba color)
    {
        var s = ui.Scale;
        var cell = LoupeCell * s;
        var size = LoupePixels * cell;
        var label = 30 * s;

        var x = _cursor.X + 24 * s;
        var y = _cursor.Y + 24 * s;
        if (x + size > _desktop.Width) x = _cursor.X - 24 * s - size;
        if (y + size + label > _desktop.Height) y = _cursor.Y - 24 * s - size - label;

        var loupe = new Rect(x, y, size, size);
        var radius = (float)(Metrics.RadiusLg * s);
        ui.Shadow(loupe, radius, 22 * s, 10 * s, Rgba.Black.WithAlpha(120));

        // Nearest-neighbour, so each screen pixel lands as a crisp square rather than a blur.
        var half = LoupePixels / 2;
        var source = new D2D_RECT_F(
            (float)Math.Floor(_cursor.X) - half, (float)Math.Floor(_cursor.Y) - half,
            (float)Math.Floor(_cursor.X) + half + 1, (float)Math.Floor(_cursor.Y) + half + 1);
        var destination = AnnotationRenderer.ToRect(loupe);
        ui.PushRoundedLayer(loupe, radius);
        ui.FillRect(loupe, Rgba.Black);
        target.Object.DrawBitmap(_snapshot!.Bitmap.Object, (nint)(&destination), 1f,
            D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_NEAREST_NEIGHBOR, (nint)(&source));
        ui.PopRoundedLayer();
        ui.StrokeRounded(loupe, radius, Rgba.White.WithAlpha(200), (float)(2 * s));

        var centre = new Rect(loupe.X + half * cell, loupe.Y + half * cell, cell, cell);
        ui.StrokeRounded(centre.Deflate(-1 * s), 0, Rgba.Black.WithAlpha(160), (float)(3 * s));
        ui.StrokeRounded(centre, 0, Rgba.White, (float)(1.5 * s));

        var hex = color.ToHex();
        var font = 12 * s;
        var pill = new Rect(loupe.X, loupe.Bottom + 6 * s, loupe.Width, label - 6 * s);
        ui.FillRounded(pill, (float)(pill.Height / 2), _theme.SurfaceRaised);
        ui.StrokeRounded(pill, (float)(pill.Height / 2), _theme.StrokeDefault);
        var well = new Rect(pill.X + 6 * s, pill.Center.Y - 7 * s, 14 * s, 14 * s);
        ui.FillRounded(well, (float)(3 * s), color);
        ui.StrokeRounded(well, (float)(3 * s), _theme.StrokeStrong);
        ui.Text(hex, new Rect(well.Right + 6 * s, pill.Y, pill.Width - 26 * s, pill.Height), _theme.TextPrimary, font,
            Weight.Semibold, face: Face.Mono);
    }

    protected override LRESULT? WindowProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case WmSetCursor:
                DirectN.Extensions.Utilities.Cursor.Set(DirectN.Extensions.Utilities.Cursor.Cross);
                return new LRESULT { Value = 1 };

            case WmMouseMove:
                _cursor = ClientPoint(lParam);
                Invalidate();
                return new LRESULT { Value = 0 };

            case WmLButtonDown:
                Picked = Sample(ClientPoint(lParam));
                Close();
                return new LRESULT { Value = 0 };

            case WmRButtonDown:
                Close();
                return new LRESULT { Value = 0 };

            case WmKeyDown:
                if ((VIRTUAL_KEY)(ulong)wParam.Value == VIRTUAL_KEY.VK_ESCAPE) Close();
                return new LRESULT { Value = 0 };
        }
        return base.WindowProc(hwnd, msg, wParam, lParam);
    }

    private static Point ClientPoint(LPARAM lParam)
    {
        var value = lParam.Value.ToInt64();
        return new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
    }

    /// <summary>A full-desktop snapshot is the largest bitmap the app makes, so it goes the moment
    /// the window does rather than waiting for Dispose.</summary>
    protected override void OnDestroyed(object? sender, EventArgs e)
    {
        ReleaseResources();
        base.OnDestroyed(sender, e);
    }

    private void ReleaseResources()
    {
        _snapshot?.Dispose();
        _snapshot = null;
        _ui = null;
        _resources?.Dispose();
        _resources = null;
    }

    protected override void Dispose(bool disposing)
    {
        ReleaseResources();
        base.Dispose(disposing);
    }
}
