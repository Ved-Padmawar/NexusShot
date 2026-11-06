using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;
using static NexusShot.Views.Win32Window;

namespace NexusShot.Views;

/// <summary>
/// The eyedropper: the frozen desktop, a magnifier following the pointer, and the colour of the pixel
/// under it. A click takes the colour; Escape or a right-click leaves.
///
/// Windows has no system eyedropper, so this is built the way the region picker is: a snapshot taken
/// before the window appears, drawn full-screen. Sampling the snapshot rather than the live screen
/// means the loupe can never sample itself.
/// </summary>
public sealed unsafe class ColorDropper : PopupWindow
{
    private readonly RectInt _desktop;
    private readonly DecodedImage _pixels;
    private readonly Theme _theme;

    private Ui? _ui;
    private ImageSurface? _snapshot;
    private Point _cursor = new(-1, -1);

    public Rgba? Picked { get; private set; }

    private ColorDropper(RectInt desktop, DecodedImage pixels, Theme theme)
        : base("NexusShot eyedropper", software: false)
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

    protected override void Render(IComObject<ID2D1HwndRenderTarget> renderTarget)
    {
        using var target = renderTarget.AsRenderTarget();
        target.Object.SetDpi(96, 96);

        if (_ui is null)
        {
            _ui = new Ui(Resources(target)) { Theme = _theme };
            using var context = target.AsDeviceContext();
            if (context is not null) _snapshot = ImageSurface.Upload(_pixels, context);
        }
        if (_snapshot is null || _ui is null) return;

        var ui = _ui;
        ui.Scale = Functions.GetDpiForWindow(Handle) / 96.0;
        ui.BeginFrame(target, _cursor);

        var full = new D2D_RECT_F(0, 0, _desktop.Width, _desktop.Height);
        target.Object.DrawBitmap(_snapshot.Bitmap.Object, (nint)(&full), 1f,
            D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, 0);

        if (_pixels.OpaquePixelAt((int)_cursor.X, (int)_cursor.Y) is { } color)
            Loupe.Draw(ui, target, _snapshot, _cursor, new Size(_desktop.Width, _desktop.Height), color.ToHex(), color);
        ui.EndFrame();
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
                var picked = ClientPoint(lParam);
                Picked = _pixels.OpaquePixelAt((int)picked.X, (int)picked.Y);
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
    }

    protected override void Dispose(bool disposing)
    {
        ReleaseResources();
        base.Dispose(disposing);
    }
}
