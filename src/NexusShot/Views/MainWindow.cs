using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>
/// The shell: a sidebar that browses, a pane that previews and acts.
///
/// The layout is the XAML build's, because that layout was right: a 248px sunken sidebar holding the
/// brand, the capture actions as a labelled list with their shortcuts, then the recent captures; and
/// a detail pane whose preview sits in a rounded sunken well so the capture reads as inset from the
/// chrome. Annotating opens the editor as its own window rather than docking it here - a docked
/// editor would permanently surrender the sidebar's width from the image, on every edit, to a list
/// the user has stopped looking at.
///
/// What is different is underneath. A XAML Image scales whatever bitmap it is handed, so the old
/// detail view either showed a pre-scaled thumbnail (soft - the bug that cost a release) or decoded
/// the full image into the visual tree (heavy). Here one bitmap per capture is uploaded at full
/// resolution and the GPU rescales it per frame, so the row and the preview draw from the same
/// pixels and the preview is exact at any size.
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

    /// <summary>Decoded captures, by path. One bitmap serves both the row and the full-size preview,
    /// which is what makes the preview exact rather than a scaled-up thumbnail.</summary>
    private readonly Dictionary<string, ImageSurface> _bitmaps = [];

    /// <summary>The frame each cached bitmap was last drawn on, so the cache can evict the coldest.</summary>
    private readonly Dictionary<string, long> _lastDrawn = [];
    private long _frame;

    private ScreenshotHistoryItem? _selected;
    private Point _pointer;
    private bool _pointerDown;
    private double _scroll;
    private bool _settingsOpen;

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

    private double _scale = 1;

    /// <summary>Design units to physical pixels. Every metric goes through here.</summary>
    private double S(double units) => units * _scale;

    public void AddCapture(ScreenshotHistoryItem item)
    {
        _history.Insert(0, item);
        _selected = item;
        _settingsOpen = false;
        _scroll = 0;
        _storage.SaveHistory(_history);
        Invalidate();
    }

    /// <summary>Forgets a cached bitmap, so the next frame re-decodes it. Used after an editor saves
    /// over a capture: the file has changed, and the cached pixels are the old ones.</summary>
    public void DropCache(string path)
    {
        if (_bitmaps.Remove(path, out var bitmap)) bitmap.Dispose();
        _lastDrawn.Remove(path);
    }

    // ============================  RENDER  ============================

    protected override void Render(IComObject<ID2D1HwndRenderTarget> renderTarget)
    {
        using var target = renderTarget.AsRenderTarget();
        target.Object.SetDpi(96, 96);

        _resources ??= new D2DResources(target);
        _ui ??= new Ui(_resources);
        _ui.Theme = _settings.Theme == AppTheme.Light ? Theme.Light : Theme.Dark;

        _scale = Functions.GetDpiForWindow(Handle) / 96.0;
        _frame++;

        var client = ClientRect;
        var width = (double)client.Width;
        var height = (double)client.Height;

        renderTarget.Clear(D2DResources.ToD3D(_ui.Theme.SurfaceBase));
        _ui.BeginFrame(target, _pointer, _pointerDown);

        var sidebar = new Rect(0, 0, S(248), height);
        var pane = new Rect(sidebar.Right, 0, width - sidebar.Width, height);

        DrawSidebar(_ui, target, sidebar);

        if (_settingsOpen) DrawSettings(_ui, pane);
        else DrawDetail(_ui, target, pane);

        _ui.EndFrame();
    }

    // ============================  SIDEBAR  ============================

    private void DrawSidebar(Ui ui, IComObject<ID2D1RenderTarget> target, Rect bounds)
    {
        var theme = ui.Theme;
        ui.FillRect(bounds, theme.SurfaceSunken);

        var y = bounds.Y + S(20);

        // Brand: the mark, the name, and a version pill.
        DrawBrandMark(ui, new Rect(bounds.X + S(18), y, S(22), S(22)));
        ui.Text("NexusShot", new Rect(bounds.X + S(50), y, S(110), S(22)),
            theme.TextPrimary, (float)S(Metrics.FontSubtitle), bold: true);

        var pill = new Rect(bounds.X + S(158), y + S(2), S(44), S(18));
        ui.FillRounded(pill, (float)S(9), theme.SurfaceOverlay);
        ui.Text("2.0.0", pill, theme.TextTertiary, (float)S(10), align: TextAlign.Center);

        y += S(38);

        // Capture actions: icon, label, shortcut - the sidebar's primary content.
        y = DrawCaptureAction(ui, bounds, y, 1, ToolIcons.CaptureRegion, "Region", "Ctrl+Shift+S",
            CaptureMode.Region);
        y = DrawCaptureAction(ui, bounds, y, 2, ToolIcons.CaptureScreen, "Full screen", "Ctrl+Shift+F",
            CaptureMode.FullScreen);
        y = DrawCaptureAction(ui, bounds, y, 3, ToolIcons.CaptureWindow, "Active window", "Ctrl+Shift+W",
            CaptureMode.ActiveWindow);

        y += S(14);

        // "RECENT" header, over a hairline.
        ui.FillRect(new Rect(bounds.X, y, bounds.Width, 1), theme.StrokeSubtle);
        ui.Text("RECENT", new Rect(bounds.X + S(20), y + S(10), bounds.Width, S(18)),
            theme.TextTertiary, (float)S(Metrics.FontCaption), bold: true);

        y += S(34);

        var footer = S(48);
        var list = new Rect(bounds.X, y, bounds.Width, bounds.Bottom - y - footer);
        DrawHistory(ui, target, list);

        DrawSidebarFooter(ui, new Rect(bounds.X, bounds.Bottom - footer, bounds.Width, footer));
    }

    /// <summary>The app mark: the Nexus tile, a rounded square split on the diagonal.</summary>
    private void DrawBrandMark(Ui ui, Rect bounds)
    {
        ui.FillRounded(bounds, (float)S(5), new Rgba(0x3A, 0x46, 0x52));

        // The cyan half, as a stack of horizontal spans under the diagonal.
        var steps = Math.Max(1, (int)bounds.Height);
        for (var i = 0; i < steps; i++)
        {
            var t = i / (double)steps;
            var rowY = bounds.Y + t * bounds.Height;
            var span = bounds.Width * (1 - t);
            if (span <= 0) continue;
            ui.FillRect(new Rect(bounds.X, rowY, span, bounds.Height / steps + 0.5),
                new Rgba(0x4F, 0xC3, 0xE8));
        }
    }

    private double DrawCaptureAction(
        Ui ui, Rect sidebar, double y, int id,
        Action<Ui, Rect, Rgba> icon, string label, string shortcut, CaptureMode mode)
    {
        var theme = ui.Theme;
        var row = new Rect(sidebar.X + S(10), y, sidebar.Width - S(20), S(38));

        if (ui.Interact(id, row)) CaptureRequested?.Invoke(mode);

        var fill = ui.IsActive(id) ? theme.FillPressed : ui.IsHot(id) ? theme.FillHover : default;
        if (fill.A > 0) ui.FillRounded(row, (float)S(Metrics.RadiusControl), fill);

        // The icon carries the accent: it is the only colour in an otherwise neutral row.
        icon(ui, new Rect(row.X + S(9), row.Y + S(10), S(18), S(18)), theme.Accent);

        ui.Text(label, new Rect(row.X + S(38), row.Y, row.Width - S(48), row.Height),
            theme.TextPrimary, (float)S(Metrics.FontBody));

        ui.Text(shortcut, new Rect(row.X, row.Y, row.Width - S(10), row.Height),
            theme.TextTertiary, (float)S(Metrics.FontCaption), align: TextAlign.Right);

        return y + S(39);
    }

    private void DrawHistory(Ui ui, IComObject<ID2D1RenderTarget> target, Rect bounds)
    {
        var theme = ui.Theme;

        if (_history.Count == 0)
        {
            ui.Text("Nothing captured yet", bounds, theme.TextTertiary,
                (float)S(Metrics.FontCaption), align: TextAlign.Center);
            return;
        }

        var rowHeight = S(48);
        var gap = S(2);
        var y = bounds.Y - _scroll;

        for (var i = 0; i < _history.Count; i++)
        {
            var item = _history[i];
            var row = new Rect(bounds.X + S(10), y, bounds.Width - S(20), rowHeight);
            y += rowHeight + gap;

            // Cull, and clip to the list: a long history must not paint over the footer.
            if (row.Bottom < bounds.Y || row.Y > bounds.Bottom) continue;

            var id = 1000 + i;
            var selected = ReferenceEquals(item, _selected);
            if (ui.Interact(id, row))
            {
                _selected = item;
                _settingsOpen = false;
            }

            // Selection is an elevated neutral pill, not a tint: it sits behind the thumbnail, so a
            // coloured fill would cast onto the capture.
            if (selected)
            {
                ui.FillRounded(row, (float)S(Metrics.RadiusControl), theme.RowSelectFill);
                ui.StrokeRounded(row, (float)S(Metrics.RadiusControl), theme.RowSelectStroke);
            }
            else if (ui.IsActive(id))
                ui.FillRounded(row, (float)S(Metrics.RadiusControl), theme.RowPressedFill);
            else if (ui.IsHot(id))
                ui.FillRounded(row, (float)S(Metrics.RadiusControl), theme.RowHoverFill);

            // Thumbnail: 52x34, filling its slot, on an overlay backing so a transparent PNG reads.
            var slot = new Rect(row.X + S(8), row.Y + S(7), S(52), S(34));
            ui.FillRounded(slot, (float)S(4), theme.SurfaceOverlay);
            DrawThumbnail(ui, target, item, slot);

            var textX = slot.Right + S(10);
            var textWidth = row.Right - textX - S(8);

            ui.Text(Truncate(item.FileName, 22),
                new Rect(textX, row.Y + S(7), textWidth, S(18)),
                theme.TextPrimary, (float)S(Metrics.FontBody), middle: false);

            ui.Text($"{item.Width}×{item.Height}  ·  {Ago(item.CapturedAt)}",
                new Rect(textX, row.Y + S(26), textWidth, S(16)),
                theme.TextTertiary, (float)S(Metrics.FontCaption), middle: false);
        }
    }

    private void DrawSidebarFooter(Ui ui, Rect bounds)
    {
        var theme = ui.Theme;
        ui.FillRect(new Rect(bounds.X, bounds.Y, bounds.Width, 1), theme.StrokeSubtle);

        var size = S(32);
        var y = bounds.Y + (bounds.Height - size) / 2;

        if (ui.Tile(30, new Rect(bounds.X + S(12), y, size, size), false,
            ToolIcons.ThemeToggle, "Switch theme"))
        {
            _settings.Theme = _settings.Theme == AppTheme.Light ? AppTheme.Dark : AppTheme.Light;
            _storage.SaveSettings(_settings);
        }

        if (ui.Tile(31, new Rect(bounds.Right - S(12) - size, y, size, size), _settingsOpen,
            ToolIcons.Settings, "Settings"))
        {
            _settingsOpen = !_settingsOpen;
        }
    }

    // ============================  DETAIL PANE  ============================

    private void DrawDetail(Ui ui, IComObject<ID2D1RenderTarget> target, Rect bounds)
    {
        var theme = ui.Theme;

        if (_selected is not { } item)
        {
            DrawEmptyState(ui, bounds);
            return;
        }

        var bar = S(64);

        // The preview well: sunken and rounded, so the capture reads as inset from the chrome.
        // The top margin clears the caption buttons floating over this pane's top-right.
        var well = new Rect(
            bounds.X + S(24),
            bounds.Y + S(48),
            bounds.Width - S(48),
            bounds.Height - S(48) - bar - S(12));

        ui.FillRounded(well, (float)S(Metrics.RadiusContainer), theme.SurfaceSunken);
        ui.StrokeRounded(well, (float)S(Metrics.RadiusContainer), theme.StrokeSubtle);

        var bitmap = GetBitmap(target, item);
        if (bitmap is null)
        {
            ui.Text("Could not open this capture", well, theme.TextTertiary,
                (float)S(Metrics.FontBody), align: TextAlign.Center);
        }
        else
        {
            // Inset from the well, then fill it: the image floats inside the frame rather than
            // touching it, but a small capture still uses the space it was given.
            var fit = Fit(bitmap.Width, bitmap.Height, well.Deflate(S(20)), enlarge: true);
            target.DrawBitmap(
                bitmap.Bitmap, 1f,
                D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR,
                AnnotationRenderer.ToRect(fit));
        }

        DrawDetailBar(ui, item, new Rect(bounds.X, well.Bottom, bounds.Width, bar));
    }

    private void DrawDetailBar(Ui ui, ScreenshotHistoryItem item, Rect bounds)
    {
        var theme = ui.Theme;

        ui.Text(Truncate(item.FileName, 42),
            new Rect(bounds.X + S(24), bounds.Y + S(12), bounds.Width * 0.5, S(20)),
            theme.TextPrimary, (float)S(Metrics.FontSubtitle), bold: true, middle: false);

        ui.Text($"{item.Width} × {item.Height}   ·   {item.CapturedAt.LocalDateTime:d MMM yyyy, HH:mm}",
            new Rect(bounds.X + S(24), bounds.Y + S(34), bounds.Width * 0.5, S(16)),
            theme.TextTertiary, (float)S(Metrics.FontCaption), middle: false);

        // Actions, right-aligned. Edit is the accent: it is what this pane is for.
        var y = bounds.Y + (bounds.Height - S(32)) / 2;
        var right = bounds.Right - S(24);

        right -= S(86);
        if (ui.Button(23, new Rect(right, y, S(86), S(32)), "Edit", primary: true))
            EditRequested?.Invoke(item);

        right -= S(94);
        if (ui.Button(22, new Rect(right, y, S(86), S(32)), "Copy"))
            ClipboardImage.Copy(item.FilePath);

        right -= S(40);
        if (ui.Tile(21, new Rect(right, y, S(32), S(32)), false, ToolIcons.Delete, "Remove"))
            Delete(item);

        right -= S(40);
        if (ui.Tile(20, new Rect(right, y, S(32), S(32)), false, ToolIcons.Reveal, "Show in Explorer"))
            Reveal(item.FilePath);
    }

    private void DrawEmptyState(Ui ui, Rect bounds)
    {
        var theme = ui.Theme;
        var centre = bounds.Center;

        ToolIcons.EmptyState(
            ui, new Rect(centre.X - S(24), centre.Y - S(58), S(48), S(48)), theme.TextTertiary);

        ui.Text("No captures yet",
            new Rect(bounds.X, centre.Y - S(6), bounds.Width, S(24)),
            theme.TextSecondary, (float)S(Metrics.FontSubtitle), align: TextAlign.Center);

        ui.Text("Press Ctrl + Shift + S to capture a region",
            new Rect(bounds.X, centre.Y + S(20), bounds.Width, S(20)),
            theme.TextTertiary, (float)S(Metrics.FontBody), align: TextAlign.Center);
    }

    // ============================  SETTINGS  ============================

    /// <summary>
    /// Settings replace the detail pane in place rather than opening a dialog: every change applies
    /// immediately, so there is nothing to confirm or cancel.
    /// </summary>
    private void DrawSettings(Ui ui, Rect bounds)
    {
        var theme = ui.Theme;

        // Header, over a hairline. The right inset clears the caption buttons.
        var header = new Rect(bounds.X, bounds.Y, bounds.Width, S(64));
        ui.Text("Settings", new Rect(header.X + S(28), header.Y, header.Width, header.Height),
            theme.TextPrimary, (float)S(Metrics.FontTitle), bold: true);
        ui.FillRect(new Rect(bounds.X, header.Bottom, bounds.Width, 1), theme.StrokeSubtle);

        // A capped, centred column: rows fill it, and it centres itself in the pane.
        var columnWidth = Math.Min(S(600), bounds.Width - S(64));
        var x = bounds.X + (bounds.Width - columnWidth) / 2;
        var y = header.Bottom + S(24);

        y = Section(ui, "CAPTURE", x, y, columnWidth);

        y = Toggle(ui, 40, "Copy to clipboard automatically",
            "Every capture lands on the clipboard as well as on disk.",
            x, y, columnWidth, _settings.CopyToClipboardAutomatically,
            value => _settings.CopyToClipboardAutomatically = value);

        y = Toggle(ui, 41, "Save automatically",
            $"Captures are written to {_settings.ScreenshotFolder}.",
            x, y, columnWidth, _settings.SaveAutomatically,
            value => _settings.SaveAutomatically = value);

        y = Section(ui, "GENERAL", x, y, columnWidth);

        y = Toggle(ui, 42, "Start with Windows",
            "NexusShot launches into the notification area at sign-in.",
            x, y, columnWidth, _settings.StartWithWindows,
            value =>
            {
                _settings.StartWithWindows = value;
                Startup.Set(value);
            });

        _ = y;
    }

    private double Section(Ui ui, string title, double x, double y, double width)
    {
        ui.Text(title, new Rect(x, y, width, S(20)),
            ui.Theme.TextTertiary, (float)S(Metrics.FontCaption), bold: true, middle: false);
        return y + S(26);
    }

    /// <summary>A settings row: title, caption, and a switch, over a hairline.</summary>
    private double Toggle(
        Ui ui, int id, string title, string caption,
        double x, double y, double width, bool value, Action<bool> set)
    {
        var theme = ui.Theme;
        var height = S(56);
        var row = new Rect(x, y, width, height);

        if (ui.Interact(id, row)) { set(!value); _storage.SaveSettings(_settings); }

        ui.Text(title, new Rect(x, y + S(9), width - S(60), S(18)),
            theme.TextPrimary, (float)S(Metrics.FontBody), middle: false);

        ui.Text(caption, new Rect(x, y + S(28), width - S(60), S(16)),
            theme.TextTertiary, (float)S(Metrics.FontCaption), middle: false);

        // The switch: a track with a knob that slides, filled with the accent when on.
        var track = new Rect(row.Right - S(40), row.Center.Y - S(10), S(40), S(20));
        ui.FillRounded(track, (float)S(10), value ? theme.Accent : theme.StrokeDefault);

        var knob = new Point(
            value ? track.Right - S(10) : track.X + S(10),
            track.Center.Y);
        ui.FillCircle(knob, (float)S(8), Rgba.White);

        ui.FillRect(new Rect(x, row.Bottom, width, 1), theme.StrokeSubtle);
        return row.Bottom + S(1);
    }

    // ============================  DATA  ============================

    private void DrawThumbnail(
        Ui ui, IComObject<ID2D1RenderTarget> target, ScreenshotHistoryItem item, Rect slot)
    {
        var bitmap = GetBitmap(target, item);
        if (bitmap is null) return;

        // Aspect-fill, clipped to the chip. A letterboxed thumbnail in a 52x34 cell is mostly empty
        // background; filling it makes the row scannable, which is the whole job of a thumbnail.
        var fit = Cover(bitmap.Width, bitmap.Height, slot);

        target.Object.PushAxisAlignedClip(
            AnnotationRenderer.ToRect(slot), D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_ALIASED);
        target.DrawBitmap(
            bitmap.Bitmap, 1f,
            D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR,
            AnnotationRenderer.ToRect(fit));
        target.Object.PopAxisAlignedClip();
    }

    private ImageSurface? GetBitmap(IComObject<ID2D1RenderTarget> target, ScreenshotHistoryItem item)
    {
        if (_bitmaps.TryGetValue(item.FilePath, out var cached))
        {
            _lastDrawn[item.FilePath] = _frame;
            return cached;
        }

        using var context = target.AsDeviceContext();
        if (context is null || !File.Exists(item.FilePath)) return null;

        try
        {
            var surface = ImageSurface.Load(item.FilePath, context);
            _bitmaps[item.FilePath] = surface;
            _lastDrawn[item.FilePath] = _frame;
            Evict();
            return surface;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Drops the bitmaps that have gone longest without being drawn.
    ///
    /// A GPU bitmap for a 4K capture is ~33 MB. An unbounded cache would pin one per capture for the
    /// life of the process, which is a leak that only shows up after a long session.
    /// </summary>
    private void Evict()
    {
        const int limit = 12;
        while (_bitmaps.Count > limit)
        {
            var coldest = _lastDrawn
                .Where(entry => _selected is null || entry.Key != _selected.FilePath)
                .OrderBy(entry => entry.Value)
                .Select(entry => entry.Key)
                .FirstOrDefault();

            if (coldest is null) break;
            if (_bitmaps.Remove(coldest, out var surface)) surface.Dispose();
            _lastDrawn.Remove(coldest);
        }
    }

    private void Delete(ScreenshotHistoryItem item)
    {
        _history.Remove(item);
        if (ReferenceEquals(_selected, item)) _selected = _history.FirstOrDefault();

        DropCache(item.FilePath);

        try { File.Delete(item.FilePath); }
        catch (IOException) { /* the file may be open elsewhere; the history entry still goes */ }

        _storage.SaveHistory(_history);
        Invalidate();
    }

    private static void Reveal(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is IOException
            or System.ComponentModel.Win32Exception)
        {
            // Explorer not opening is not worth taking the app down for.
        }
    }

    /// <summary>
    /// Aspect-preserving fit, centred.
    ///
    /// <paramref name="enlarge"/> lets a small capture grow to fill its container. The preview well
    /// wants that - a 600x350 capture pinned at 1:1 in a 1400px pane is a postage stamp adrift in
    /// empty space. It costs nothing in sharpness here because the GPU is upscaling the real bitmap,
    /// not a pre-scaled thumbnail; the editor is where 1:1 actually matters, and it opts out.
    /// </summary>
    private static Rect Fit(double imageWidth, double imageHeight, Rect bounds, bool enlarge = false)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || bounds.IsEmpty) return bounds;

        var scale = Math.Min(bounds.Width / imageWidth, bounds.Height / imageHeight);
        if (!enlarge) scale = Math.Min(1, scale);

        var width = imageWidth * scale;
        var height = imageHeight * scale;

        return new Rect(
            Math.Round(bounds.X + (bounds.Width - width) / 2),
            Math.Round(bounds.Y + (bounds.Height - height) / 2),
            Math.Round(width),
            Math.Round(height));
    }

    /// <summary>Aspect-preserving *fill*: covers the bounds entirely, overflowing on one axis. The
    /// caller clips. This is `Stretch="UniformToFill"`.</summary>
    private static Rect Cover(double imageWidth, double imageHeight, Rect bounds)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || bounds.IsEmpty) return bounds;

        var scale = Math.Max(bounds.Width / imageWidth, bounds.Height / imageHeight);
        var width = imageWidth * scale;
        var height = imageHeight * scale;

        return new Rect(
            bounds.X + (bounds.Width - width) / 2,
            bounds.Y + (bounds.Height - height) / 2,
            width,
            height);
    }

    private static string Truncate(string text, int limit) =>
        text.Length <= limit ? text : text[..(limit - 1)] + "…";

    private static string Ago(DateTimeOffset when)
    {
        var elapsed = DateTimeOffset.Now - when;
        if (elapsed.TotalMinutes < 1) return "just now";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours}h ago";
        if (elapsed.TotalDays < 7) return $"{(int)elapsed.TotalDays}d ago";
        return when.LocalDateTime.ToString("d MMM");
    }

    // ============================  INPUT  ============================

    protected override LRESULT? WindowProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
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
                var content = _history.Count * S(50);
                var viewport = Math.Max(1, ClientRect.Height - S(300));
                var maximum = Math.Max(0, content - viewport);

                _scroll = Math.Clamp(_scroll - delta / 120.0 * S(50), 0, maximum);
                Invalidate();
                return new LRESULT { Value = 0 };
            }

            case WmKeyDown:
                if ((VIRTUAL_KEY)(ulong)wParam.Value == VIRTUAL_KEY.VK_ESCAPE)
                {
                    if (_settingsOpen) { _settingsOpen = false; Invalidate(); }
                    else Hide();
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
        _lastDrawn.Clear();
        _resources?.Dispose();
        base.Dispose(disposing);
    }
}
