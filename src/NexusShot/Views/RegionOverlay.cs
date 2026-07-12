using System.Runtime.InteropServices;
using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>
/// The region picker: a full-desktop window showing a frozen snapshot of the screen, dimmed, with a
/// bright cut-out that follows the drag.
///
/// It draws a *snapshot* rather than being transparent over the live desktop. That is what makes
/// the selection stable - a live overlay has to fight the compositor and can catch its own dimming
/// in the capture. The snapshot is taken before the window appears, so what the user selects is
/// exactly what they get.
/// </summary>
public sealed class RegionOverlay : D2DRenderWindow
{
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;

    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmKeyDown = 0x0100;
    private const uint WmSetCursor = 0x0020;

    private readonly RectInt _desktop;
    private readonly string _snapshotPath;

    private D2DResources? _resources;
    private ImageSurface? _snapshot;

    private Point _origin;
    private Point _cursor;
    private bool _dragging;
    private bool _hasSelection;

    /// <summary>The chosen region in desktop coordinates, or null if cancelled.</summary>
    public RectInt? Selection { get; private set; }

    public RegionOverlay(RectInt desktop, string snapshotPath)
        : base("NexusShot region",
            (WINDOW_STYLE)WS_POPUP,
            (WINDOW_EX_STYLE)(WS_EX_TOPMOST | WS_EX_TOOLWINDOW))
    {
        _desktop = desktop;
        _snapshotPath = snapshotPath;
    }

    /// <summary>
    /// Runs the picker to completion and returns the region. Blocking, because a capture is a modal
    /// act: nothing else in the app can meaningfully happen while the user is choosing what to grab.
    /// </summary>
    public static RectInt? Pick()
    {
        var desktop = ScreenCapture.VirtualDesktop;
        var snapshot = ScreenCapture.Capture(desktop);

        try
        {
            using var overlay = new RegionOverlay(desktop, snapshot);
            SetWindowPos(overlay.Handle, IntPtr.Zero,
                desktop.X, desktop.Y, desktop.Width, desktop.Height, 0);
            overlay.Show();
            overlay.SetForeground();

            // A private message loop: the overlay owns the desktop until it resolves. It ends when
            // the window is destroyed, which Commit and Escape both do.
            while (overlay.IsWindow && GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }
            return overlay.Selection;
        }
        finally
        {
            try { File.Delete(snapshot); } catch (IOException) { /* a temp file we can leak */ }
        }
    }

    protected override void Render(IComObject<ID2D1HwndRenderTarget> renderTarget)
    {
        using var target = renderTarget.AsRenderTarget();
        target.Object.SetDpi(96, 96);

        if (_resources is null)
        {
            _resources = new D2DResources(target);
            using var context = target.AsDeviceContext();
            if (context is not null) _snapshot = ImageSurface.Load(_snapshotPath, context);
        }

        if (_snapshot is null) return;
        var ui = new Ui(_resources) { Theme = Theme.Dark };
        ui.BeginFrame(target, _cursor, _dragging);

        var full = new Rect(0, 0, _desktop.Width, _desktop.Height);

        // The frozen desktop, then a dim over all of it.
        renderTarget.DrawBitmap(
            _snapshot.Bitmap, 1f,
            D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR,
            AnnotationRenderer.ToRect(full));

        var selection = CurrentSelection();

        if (!_hasSelection || selection.IsEmpty)
        {
            ui.FillRect(full, Rgba.Black.WithAlpha(110));
            DrawHint(ui, full);
            ui.EndFrame();
            return;
        }

        // Dim everything except the selection, so the cut-out shows the true pixels.
        foreach (var band in AdornerGeometry.DimAround(selection, full.Width, full.Height))
            ui.FillRect(band, Rgba.Black.WithAlpha(110));

        ui.StrokeRounded(selection, 0, Palette.Selection, 1.5f);
        DrawSizeBadge(ui, selection);
        ui.EndFrame();
    }

    private void DrawHint(Ui ui, Rect full)
    {
        const string hint = "Drag to select an area    •    Esc to cancel";
        var box = new Rect(full.Center.X - 200, full.Center.Y - 22, 400, 44);
        ui.FillRounded(box, 8, Rgba.Black.WithAlpha(190));
        ui.Text(hint, box, Rgba.White.WithAlpha(220), Metrics.FontBody, align: TextAlign.Center);
    }

    /// <summary>The live pixel dimensions, pinned just outside the selection so it never covers the
    /// content being selected.</summary>
    private void DrawSizeBadge(Ui ui, Rect selection)
    {
        var label = $"{(int)selection.Width} × {(int)selection.Height}";
        var width = 110.0;
        var height = 26.0;

        // Below the selection normally; above it when there is no room below.
        var y = selection.Bottom + 8;
        if (y + height > _desktop.Height) y = Math.Max(0, selection.Top - height - 8);

        var box = new Rect(selection.X, y, width, height);
        ui.FillRounded(box, 4, Rgba.Black.WithAlpha(200));
        ui.Text(label, box, Rgba.White, Metrics.FontCaption, align: TextAlign.Center);
    }

    private Rect CurrentSelection() => Rect.FromEdges(_origin.X, _origin.Y, _cursor.X, _cursor.Y);

    protected override LRESULT? WindowProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case WmSetCursor:
                DirectN.Extensions.Utilities.Cursor.Set(DirectN.Extensions.Utilities.Cursor.Cross);
                return new LRESULT { Value = 1 };

            case WmLButtonDown:
                _origin = _cursor = ClientPoint(lParam);
                _dragging = true;
                _hasSelection = true;
                Invalidate();
                return new LRESULT { Value = 0 };

            case WmMouseMove:
                _cursor = ClientPoint(lParam);
                Invalidate();
                return new LRESULT { Value = 0 };

            case WmLButtonUp:
                if (_dragging)
                {
                    _dragging = false;
                    _cursor = ClientPoint(lParam);
                    Commit();
                }
                return new LRESULT { Value = 0 };

            case WmKeyDown:
                if ((VIRTUAL_KEY)(ulong)wParam.Value == VIRTUAL_KEY.VK_ESCAPE)
                {
                    Selection = null;
                    Close();
                }
                return new LRESULT { Value = 0 };
        }
        return base.WindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>Accepts the region, in desktop coordinates. A click without a drag is a cancel, not
    /// a zero-pixel capture.</summary>
    private void Commit()
    {
        var selection = CurrentSelection();
        if (selection.Width < 2 || selection.Height < 2)
        {
            Selection = null;
        }
        else
        {
            Selection = new RectInt(
                _desktop.X + (int)Math.Round(selection.X),
                _desktop.Y + (int)Math.Round(selection.Y),
                (int)Math.Round(selection.Width),
                (int)Math.Round(selection.Height));
        }
        Close();
    }

    private static Point ClientPoint(LPARAM lParam)
    {
        var value = lParam.Value.ToInt64();
        return new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
    }

    protected override void Dispose(bool disposing)
    {
        _snapshot?.Dispose();
        _resources?.Dispose();
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG message, IntPtr window, uint min, uint max);

    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG message);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }
}
