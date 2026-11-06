using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;
using static NexusShot.Views.MouseMessage;

namespace NexusShot.Views;

/// <summary>What the picker chose, where it is on the desktop.</summary>
public abstract record Picked(RectInt Region);

/// <summary>A still: its pixels, owned by the caller, and whether its text was asked for rather than the image.</summary>
public sealed record PickedImage(DecodedImage Pixels, RectInt Region, bool Text) : Picked(Region);

public sealed record PickedRecording(RectInt Region, RecordingFormat Format) : Picked(Region);

/// <summary>
/// The region picker: a full-desktop window showing a frozen snapshot of the screen, dimmed, with a
/// bright cut-out for what would be taken, and a bar at the top: Photo or Video, then the modes.
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

    private static readonly (CaptureKind Kind, Icon Icon, string Tip)[] Kinds =
        [(CaptureKind.Photo, Icons.Camera, "Screenshot"), (CaptureKind.Video, Icons.Video, "Recording")];

    private static readonly (string Label, TextScope Scope)[] TextScopes =
        [("Area", TextScope.Area), ("Window", TextScope.Window)];

    private static readonly (string Label, RecordingFormat Format)[] Formats =
        [("MP4", RecordingFormat.Mp4), ("GIF", RecordingFormat.Gif)];

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

    /// <summary>The bar's width in its widest kind and mode, measured once, so switching never resizes it.</summary>
    private double? _barWidth;

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
    /// overlay's own activation dismisses any open menu, so a re-capture saves a changed desktop. A
    /// recording takes no pixels from it: it starts from the live screen once the overlay is gone.
    /// </summary>
    public static Picked? Pick(bool includeCursor, Theme theme, RectInt? lastRegion, PickerMode mode, bool showLoupe,
        CaptureKind kind, RecordingFormat format) =>
        Pick(bounds => ScreenCapture.Capture(bounds, includeCursor), theme, lastRegion, mode, showLoupe,
            ScreenCapture.VisibleWindows, Monitors.All, kind, format);

    internal static Picked? Pick(Func<RectInt, DecodedImage> capture, Theme? theme = null,
        RectInt? lastRegion = null, PickerMode mode = PickerMode.Region, bool showLoupe = true,
        Func<List<RectInt>>? windows = null, Func<List<RectInt>>? screens = null,
        CaptureKind kind = CaptureKind.Photo, RecordingFormat format = RecordingFormat.Mp4)
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
                lastRegion is { } last ? ToOverlay(last) : null, mode, kind, format);

            PickResult? result;
            using (var overlay = new RegionOverlay(desktop, snapshot, theme ?? Theme.Dark, picker, showLoupe))
            {
                if (WindowInterop.GetCursorPos(out var start))
                    picker.Move(new Point(start.X - desktop.X, start.Y - desktop.Y));
                ModalLoop.Run(overlay, desktop);
                result = overlay._result;
            }

            if (result is not { Region: var region }) return null;
            var area = new RectInt(desktop.X + (int)region.X, desktop.Y + (int)region.Y, (int)region.Width, (int)region.Height);
            if (result.Recording is { } recording) return new PickedRecording(area, recording);

            // Independent pixels; encoding belongs to the media worker.
            var pixels = snapshot.Crop((int)region.X, (int)region.Y, (int)region.Width, (int)region.Height);
            if (result.Outline is { } outline) pixels.KeepInside(outline);
            return new PickedImage(pixels, area, result.Text);
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
        ui.BeginFrame(target, _picker.Pointer);

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
        if (ui.Animating || ui.ClickedThisFrame) Invalidate();
    }

    /// <summary>The live pixel dimensions, where <see cref="OverlayGeometry.SizeBadge"/> places them.</summary>
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
    /// Photo or Video, then the kind's modes, then the mode's options - Text's scopes or Video's formats -
    /// and what the mode expects. Hidden while dragging, where it would only be in the way.
    /// </summary>
    private void DrawBar(Ui ui)
    {
        var theme = ui.Theme;
        var width = _barWidth ??= Kinds.Max(kind => RegionPicker.ModesFor(kind.Kind)
            .Max(mode => TextScopes.Max(scope => ContentWidth(ui, kind.Kind, mode, scope.Scope))));
        _bar = new Rect(Math.Round(_home.X + (_home.Width - width) / 2), _home.Y + S(16), width, S(40));
        ui.Shadow(_bar, (float)S(Metrics.RadiusLg), S(18), S(6), theme.Shadow);
        ui.FillRounded(_bar, (float)S(Metrics.RadiusLg), theme.SurfacePane);
        ui.StrokeRounded(_bar, (float)S(Metrics.RadiusLg), theme.StrokeDefault);

        var x = _bar.X + S(4);
        var y = _bar.Y + S(5);
        for (var i = 0; i < Kinds.Length; i++)
        {
            var (kind, icon, tip) = Kinds[i];
            if (ui.IconButton(Ui.Id(Ui.Id("picker.kind"), i), new Rect(x, y, S(30), S(30)), icon, tip,
                on: _picker.Kind == kind, iconSize: 16))
                _picker.SetKind(kind);
            x += S(32);
        }
        x = GroupEnd(ui, x);

        foreach (var mode in _picker.Modes)
        {
            var (_, label, icon) = Entry(mode);
            var button = ui.ButtonWidth(label, icon, small: true);
            if (ui.Button(Ui.Id(Ui.Id("picker.mode"), (int)mode), new Rect(x, y, button, S(30)), label,
                _picker.Mode == mode ? ButtonStyle.Primary : ButtonStyle.Ghost, icon, small: true))
                _picker.SetMode(mode);
            x += button + S(2);
        }
        x = GroupEnd(ui, x);

        var video = _picker.Kind == CaptureKind.Video;
        var options = OptionsIn(_picker.Kind, _picker.Mode);
        for (var i = 0; i < options.Length; i++)
        {
            var button = ui.ButtonWidth(options[i], small: true);
            var on = video ? _picker.Format == Formats[i].Format : _picker.TextScope == TextScopes[i].Scope;
            if (ui.Button(Ui.Id(Ui.Id("picker.option"), i), new Rect(x, y, button, S(30)), options[i],
                on ? ButtonStyle.Secondary : ButtonStyle.Ghost, small: true))
            {
                if (video) _picker.SetFormat(Formats[i].Format);
                else _picker.SetTextScope(TextScopes[i].Scope);
            }
            x += button + S(2);
        }
        if (options.Length > 0) x = GroupEnd(ui, x);

        var hint = Hint(_picker.Mode, _picker.TextScope);
        ui.Text(hint, new Rect(x, _bar.Y, Math.Ceiling(ui.MeasureText(hint, S(Metrics.FontSm))) + 1, _bar.Height),
            theme.TextSecondary, S(Metrics.FontSm));
    }

    private double GroupEnd(Ui ui, double x)
    {
        ui.Separator(new Point(x + S(5), _bar.Center.Y));
        return x + S(13);
    }

    private static (PickerMode Mode, string Label, Icon Icon) Entry(PickerMode mode) => Modes.First(entry => entry.Mode == mode);

    private static string[] OptionsIn(CaptureKind kind, PickerMode mode) =>
        kind == CaptureKind.Video ? [.. Formats.Select(format => format.Label)]
        : mode == PickerMode.Text ? [.. TextScopes.Select(scope => scope.Label)]
        : [];

    /// <summary>The bar's width in one state, walked the way <see cref="DrawBar"/> lays it out.</summary>
    private double ContentWidth(Ui ui, CaptureKind kind, PickerMode mode, TextScope scope)
    {
        var options = OptionsIn(kind, mode);
        return S(4) + Kinds.Length * S(32) + S(13)
            + RegionPicker.ModesFor(kind).Sum(each => ui.ButtonWidth(Entry(each).Label, Entry(each).Icon, small: true) + S(2)) + S(13)
            + (options.Length > 0 ? options.Sum(option => ui.ButtonWidth(option, small: true) + S(2)) + S(13) : 0)
            + Math.Ceiling(ui.MeasureText(Hint(mode, scope), S(Metrics.FontSm))) + S(12);
    }

    private string Hint(PickerMode mode, TextScope scope) => mode switch
    {
        PickerMode.Region => "Drag an area or click a window  ·  Shift square  ·  Space move",
        PickerMode.Freeform => "Draw around what you want",
        PickerMode.Window => "Click a window",
        PickerMode.Screen => "Click a screen",
        _ when scope == TextScope.Area => "Drag over the text",
        _ => "Click a window to read its text",
    } + (_picker.HasLastRegion && RegionPicker.DragsIn(mode, scope) ? "  ·  Enter last area" : "") + "  ·  Esc cancel";

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
                _ui?.NotePress(at);
                if (!_bar.Contains(at) || _picker.IsDragging) _picker.Press(at);
                Invalidate();
                return new LRESULT { Value = 0 };

            case WmMouseMove:
                _picker.Move(ClientPoint(lParam), Held(VIRTUAL_KEY.VK_SHIFT), Held(VIRTUAL_KEY.VK_SPACE));
                Invalidate();
                return new LRESULT { Value = 0 };

            case WmLButtonUp:
                var released = ClientPoint(lParam);
                _ui?.NoteRelease(released);
                if (_picker.IsDragging) Finish(_picker.Release(released, Held(VIRTUAL_KEY.VK_SHIFT), Held(VIRTUAL_KEY.VK_SPACE)));
                else Invalidate();
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
