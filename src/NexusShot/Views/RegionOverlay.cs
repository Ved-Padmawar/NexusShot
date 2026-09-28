using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>A picked region: its pixels, owned by the caller, and where it was on the desktop.</summary>
public sealed record PickedRegion(DecodedImage Pixels, RectInt Region);

/// <summary>
/// The region picker: a full-desktop window showing a frozen snapshot of the screen, dimmed, with a
/// bright cut-out that follows the drag, the window under the pointer, or the lasso.
///
/// It draws a *snapshot* rather than being transparent over the live desktop. That is what makes
/// the selection stable - a live overlay has to fight the compositor and can catch its own dimming
/// in the capture. The snapshot is taken before the window appears, so what the user selects is
/// exactly what they get. What the input means is <see cref="RegionPicker"/>'s to decide.
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
    private const uint WmRButtonDown = 0x0204;
    private const uint WmKeyDown = 0x0100;
    private const uint WmSetCursor = 0x0020;

    private static readonly Rgba Dim = Rgba.Black.WithAlpha(110);

    private readonly RectInt _desktop;
    private readonly DecodedImage _snapshotPixels;
    private readonly RegionPicker _picker;

    private D2DResources? _resources;
    private Ui? _ui;
    private ImageSurface? _snapshot;

    /// <summary>The choice in overlay pixels, or null if cancelled.</summary>
    private PickResult? _result;

    /// <summary>The app's theme, for the accents.</summary>
    private readonly Theme _theme;

    private RegionOverlay(RectInt desktop, DecodedImage snapshotPixels, Theme theme, RegionPicker picker)
        : base("NexusShot region",
            (WINDOW_STYLE)WS_POPUP,
            (WINDOW_EX_STYLE)(WS_EX_TOPMOST | WS_EX_TOOLWINDOW))
    {
        _desktop = desktop;
        _snapshotPixels = snapshotPixels;
        _theme = theme;
        _picker = picker;
    }

    private static bool _isPicking;

    /// <summary>
    /// Runs the picker to completion and returns owned pixels, or null if cancelled. Blocking, because
    /// a capture is a modal act: nothing else in the app can meaningfully happen while the user is
    /// choosing what to grab. <paramref name="lastRegion"/> is what Enter repeats.
    ///
    /// The result is cropped from the frozen snapshot, never re-captured from the live screen: the
    /// overlay's own activation dismisses any open menu, so a re-capture saves a changed desktop.
    /// </summary>
    public static PickedRegion? Pick(bool includeCursor, Theme theme, RectInt? lastRegion) =>
        Pick(bounds => ScreenCapture.Capture(bounds, includeCursor), theme, lastRegion, ScreenCapture.VisibleWindows);

    internal static PickedRegion? Pick(Func<RectInt, DecodedImage> capture, Theme? theme = null,
        RectInt? lastRegion = null, Func<List<RectInt>>? windows = null)
    {
        if (_isPicking) return null;
        _isPicking = true;
        try
        {
            var desktop = ScreenCapture.VirtualDesktop;
            Rect ToOverlay(RectInt rect) => new(rect.X - desktop.X, rect.Y - desktop.Y, rect.Width, rect.Height);

            // Listed just before the snapshot, so snapping matches what is shown.
            var snapTargets = (windows?.Invoke() ?? []).Select(ToOverlay).ToList();
            using var snapshot = capture(desktop);
            var picker = new RegionPicker(new Size(desktop.Width, desktop.Height), snapTargets,
                lastRegion is { } last ? ToOverlay(last) : null);

            PickResult? result;
            using (var overlay = new RegionOverlay(desktop, snapshot, theme ?? Theme.Dark, picker))
            {
                if (WindowInterop.GetCursorPos(out var start))
                    picker.Move(new Point(start.X - desktop.X, start.Y - desktop.Y));
                ModalLoop.Run(overlay, desktop);
                result = overlay._result;
            }

            if (result is not { Region: var region }) return null;

            // Independent pixels; encoding belongs to the media worker.
            var pixels = snapshot.Crop((int)region.X, (int)region.Y, (int)region.Width, (int)region.Height);
            if (result.Outline is { } outline) pixels.KeepInside(outline);
            return new PickedRegion(pixels,
                new RectInt(desktop.X + (int)region.X, desktop.Y + (int)region.Y, pixels.Width, pixels.Height));
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
        ui.BeginFrame(target, _picker.Pointer, _picker.IsDragging);

        var full = new Rect(0, 0, _desktop.Width, _desktop.Height);
        var desktop = new Size(_desktop.Width, _desktop.Height);

        // The frozen desktop, then a dim over everything but what would be captured.
        renderTarget.DrawBitmap(
            _snapshot.Bitmap, 1f,
            D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR,
            AnnotationRenderer.ToRect(full));

        if (_picker.Shape == PickerShape.Freeform && _picker.IsDragging && _picker.Selection is { } bounds)
        {
            ui.FillRect(full, Dim);
            ui.PushPathLayer(_picker.Path, full);
            renderTarget.DrawBitmap(_snapshot.Bitmap, 1f,
                D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, AnnotationRenderer.ToRect(full));
            ui.PopLayer();
            var path = _picker.Path;
            for (var i = 1; i < path.Count; i++) ui.Line(path[i - 1], path[i], ui.Theme.Accent, 1.5f);
            DrawSizeBadge(ui, bounds);
        }
        else if ((_picker.Selection ?? _picker.HoveredWindow) is { } selection)
        {
            foreach (var band in AdornerGeometry.DimAround(selection, full.Width, full.Height)) ui.FillRect(band, Dim);
            ui.StrokeRounded(selection, 0, ui.Theme.Accent, _picker.IsDragging ? 1.5f : 2.5f);
            DrawSizeBadge(ui, selection);
        }
        else ui.FillRect(full, Dim);

        var pointer = _picker.Pointer;
        Loupe.Draw(ui, target, _snapshot, pointer, desktop,
            $"{(int)pointer.X + _desktop.X}, {(int)pointer.Y + _desktop.Y}");
        if (!_picker.IsDragging) DrawHints(ui);
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

    /// <summary>The keys, top-centre of the screen being looked at, so the picker's extras are found
    /// without a manual. Gone while dragging, where it would only be in the way.</summary>
    private void DrawHints(Ui ui)
    {
        var screen = Monitors.WorkAreaUnderCursor();
        var mode = _picker.Shape == PickerShape.Freeform ? "Draw around it" : "Drag, or click a window";
        var repeat = _picker.HasLastRegion ? "   Enter last region" : "";
        var label = $"{mode}   Shift square   Space move   Tab {(_picker.Shape == PickerShape.Freeform ? "rectangle" : "freeform")}{repeat}   Esc cancel";
        const float font = 12;
        var width = Math.Ceiling(ui.MeasureText(label, font, Weight.Semibold)) + 28;
        var pill = new Rect(Math.Round(screen.X - _desktop.X + (screen.Width - width) / 2), screen.Y - _desktop.Y + 16, width, 32);
        ui.FillRounded(pill, (float)(pill.Height / 2), ui.Theme.SurfaceRaised.WithAlpha(235));
        ui.StrokeRounded(pill, (float)(pill.Height / 2), ui.Theme.StrokeDefault);
        ui.Text(label, pill, ui.Theme.TextPrimary, font, Weight.Semibold, TextAlign.Center);
    }

    protected override LRESULT? WindowProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case WmSetCursor:
                DirectN.Extensions.Utilities.Cursor.Set(DirectN.Extensions.Utilities.Cursor.Cross);
                return new LRESULT { Value = 1 };

            case WmLButtonDown:
                _picker.Press(ClientPoint(lParam));
                Invalidate();
                return new LRESULT { Value = 0 };

            case WmMouseMove:
                _picker.Move(ClientPoint(lParam), Held(VIRTUAL_KEY.VK_SHIFT), Held(VIRTUAL_KEY.VK_SPACE));
                Invalidate();
                return new LRESULT { Value = 0 };

            case WmLButtonUp when _picker.IsDragging:
                Finish(_picker.Release(ClientPoint(lParam), Held(VIRTUAL_KEY.VK_SHIFT), Held(VIRTUAL_KEY.VK_SPACE)));
                return new LRESULT { Value = 0 };

            case WmKeyDown when (VIRTUAL_KEY)(ulong)wParam.Value == VIRTUAL_KEY.VK_ESCAPE:
            case WmRButtonDown:
                Finish(null);
                return new LRESULT { Value = 0 };

            case WmKeyDown:
                OnKey((VIRTUAL_KEY)(ulong)wParam.Value);
                return new LRESULT { Value = 0 };
        }
        return base.WindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>Tab switches shape, Enter repeats the last region, and the arrows move the pointer
    /// itself a pixel at a time (Shift: ten) - so both ends of a drag can be placed exactly.</summary>
    private void OnKey(VIRTUAL_KEY key)
    {
        switch (key)
        {
            case VIRTUAL_KEY.VK_TAB:
                _picker.ToggleShape();
                Invalidate();
                break;

            case VIRTUAL_KEY.VK_RETURN when _picker.RepeatLast() is { } last:
                Finish(last);
                break;

            case VIRTUAL_KEY.VK_LEFT or VIRTUAL_KEY.VK_RIGHT or VIRTUAL_KEY.VK_UP or VIRTUAL_KEY.VK_DOWN:
                var step = Held(VIRTUAL_KEY.VK_SHIFT) ? 10 : 1;
                var (dx, dy) = key switch
                {
                    VIRTUAL_KEY.VK_LEFT => (-step, 0),
                    VIRTUAL_KEY.VK_RIGHT => (step, 0),
                    VIRTUAL_KEY.VK_UP => (0, -step),
                    _ => (0, step),
                };
                if (WindowInterop.GetCursorPos(out var at)) WindowInterop.SetCursorPos(at.X + dx, at.Y + dy);
                break;
        }
    }

    private void Finish(PickResult? result)
    {
        _result = result;
        Close();
    }

    private static bool Held(VIRTUAL_KEY key) => (Functions.GetKeyState((int)key) & 0x8000) != 0;

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
