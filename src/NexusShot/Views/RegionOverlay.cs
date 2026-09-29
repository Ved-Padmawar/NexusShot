using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>A picked region: its pixels, owned by the caller, where it was on the desktop, and
/// whether its text was asked for rather than the image.</summary>
public sealed record PickedRegion(DecodedImage Pixels, RectInt Region, bool Text);

/// <summary>
/// The region picker: a full-desktop window showing a frozen snapshot of the screen, dimmed, with a
/// bright cut-out for what would be taken, and a bar of modes at the top.
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

    /// <summary>A hovered window in a mode that also drags: lit a little, a hint rather than a choice.</summary>
    private static readonly Rgba HintDim = Rgba.Black.WithAlpha(45);

    private static readonly (PickerMode Mode, string Label, Icon Icon)[] Modes =
    [
        (PickerMode.Region, "Region", Icons.CaptureRegion),
        (PickerMode.Freeform, "Freeform", Icons.CaptureFreeform),
        (PickerMode.Window, "Window", Icons.CaptureWindow),
        (PickerMode.Screen, "Screen", Icons.CaptureScreen),
        (PickerMode.Text, "Text", Icons.Ocr),
    ];

    private readonly RectInt _desktop;
    private readonly DecodedImage _snapshotPixels;
    private readonly RegionPicker _picker;
    private readonly bool _showLoupe;

    /// <summary>The monitor the picker opened on, in overlay pixels, and its scale: the bar stays
    /// there, sized for that screen, wherever the pointer goes.</summary>
    private readonly Rect _home;
    private readonly double _scale;

    private D2DResources? _resources;
    private Ui? _ui;
    private ImageSurface? _snapshot;

    /// <summary>Where the bar was last drawn, so a press on it is not taken as the start of a drag.</summary>
    private Rect _bar;
    private bool _pressedOnBar;

    /// <summary>The choice in overlay pixels, or null if cancelled.</summary>
    private PickResult? _result;

    /// <summary>The app's theme, for the accents.</summary>
    private readonly Theme _theme;

    private RegionOverlay(RectInt desktop, DecodedImage snapshotPixels, Theme theme, RegionPicker picker, bool showLoupe)
        : base("NexusShot region",
            (WINDOW_STYLE)WS_POPUP,
            (WINDOW_EX_STYLE)(WS_EX_TOPMOST | WS_EX_TOOLWINDOW))
    {
        _desktop = desktop;
        _snapshotPixels = snapshotPixels;
        _theme = theme;
        _picker = picker;
        _showLoupe = showLoupe;
        var home = Monitors.WorkAreaUnderCursor();
        _home = new Rect(home.X - desktop.X, home.Y - desktop.Y, home.Width, home.Height);
        _scale = Monitors.DpiScaleUnderCursor(Handle);
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
    public static PickedRegion? Pick(bool includeCursor, Theme theme, RectInt? lastRegion, PickerMode mode, bool showLoupe) =>
        Pick(bounds => ScreenCapture.Capture(bounds, includeCursor), theme, lastRegion, mode, showLoupe,
            ScreenCapture.VisibleWindows, Monitors.All);

    internal static PickedRegion? Pick(Func<RectInt, DecodedImage> capture, Theme? theme = null,
        RectInt? lastRegion = null, PickerMode mode = PickerMode.Region, bool showLoupe = true,
        Func<List<RectInt>>? windows = null, Func<List<RectInt>>? screens = null)
    {
        if (_isPicking) return null;
        _isPicking = true;
        try
        {
            var desktop = ScreenCapture.VirtualDesktop;
            Rect ToOverlay(RectInt rect) => new(rect.X - desktop.X, rect.Y - desktop.Y, rect.Width, rect.Height);

            // Listed just before the snapshot, so snapping matches what is shown.
            var snapTargets = (windows?.Invoke() ?? []).Select(ToOverlay).ToList();
            var monitors = (screens?.Invoke() ?? []).Select(ToOverlay).ToList();
            using var snapshot = capture(desktop);
            var picker = new RegionPicker(new Size(desktop.Width, desktop.Height), snapTargets, monitors,
                lastRegion is { } last ? ToOverlay(last) : null, mode);

            PickResult? result;
            using (var overlay = new RegionOverlay(desktop, snapshot, theme ?? Theme.Dark, picker, showLoupe))
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
                new RectInt(desktop.X + (int)region.X, desktop.Y + (int)region.Y, pixels.Width, pixels.Height), result.Text);
        }
        finally
        {
            _isPicking = false;
        }
    }

    private double S(double value) => value * _scale;

    protected override void Render(IComObject<ID2D1HwndRenderTarget> renderTarget)
    {
        using var target = renderTarget.AsRenderTarget();
        target.Object.SetDpi(96, 96);

        if (_resources is null)
        {
            _resources = new D2DResources(target);
            _ui = new Ui(_resources) { Theme = _theme, Scale = _scale };
            using var context = target.AsDeviceContext();
            if (context is not null) _snapshot = ImageSurface.Upload(_snapshotPixels, context);
        }

        if (_snapshot is null || _ui is null) return;
        var ui = _ui;
        ui.BeginFrame(target, _picker.Pointer, _pressedOnBar || _picker.IsDragging);

        var full = new Rect(0, 0, _desktop.Width, _desktop.Height);

        // The frozen desktop, then a dim over everything but what would be taken.
        renderTarget.DrawBitmap(
            _snapshot.Bitmap, 1f,
            D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR,
            AnnotationRenderer.ToRect(full));

        if (_picker.Mode == PickerMode.Freeform && _picker.IsDragging && _picker.Selection is { } bounds)
        {
            ui.FillRect(full, Dim);
            ui.PushPathLayer(_picker.Path, full);
            renderTarget.DrawBitmap(_snapshot.Bitmap, 1f,
                D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, AnnotationRenderer.ToRect(full));
            ui.PopLayer();
            var path = _picker.Path;
            for (var i = 1; i < path.Count; i++) ui.Line(path[i - 1], path[i], ui.Theme.Accent, (float)S(1.5));
            DrawSizeBadge(ui, bounds);
        }
        else if (_picker.Selection is { } selection)
        {
            foreach (var band in AdornerGeometry.DimAround(selection, full.Width, full.Height)) ui.FillRect(band, Dim);
            ui.StrokeRounded(selection, 0, ui.Theme.Accent, (float)S(1.5));
            DrawSizeBadge(ui, selection);
        }
        else if (_picker.Target is { } shown && _picker.Drags)
        {
            foreach (var band in AdornerGeometry.DimAround(shown, full.Width, full.Height)) ui.FillRect(band, Dim);
            ui.FillRect(shown, HintDim);
            ui.StrokeRounded(shown, 0, ui.Theme.Accent.WithAlpha(150), (float)S(1));
        }
        else if (_picker.Target is { } chosen)
        {
            foreach (var band in AdornerGeometry.DimAround(chosen, full.Width, full.Height)) ui.FillRect(band, Dim);
            ui.StrokeRounded(chosen, 0, ui.Theme.Accent, (float)S(2.5));
            DrawSizeBadge(ui, chosen);
        }
        else ui.FillRect(full, Dim);

        var pointer = _picker.Pointer;
        if (_showLoupe && !_bar.Contains(pointer))
            Loupe.Draw(ui, target, _snapshot, pointer, new Size(_desktop.Width, _desktop.Height),
                $"{(int)pointer.X + _desktop.X}, {(int)pointer.Y + _desktop.Y}");
        if (!_picker.IsDragging) DrawBar(ui);
        ui.EndFrame();
        if (ui.Animating) Invalidate();
    }

    /// <summary>The live pixel dimensions, pinned just outside the selection so it never covers the
    /// content being selected.</summary>
    private void DrawSizeBadge(Ui ui, Rect selection)
    {
        var label = $"{(int)selection.Width} × {(int)selection.Height}";
        var font = S(Metrics.FontSm);
        var width = Math.Ceiling(ui.MeasureText(label, font, Weight.Bold, Face.Mono)) + S(16);
        var height = S(26);

        var origin = OverlayGeometry.SizeBadge(
            selection, new Size(width, height), new Size(_desktop.Width, _desktop.Height), gap: S(8));
        var box = new Rect(origin.X, origin.Y, width, height);
        ui.FillRounded(box, (float)S(Metrics.RadiusSm), ui.Theme.Accent);
        ui.Text(label, box, ui.Theme.TextOnAccent, font, Weight.Bold, TextAlign.Center, face: Face.Mono);
    }

    /// <summary>
    /// The modes, as the Library header offers them, with what the current one expects beside them.
    /// Text adds its two scopes. Hidden while dragging, where it would only be in the way.
    /// </summary>
    private void DrawBar(Ui ui)
    {
        var theme = ui.Theme;
        var widths = Modes.Select(mode => ui.ButtonWidth(mode.Label, mode.Icon, small: true)).ToArray();
        var scopes = _picker.Mode == PickerMode.Text
            ? new[] { ("Area", TextScope.Area), ("Window", TextScope.Window) } : [];
        var scopeWidths = scopes.Select(scope => ui.ButtonWidth(scope.Item1, small: true)).ToArray();
        var hint = Hint();
        var hintWidth = Math.Ceiling(ui.MeasureText(hint, S(Metrics.FontSm)));

        var width = S(6) + widths.Sum() + S(2) * (widths.Length - 1)
            + (scopes.Length > 0 ? S(17) + scopeWidths.Sum() + S(2) : 0)
            + S(17) + hintWidth + S(14);
        _bar = new Rect(Math.Round(_home.X + (_home.Width - width) / 2), _home.Y + S(16), width, S(40));
        ui.Shadow(_bar, (float)S(Metrics.RadiusLg), S(18), S(6), theme.Shadow);
        ui.FillRounded(_bar, (float)S(Metrics.RadiusLg), theme.SurfacePane);
        ui.StrokeRounded(_bar, (float)S(Metrics.RadiusLg), theme.StrokeDefault);

        var x = _bar.X + S(4);
        var y = _bar.Y + S(5);
        for (var i = 0; i < Modes.Length; i++)
        {
            var (mode, label, icon) = Modes[i];
            if (ui.Button(Ui.Id(Ui.Id("picker.mode"), i), new Rect(x, y, widths[i], S(30)), label,
                _picker.Mode == mode ? ButtonStyle.Primary : ButtonStyle.Ghost, icon, small: true))
                _picker.SetMode(mode);
            x += widths[i] + S(2);
        }

        if (scopes.Length > 0)
        {
            ui.Separator(new Point(x + S(6), _bar.Center.Y));
            x += S(15);
            for (var i = 0; i < scopes.Length; i++)
            {
                var (label, scope) = scopes[i];
                if (ui.Button(Ui.Id(Ui.Id("picker.scope"), i), new Rect(x, y, scopeWidths[i], S(30)), label,
                    _picker.TextScope == scope ? ButtonStyle.Secondary : ButtonStyle.Ghost, small: true))
                    _picker.SetTextScope(scope);
                x += scopeWidths[i] + S(2);
            }
        }

        ui.Separator(new Point(x + S(6), _bar.Center.Y));
        x += S(15);
        ui.Text(hint, new Rect(x, _bar.Y, hintWidth + 1, _bar.Height), theme.TextSecondary, S(Metrics.FontSm));
    }

    private string Hint() => _picker.Mode switch
    {
        PickerMode.Region => "Drag an area or click a window  ·  Shift square  ·  Space move",
        PickerMode.Freeform => "Draw around what you want",
        PickerMode.Window => "Click a window",
        PickerMode.Screen => "Click a screen",
        _ when _picker.TextScope == TextScope.Area => "Drag over the text",
        _ => "Click a window to read its text",
    } + (_picker.HasLastRegion && _picker.Drags ? "  ·  Enter last area" : "") + "  ·  Esc cancel";

    protected override LRESULT? WindowProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case WmSetCursor:
                DirectN.Extensions.Utilities.Cursor.Set(_bar.Contains(_picker.Pointer)
                    ? DirectN.Extensions.Utilities.Cursor.Hand
                    : DirectN.Extensions.Utilities.Cursor.Cross);
                return new LRESULT { Value = 1 };

            case WmLButtonDown:
                var at = ClientPoint(lParam);
                if (_bar.Contains(at) && !_picker.IsDragging) _pressedOnBar = true;
                else _picker.Press(at);
                Invalidate();
                return new LRESULT { Value = 0 };

            case WmMouseMove:
                _picker.Move(ClientPoint(lParam), Held(VIRTUAL_KEY.VK_SHIFT), Held(VIRTUAL_KEY.VK_SPACE));
                Invalidate();
                return new LRESULT { Value = 0 };

            case WmLButtonUp when _pressedOnBar:
                _pressedOnBar = false;
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

    /// <summary>Tab steps through the modes, Enter repeats the last area, and the arrows move the
    /// pointer itself a pixel at a time (Shift: ten) - so both ends of a drag can be placed exactly.</summary>
    private void OnKey(VIRTUAL_KEY key)
    {
        switch (key)
        {
            case VIRTUAL_KEY.VK_TAB:
                _picker.NextMode();
                Invalidate();
                break;

            case VIRTUAL_KEY.VK_RETURN when _picker.Drags && _picker.RepeatLast() is { } last:
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
