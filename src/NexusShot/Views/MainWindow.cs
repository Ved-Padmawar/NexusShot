using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>
/// The shell: capture actions, the history of what has been captured, and a detail preview.
///
/// The XAML build's blurry preview was structural. A XAML Image scales whatever bitmap it is given,
/// so the detail view either showed a pre-scaled thumbnail (soft) or decoded the full image into
/// the visual tree (heavy). Here there is one bitmap per capture, uploaded once at full resolution,
/// and the GPU rescales it per frame - so the grid and the preview draw from the same pixels and
/// the preview is exact at any size. The thumbnail optimisation that worked in the old build (cache
/// the decode, not the display) survives as the bitmap cache.
/// </summary>
public sealed class MainWindow : D2DRenderWindow
{
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmKeyDown = 0x0100;
    private const uint WmMouseWheel = 0x020A;
    private const uint WmClose = 0x0010;

    private readonly Storage _storage;
    private readonly AppSettings _settings;
    private readonly List<ScreenshotHistoryItem> _history;

    private D2DResources? _resources;
    private Ui? _ui;

    /// <summary>Decoded captures, by path. A capture is decoded once and drawn many times, at
    /// whatever size - which is what makes both the grid and the preview sharp.</summary>
    private readonly Dictionary<string, ImageSurface> _bitmaps = [];

    private ScreenshotHistoryItem? _selected;
    private Point _pointer;
    private bool _pointerDown;
    private double _scroll;

    /// <summary>Raised when the user asks for a capture. The host owns the capture pipeline; the
    /// window only reports intent.</summary>
    public event Action<CaptureMode>? CaptureRequested;
    public event Action<ScreenshotHistoryItem>? EditRequested;

    /// <summary>
    /// Lets the app see raw messages first. The tray icon and the global hotkeys both post to this
    /// window's handle - it is the app's message pump - so the app needs a way in without the window
    /// having to know what a tray or a hotkey is. Returning true marks the message handled.
    /// </summary>
    public Func<uint, long, long, bool>? MessageIntercept { get; set; }

    public MainWindow(Storage storage, AppSettings settings, List<ScreenshotHistoryItem> history)
        : base("NexusShot")
    {
        _storage = storage;
        _settings = settings;
        _history = history;
        _selected = history.FirstOrDefault();
    }

    private double Scale => Functions.GetDpiForWindow(Handle) / 96.0;
    private static double S(double units, double scale) => units * scale;

    /// <summary>Adds a capture to the top of the history and selects it.</summary>
    public void AddCapture(ScreenshotHistoryItem item)
    {
        _history.Insert(0, item);
        _selected = item;
        _storage.SaveHistory(_history);
        Invalidate();
    }

    protected override void Render(IComObject<ID2D1HwndRenderTarget> renderTarget)
    {
        using var target = renderTarget.AsRenderTarget();
        target.Object.SetDpi(96, 96);

        _resources ??= new D2DResources(target);
        _ui ??= new Ui(_resources);
        _ui.Theme = _settings.Theme == AppTheme.Light ? Theme.Light : Theme.Dark;

        var theme = _ui.Theme;
        var scale = Scale;
        var client = ClientRect;
        var width = (double)client.Width;
        var height = (double)client.Height;

        renderTarget.Clear(D2DResources.ToD3D(theme.SurfaceBase));
        _ui.BeginFrame(target, _pointer, _pointerDown);

        DrawToolbar(_ui, width, scale);

        var sidebarWidth = S(300, scale);
        var top = S(64, scale);

        DrawHistory(_ui, target, new Rect(0, top, sidebarWidth, height - top), scale);
        DrawPreview(_ui, target,
            new Rect(sidebarWidth, top, width - sidebarWidth, height - top), scale);

        _ui.EndFrame();
    }

    private void DrawToolbar(Ui ui, double width, double scale)
    {
        var height = S(64, scale);
        ui.FillRect(new Rect(0, 0, width, height), ui.Theme.SurfaceRaised);
        ui.FillRect(new Rect(0, height - 1, width, 1), ui.Theme.StrokeSubtle);

        var y = (height - S(32, scale)) / 2;
        var x = S(16, scale);

        ui.Text("NexusShot", new Rect(x, 0, S(120, scale), height),
            ui.Theme.TextPrimary, (float)S(Metrics.FontSubtitle, scale), bold: true);
        x += S(130, scale);

        if (ui.Button(1, new Rect(x, y, S(110, scale), S(32, scale)), "Region", primary: true))
            CaptureRequested?.Invoke(CaptureMode.Region);
        x += S(118, scale);

        if (ui.Button(2, new Rect(x, y, S(120, scale), S(32, scale)), "Full screen"))
            CaptureRequested?.Invoke(CaptureMode.FullScreen);
        x += S(128, scale);

        if (ui.Button(3, new Rect(x, y, S(120, scale), S(32, scale)), "Window"))
            CaptureRequested?.Invoke(CaptureMode.ActiveWindow);

        // Theme toggle, right-aligned.
        var right = width - S(16, scale) - S(90, scale);
        if (ui.Button(4, new Rect(right, y, S(90, scale), S(32, scale)),
            _settings.Theme == AppTheme.Light ? "Dark" : "Light"))
        {
            _settings.Theme = _settings.Theme == AppTheme.Light ? AppTheme.Dark : AppTheme.Light;
            _storage.SaveSettings(_settings);
        }
    }

    /// <summary>The capture list. Each row draws its own thumbnail from the cached full bitmap.</summary>
    private void DrawHistory(Ui ui, IComObject<ID2D1RenderTarget> target, Rect bounds, double scale)
    {
        ui.FillRect(bounds, ui.Theme.SurfaceBase);
        ui.FillRect(new Rect(bounds.Right - 1, bounds.Y, 1, bounds.Height), ui.Theme.StrokeSubtle);

        if (_history.Count == 0)
        {
            ui.Text("No captures yet", bounds, ui.Theme.TextTertiary,
                (float)S(Metrics.FontBody, scale), align: TextAlign.Center);
            return;
        }

        var rowHeight = S(76, scale);
        var padding = S(10, scale);
        var y = bounds.Y + padding - _scroll;

        for (var i = 0; i < _history.Count; i++)
        {
            var item = _history[i];
            var row = new Rect(bounds.X + padding, y, bounds.Width - padding * 2, rowHeight);
            y += rowHeight + S(6, scale);

            // Cull: a long history must not cost anything for the rows nobody can see.
            if (row.Bottom < bounds.Y || row.Y > bounds.Bottom) continue;

            var id = 1000 + i;
            var selected = ReferenceEquals(item, _selected);
            var clicked = ui.Interact(id, row);

            // Selection is an elevated neutral pill, not a tint - it sits behind the thumbnail, so a
            // coloured fill would cast onto the capture.
            if (selected)
            {
                ui.FillRounded(row, (float)S(Metrics.RadiusContainer, scale), ui.Theme.RowSelectFill);
                ui.StrokeRounded(row, (float)S(Metrics.RadiusContainer, scale), ui.Theme.RowSelectStroke);
            }
            else if (ui.IsActive(id))
                ui.FillRounded(row, (float)S(Metrics.RadiusContainer, scale), ui.Theme.RowPressedFill);
            else if (ui.IsHot(id))
                ui.FillRounded(row, (float)S(Metrics.RadiusContainer, scale), ui.Theme.RowHoverFill);

            DrawThumbnail(ui, target, item,
                new Rect(row.X + S(8, scale), row.Y + S(8, scale), S(96, scale), rowHeight - S(16, scale)));

            var textX = row.X + S(114, scale);
            var textWidth = row.Right - textX - S(10, scale);

            ui.Text(item.FileName, new Rect(textX, row.Y + S(14, scale), textWidth, S(20, scale)),
                ui.Theme.TextPrimary, (float)S(Metrics.FontBody, scale));

            ui.Text($"{item.Width}×{item.Height}   ·   {Ago(item.CapturedAt)}",
                new Rect(textX, row.Y + S(38, scale), textWidth, S(18, scale)),
                ui.Theme.TextTertiary, (float)S(Metrics.FontCaption, scale));

            if (clicked)
            {
                _selected = item;
                Invalidate();
            }
        }
    }

    private void DrawThumbnail(
        Ui ui, IComObject<ID2D1RenderTarget> target, ScreenshotHistoryItem item, Rect bounds)
    {
        var bitmap = GetBitmap(target, item);
        if (bitmap is null)
        {
            ui.FillRounded(bounds, (float)S(4, Scale), ui.Theme.SurfaceSunken);
            return;
        }

        // Aspect-fit inside the cell: a stretched thumbnail misrepresents what was captured.
        var fit = Fit(bitmap.Width, bitmap.Height, bounds);

        target.DrawBitmap(
            bitmap.Bitmap, 1f,
            D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR,
            AnnotationRenderer.ToRect(fit));

        ui.StrokeRounded(fit, 2, ui.Theme.StrokeSubtle);
    }

    /// <summary>
    /// The full-resolution detail view.
    ///
    /// It draws the same cached bitmap the thumbnail does, scaled by the GPU to whatever size the
    /// pane happens to be. There is no second, smaller copy to go soft - which is the bug the last
    /// release of the XAML build was spent on.
    /// </summary>
    private void DrawPreview(
        Ui ui, IComObject<ID2D1RenderTarget> target, Rect bounds, double scale)
    {
        ui.FillRect(bounds, ui.Theme.SurfaceSunken);

        if (_selected is not { } item)
        {
            ui.Text("Select a capture", bounds, ui.Theme.TextTertiary,
                (float)S(Metrics.FontBody, scale), align: TextAlign.Center);
            return;
        }

        var bitmap = GetBitmap(target, item);
        if (bitmap is null)
        {
            ui.Text("Could not open this capture", bounds, ui.Theme.TextTertiary,
                (float)S(Metrics.FontBody, scale), align: TextAlign.Center);
            return;
        }

        var actions = S(56, scale);
        var well = new Rect(
            bounds.X + S(24, scale),
            bounds.Y + S(24, scale),
            bounds.Width - S(48, scale),
            bounds.Height - S(48, scale) - actions);

        var fit = Fit(bitmap.Width, bitmap.Height, well);
        target.DrawBitmap(
            bitmap.Bitmap, 1f,
            D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR,
            AnnotationRenderer.ToRect(fit));
        ui.StrokeRounded(fit, 2, ui.Theme.StrokeSubtle);

        // Actions under the preview.
        var y = well.Bottom + S(14, scale);
        var x = bounds.Center.X - S(180, scale);

        if (ui.Button(20, new Rect(x, y, S(110, scale), S(32, scale)), "Edit", primary: true))
            EditRequested?.Invoke(item);
        x += S(120, scale);

        if (ui.Button(21, new Rect(x, y, S(110, scale), S(32, scale)), "Copy"))
            ClipboardImage.Copy(item.FilePath);
        x += S(120, scale);

        if (ui.Button(22, new Rect(x, y, S(110, scale), S(32, scale)), "Delete"))
            Delete(item);
    }

    private void Delete(ScreenshotHistoryItem item)
    {
        _history.Remove(item);
        if (ReferenceEquals(_selected, item)) _selected = _history.FirstOrDefault();

        if (_bitmaps.Remove(item.FilePath, out var bitmap)) bitmap.Dispose();

        try { File.Delete(item.FilePath); }
        catch (IOException) { /* the file may be open elsewhere; the history entry still goes */ }

        _storage.SaveHistory(_history);
        Invalidate();
    }

    /// <summary>Decodes a capture once and keeps it. This is the thumbnail optimisation from the
    /// XAML build, except that here one bitmap serves both the grid and the full-size preview.</summary>
    private ImageSurface? GetBitmap(IComObject<ID2D1RenderTarget> target, ScreenshotHistoryItem item)
    {
        if (_bitmaps.TryGetValue(item.FilePath, out var cached)) return cached;

        using var context = target.AsDeviceContext();
        if (context is null || !File.Exists(item.FilePath)) return null;

        try
        {
            var surface = ImageSurface.Load(item.FilePath, context);
            _bitmaps[item.FilePath] = surface;
            return surface;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Aspect-preserving fit, centred, never enlarged past 1:1.</summary>
    private static Rect Fit(double imageWidth, double imageHeight, Rect bounds)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || bounds.IsEmpty) return bounds;

        var scale = Math.Min(1, Math.Min(bounds.Width / imageWidth, bounds.Height / imageHeight));
        var width = imageWidth * scale;
        var height = imageHeight * scale;

        return new Rect(
            Math.Round(bounds.X + (bounds.Width - width) / 2),
            Math.Round(bounds.Y + (bounds.Height - height) / 2),
            width,
            height);
    }

    private static string Ago(DateTimeOffset when)
    {
        var elapsed = DateTimeOffset.Now - when;
        if (elapsed.TotalMinutes < 1) return "just now";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours}h ago";
        if (elapsed.TotalDays < 7) return $"{(int)elapsed.TotalDays}d ago";
        return when.LocalDateTime.ToString("d MMM");
    }

    protected override LRESULT? WindowProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        // The app sees tray and hotkey messages first; they arrive here because this window's handle
        // is what they were registered against.
        if (MessageIntercept is { } intercept
            && intercept(msg, (long)wParam.Value, lParam.Value.ToInt64()))
            return new LRESULT { Value = 0 };

        switch (msg)
        {
            case WmLButtonDown:
                _pointer = ClientPoint(lParam);
                _pointerDown = true;
                Invalidate();
                return new LRESULT { Value = 0 };

            case WmMouseMove:
                _pointer = ClientPoint(lParam);
                Invalidate();
                return new LRESULT { Value = 0 };

            case WmLButtonUp:
                _pointer = ClientPoint(lParam);
                _pointerDown = false;
                Invalidate();
                return new LRESULT { Value = 0 };

            case WmMouseWheel:
            {
                var delta = (short)((wParam.Value.ToUInt64() >> 16) & 0xFFFF);
                _scroll = Math.Max(0, _scroll - delta / 120.0 * 60 * Scale);
                Invalidate();
                return new LRESULT { Value = 0 };
            }

            case WmKeyDown:
                if ((VIRTUAL_KEY)(ulong)wParam.Value == VIRTUAL_KEY.VK_ESCAPE)
                {
                    Hide();
                    return new LRESULT { Value = 0 };
                }
                break;

            case WmClose:
                // A capture tool lives in the tray: closing the window hides it rather than exiting,
                // or the hotkeys would die with it.
                Hide();
                return new LRESULT { Value = 0 };
        }
        return base.WindowProc(hwnd, msg, wParam, lParam);
    }

    private static Point ClientPoint(LPARAM lParam)
    {
        var value = lParam.Value.ToInt64();
        return new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
    }

    protected override void Dispose(bool disposing)
    {
        foreach (var bitmap in _bitmaps.Values) bitmap.Dispose();
        _bitmaps.Clear();
        _resources?.Dispose();
        base.Dispose(disposing);
    }
}
