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
public sealed partial class RegionOverlay : D2DRenderWindow
{
    protected override void CreateRenderTarget()
    {
        RenderTarget?.Dispose();
        RenderTarget = null;
        RenderTarget = GraphicsBackend.CreateWindowTarget(Handle, ClientRect.Size.ToD2D_SIZE_U(), FactoryType, FactoryOptions,
            software: true);
    }
    protected override DirectN.Extensions.Utilities.Icon? LoadCreationIcon() => null;
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
    private Ui? _ui;
    private ImageSurface? _snapshot;

    private Point _origin;
    private Point _cursor;
    private bool _dragging;
    private bool _hasSelection;

    /// <summary>The chosen region in desktop coordinates, or null if cancelled.</summary>
    public RectInt? Selection { get; private set; }

    /// <summary>The app's theme, for the size badge's accent.</summary>
    private readonly Theme _theme;

    public RegionOverlay(RectInt desktop, DecodedImage snapshotPixels, Theme theme)
        : base("NexusShot region",
            (WINDOW_STYLE)WS_POPUP,
            (WINDOW_EX_STYLE)(WS_EX_TOPMOST | WS_EX_TOOLWINDOW))
    {
        _desktop = desktop;
        _snapshotPixels = snapshotPixels;
        _theme = theme;
    }

    /// <summary>
    /// Runs the picker to completion and returns owned cropped pixels, or null if
    /// cancelled. Blocking, because a capture is a modal act: nothing else in the app can
    /// meaningfully happen while the user is choosing what to grab.
    ///
    /// The result is cropped from the frozen snapshot, never re-captured from the live screen: the
    /// overlay's own activation dismisses any open menu, so a re-capture saves a changed desktop.
    /// </summary>
    private static bool _isPicking;

    public static DecodedImage? Pick(bool includeCursor, Theme theme) =>
        Pick(bounds => ScreenCapture.Capture(bounds, includeCursor), theme);

    internal static DecodedImage? Pick(Func<RectInt, DecodedImage> capture, Theme? theme = null)
    {
        if (_isPicking) return null;
        _isPicking = true;
        try
        {
            var desktop = ScreenCapture.VirtualDesktop;
            using var snapshot = capture(desktop);
            RectInt? selection;
            using (var overlay = new RegionOverlay(desktop, snapshot, theme ?? Theme.Dark))
            {
                ModalLoop.Run(overlay, desktop);
                selection = overlay.Selection;
            }

            if (selection is not { } region) return null;

            // Return independent cropped pixels; encoding belongs to the media worker.
            return snapshot.Crop(
                region.X - desktop.X, region.Y - desktop.Y, region.Width, region.Height);
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

        if (_resources is null)
        {
            _resources = new D2DResources(target);
            _ui = new Ui(_resources) { Theme = _theme };
            using var context = target.AsDeviceContext();
            if (context is not null) _snapshot = ImageSurface.Upload(_snapshotPixels, context);
        }

        if (_snapshot is null || _ui is null) return;
        var ui = _ui;
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
        foreach (var band in AdornerGeometry.DimAround(selection, full.Width, full.Height)) ui.FillRect(band, Rgba.Black.WithAlpha(110));

        ui.StrokeRounded(selection, 0, ui.Theme.Accent, 1.5f);
        DrawSizeBadge(ui, selection);
        ui.EndFrame();
    }

    /// <summary>The live pixel dimensions, pinned just outside the selection so it never covers the
    /// content being selected.</summary>
    private void DrawSizeBadge(Ui ui, Rect selection)
    {
        var label = $"{(int)selection.Width} × {(int)selection.Height}";
        const float font = 12;
        var width = Math.Ceiling(ui.MeasureText(label, font, Weight.Bold, Face.Mono)) + 16;
        const double height = 26;

        var origin = OverlayGeometry.SizeBadge(
            selection, new Size(width, height), new Size(_desktop.Width, _desktop.Height), gap: 8);
        var box = new Rect(origin.X, origin.Y, width, height);
        ui.FillRounded(box, Metrics.RadiusSm, ui.Theme.Accent);
        ui.Text(label, box, ui.Theme.TextOnAccent, font, Weight.Bold, TextAlign.Center, face: Face.Mono);
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

    private void Commit()
    {
        Selection = OverlayGeometry.Selection(_origin, _cursor) is { } region
            ? new RectInt(_desktop.X + (int)region.X, _desktop.Y + (int)region.Y, (int)region.Width, (int)region.Height)
            : null;
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
        _ui = null;
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
}
