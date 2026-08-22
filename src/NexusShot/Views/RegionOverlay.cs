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
    private readonly DecodedImage _snapshotPixels;

    private D2DResources? _resources;
    private ImageSurface? _snapshot;

    private Point _origin;
    private Point _cursor;
    private bool _dragging;
    private bool _hasSelection;

    /// <summary>The chosen region in desktop coordinates, or null if cancelled.</summary>
    public RectInt? Selection { get; private set; }

    public RegionOverlay(RectInt desktop, DecodedImage snapshotPixels)
        : base("NexusShot region",
            (WINDOW_STYLE)WS_POPUP,
            (WINDOW_EX_STYLE)(WS_EX_TOPMOST | WS_EX_TOOLWINDOW))
    {
        _desktop = desktop;
        _snapshotPixels = snapshotPixels;
    }

    /// <summary>
    /// Runs the picker to completion and returns the captured region as a temp PNG, or null if
    /// cancelled. Blocking, because a capture is a modal act: nothing else in the app can
    /// meaningfully happen while the user is choosing what to grab.
    ///
    /// The result is cropped from the frozen snapshot, never re-captured from the live screen: the
    /// overlay's own activation dismisses any open menu, so a re-capture saves a changed desktop.
    /// </summary>
    private static bool _isPicking;

    public static string? Pick()
    {
        if (_isPicking) return null;
        _isPicking = true;
        var desktop = ScreenCapture.VirtualDesktop;
        var snapshot = ScreenCapture.Capture(desktop);

        try
        {
            RectInt? selection;
            using (var overlay = new RegionOverlay(desktop, snapshot))
            {
                WindowInterop.SetWindowPos(overlay.Handle, IntPtr.Zero,
                    desktop.X, desktop.Y, desktop.Width, desktop.Height, 0);
                overlay.Show();
                overlay.SetForeground();

                // Filtered at the API, not at dispatch: a non-matching message stays queued for the
                // main pump rather than being dropped.
                var result = 0;
                while (overlay.IsWindow
                    && (result = GetMessageW(out var message, overlay.Handle, 0, 0)) > 0)
                {
                    TranslateMessage(ref message);
                    DispatchMessageW(ref message);
                }

                // WM_QUIT arrives regardless of the filter, so a tray Exit lands here: re-post it
                // for the main loop. (-1 is an error and must not be treated as a quit.)
                if (result == 0) PostQuitMessage(0);
                selection = overlay.Selection;
            }

            if (selection is not { } region) return null;

            // Cropped from the snapshot already in memory: no decode, and the only PNG encode in
            // the whole pick is this one, of the selected region rather than the whole desktop.
            var cropped = CropPixels(
                snapshot, region.X - desktop.X, region.Y - desktop.Y, region.Width, region.Height);

            var path = Path.Combine(Path.GetTempPath(), $"NexusShot_{Guid.NewGuid():N}.png");
            PngWriter.Write(path, cropped.Pixels, cropped.Width, cropped.Height);
            return path;
        }
        finally
        {
            _isPicking = false;
        }
    }

    /// <summary>
    /// Copies one rectangle out of a decoded image, clamped to its bounds.
    ///
    /// The origin is pinned inside the image before the size is clamped, so a selection rounded a
    /// pixel past the desktop edge narrows to the last row/column rather than producing an empty or
    /// inverted rect.
    /// </summary>
    private static DecodedImage CropPixels(DecodedImage source, int x, int y, int width, int height)
    {
        var originX = Math.Clamp(x, 0, Math.Max(0, source.Width - 1));
        var originY = Math.Clamp(y, 0, Math.Max(0, source.Height - 1));
        var cropWidth = Math.Clamp(width, 1, Math.Max(1, source.Width - originX));
        var cropHeight = Math.Clamp(height, 1, Math.Max(1, source.Height - originY));

        var sourceStride = source.Width * 4;
        var stride = cropWidth * 4;
        var pixels = new byte[stride * cropHeight];

        for (var row = 0; row < cropHeight; row++)
        {
            Buffer.BlockCopy(
                source.Pixels,
                (originY + row) * sourceStride + originX * 4,
                pixels,
                row * stride,
                stride);
        }

        return new DecodedImage(pixels, cropWidth, cropHeight);
    }

    protected override void Render(IComObject<ID2D1HwndRenderTarget> renderTarget)
    {
        using var target = renderTarget.AsRenderTarget();
        target.Object.SetDpi(96, 96);

        if (_resources is null)
        {
            _resources = new D2DResources(target);
            using var context = target.AsDeviceContext();
            if (context is not null) _snapshot = ImageSurface.Upload(_snapshotPixels, context);
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

    /// <summary>The live pixel dimensions, pinned just outside the selection so it never covers the
    /// content being selected. This mirrors main's compact accent badge rather than introducing a
    /// second black surface over the screen being captured.</summary>
    private void DrawSizeBadge(Ui ui, Rect selection)
    {
        var label = $"{(int)selection.Width} × {(int)selection.Height}";
        const float font = 12;
        var width = Math.Ceiling(ui.MeasureText(label, font, bold: true, monospace: true)) + 16;
        const double height = 26;

        // Below the selection normally; above it when there is no room below.
        var y = selection.Bottom + 8;
        if (y + height > _desktop.Height) y = Math.Max(0, selection.Top - height - 8);

        var x = Math.Clamp(selection.X, 0, Math.Max(0, _desktop.Width - width));
        var box = new Rect(x, y, width, height);
        ui.FillRect(box, Theme.Dark.Accent.WithAlpha(242));
        ui.Text(label, box, Rgba.White, font, bold: true,
            align: TextAlign.Center, monospace: true);
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

    /// <summary>
    /// Releases the snapshot as soon as the window is destroyed.
    ///
    /// A virtual-desktop snapshot is the largest bitmap the app ever makes - on a multi-monitor
    /// setup it is tens of megabytes on the GPU. Holding it until Dispose runs means it survives
    /// every frame of the editor that opens next, so it goes here instead.
    /// </summary>
    protected override void OnDestroyed(object? sender, EventArgs e)
    {
        ReleaseResources();
        base.OnDestroyed(sender, e);
    }

    private void ReleaseResources()
    {
        _snapshot?.Dispose();
        _resources?.Dispose();
        _snapshot = null;
        _resources = null;
    }

    protected override void Dispose(bool disposing)
    {
        // Idempotent: OnDestroyed already ran if the window closed normally.
        ReleaseResources();
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG message, IntPtr window, uint min, uint max);

    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG message);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);

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
