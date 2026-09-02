using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>
/// The shell: a sidebar that browses, a pane that previews and acts. Annotating opens the editor as
/// its own window rather than docking it here, so the sidebar's width is never taken from the image.
/// </summary>
public sealed class MainWindow : CaptionWindow
{
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmKeyDown = 0x0100;
    private const uint WmChar = 0x0102;
    private const uint WmMouseWheel = 0x020A;

    /// <summary>One wheel notch. Touchpads report fractions of it, scrolled as they arrive -
    /// quantizing them to notches makes a smooth drag land as jumps.</summary>
    private const double WheelDelta = 120;
    private const uint WmClose = 0x0010;

    private readonly Storage _storage;
    private readonly AppSettings _settings;
    private readonly List<ScreenshotHistoryItem> _history;

    private D2DResources? _resources;
    private Ui? _ui;

    /// <summary>Thumbnail-sized decodes. Bounded, or the cache grows with the history forever; the
    /// cap is well above a screenful, so scrolling never evicts a row it is about to draw again.</summary>
    private readonly LruCache<string, ImageSurface> _thumbnails = new(200);

    /// <summary>The selected capture, decoded to physical display pixels rather than source size.</summary>
    private ImageSurface? _preview;
    private string? _previewPath;
    private (int Width, int Height) _previewSize;

    private ScreenshotHistoryItem? _selected;
    private Point _pointer;
    private bool _pointerDown;
    private double _scroll;

    private bool _settingsOpen;
    private double _settingsScroll;
    private double _settingsHeight;

    /// <summary>The scrollable body's height, measured as it is drawn, so the wheel handler does not
    /// have to guess where the header ends.</summary>
    private double _settingsViewport = 1;

    /// <summary>The hotkey row that is armed, if any. The next key press becomes its binding.</summary>
    private int? _recordingHotkey;
    private string? _hotkeyWarning;

    /// <summary>Raised when the bindings change, so the app can re-register them.</summary>
    public event Action? HotkeysChanged;

    /// <summary>Raised when a row is armed or disarmed, so the app can suspend the global hotkeys
    /// while a key is being recorded.</summary>
    public event Action<bool>? RecordingChanged;

    /// <summary>Raised when any setting changes, so the app can react (the watcher follows the save
    /// folder, for one).</summary>
    public event Action? SettingsChanged;

    /// <summary>
    /// Runs work on the UI thread.
    ///
    /// The folder watcher fires on a thread pool thread, and mutating the history from there would
    /// be doing it underneath a frame that is drawing it.
    /// </summary>
    public void Post(Action work) => _dispatch.Post(work);

    private UiThreadDispatch _dispatch = null!;

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

        // Nothing selected on open: even a scaled PNG decode belongs after the first frame.
    }

    protected override void OnCreated(object? sender, EventArgs e)
    {
        base.OnCreated(sender, e);

        _dispatch = new UiThreadDispatch(Handle);

        // Alt+Tab and the taskbar get the icon; the caption itself shows neither icon nor title,
        // because the sidebar carries the brand and the caption is now our own pixels.
        AppIcon.ApplyLargeOnly(Handle);
        AppIcon.ClearCaption(Handle);
        ApplyTheme();
    }

    /// <summary>The window paints its own caption, so only the frame's dark-mode flag still matters -
    /// it drives the shadow and the border DWM draws around us.</summary>
    private void ApplyTheme()
    {
        SystemTheme.ApplyFrame(Handle, SystemTheme.Resolve(_settings.Theme));
        Invalidate();
        ThemeChanged?.Invoke();
    }

    /// <summary>The whole top strip drags, except where the caption buttons are.</summary>
    protected override bool IsDragRegion(Point client) =>
        client.X < ClientRect.Width - CaptionButtonsWidth;

    /// <summary>Raised when the theme moves, so open editors retheme with the shell.</summary>
    public event Action? ThemeChanged;

    /// <summary>The single write-back for settings: persist, retheme, and tell the app.</summary>
    private void SaveSettings()
    {
        _storage.SaveSettings(_settings);
        ApplyTheme();
        SettingsChanged?.Invoke();
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

    /// <summary>Forgets a capture's cached bitmaps, so the next frame re-decodes them. Used after an
    /// editor saves over a capture: the file has changed, and the cached pixels are the old ones.</summary>
    public void DropCache(string path)
    {
        _decodes.Invalidate(path);
        _previewDecodes.Invalidate(path);
        if (_thumbnails.Remove(path, out var thumbnail)) thumbnail?.Dispose();
        if (_decoded.TryRemove(path, out var stale)) stale.Dispose();

        if (_pendingPreview?.Path == path) DropPendingPreview();

        if (_previewPath != path) return;
        _preview?.Dispose();
        _preview = null;
        _previewPath = null;
    }

    // ============================  RENDER  ============================

    // The tray still needs this HWND, but not a D3D device and swap chain. The base class eagerly
    // creates them on WM_CREATE; defer that work until the first visible paint instead.
#pragma warning disable CS8774 // Intentionally lazy; RenderCore establishes the base class's target invariant.
    protected override void CreateRenderTarget() { }
#pragma warning restore CS8774

    protected override bool RenderCore()
    {
        if (!WindowInterop.IsWindowVisible(Handle)) return true;
        if (RenderTarget is null) base.CreateRenderTarget();
        return base.RenderCore();
    }

    /// <summary>Decides which worker decodes may still be used. See <see cref="DecodeCache"/>.</summary>
    private readonly DecodeCache _decodes = new();
    // The same path can have a thumbnail and a detail decode in flight simultaneously.
    // Their completion/failure state must not release or poison one another's requests.
    private readonly DecodeCache _previewDecodes = new();

    private void ReleaseVisuals()
    {
        _decodes.InvalidateAll();
        _previewDecodes.InvalidateAll();
        DropPendingPreview();
        foreach (var pixels in _decoded.Values) pixels.Dispose();
        _decoded.Clear();
        foreach (var thumbnail in _thumbnails.Values) thumbnail.Dispose();
        _thumbnails.Clear();
        _preview?.Dispose();
        _preview = null;
        _previewPath = null;
        _resources?.Dispose();
        _resources = null;
        _ui = null;
        RenderTarget?.Dispose();
        RenderTarget = null;
    }

    protected override void Render(IComObject<ID2D1HwndRenderTarget> renderTarget)
    {
        using var target = renderTarget.AsRenderTarget();
        target.Object.SetDpi(96, 96);

        _resources ??= new D2DResources(target);
        _ui ??= new Ui(_resources);
        _ui.Theme = SystemTheme.Resolve(_settings.Theme);

        _scale = DpiScale;
        _ui.Scale = _scale;

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

        // Last, so the buttons float over the app's own pixels rather than under them.
        DrawCaptionButtons(_ui, width);

        _ui.EndFrame();

        if (_ui.ClickedThisFrame) Invalidate();
    }

    // ============================  SIDEBAR  ============================

    private void DrawSidebar(Ui ui, IComObject<ID2D1RenderTarget> target, Rect bounds)
    {
        var theme = ui.Theme;
        ui.FillRect(bounds, theme.SurfaceSunken);

        // The brand sits at the top of the rail: the window controls are over on the right, so
        // nothing here has to clear them.
        var y = bounds.Y + S(16);

        DrawBrandMark(ui, new Rect(bounds.X + S(18), y, S(22), S(22)));
        ui.Text("NexusShot", new Rect(bounds.X + S(50), y, S(110), S(22)),
            theme.TextPrimary, (float)S(Metrics.FontSubtitle), bold: true);

        // The pill hangs off the rail's trailing edge, mirroring the mark's inset on the left, and is
        // sized to its text so a two-digit version does not overflow it.
        var pillFont = S(10);
        var pillWidth = Math.Max(S(40), ui.MeasureText(AppVersion, pillFont) + S(16));
        var pill = new Rect(bounds.Right - S(18) - pillWidth, y + S(2), pillWidth, S(18));

        ui.FillRounded(pill, (float)S(9), theme.SurfaceOverlay);
        ui.Text(AppVersion, pill, theme.TextTertiary, (float)pillFont, align: TextAlign.Center);

        y += S(38);

        y = DrawCaptureAction(ui, bounds, y, 1, Icons.CaptureRegion, "Region",
            Hint(_settings.CaptureRegionHotkey), CaptureMode.Region);
        y = DrawCaptureAction(ui, bounds, y, 2, Icons.CaptureScreen, "Full screen",
            Hint(_settings.CaptureFullScreenHotkey), CaptureMode.FullScreen);
        y = DrawCaptureAction(ui, bounds, y, 3, Icons.CaptureWindow, "Active window",
            Hint(_settings.CaptureActiveWindowHotkey), CaptureMode.ActiveWindow);

        y += S(14);

        ui.FillRect(new Rect(bounds.X, y, bounds.Width, 1), theme.StrokeSubtle);
        ui.Text("RECENT", new Rect(bounds.X + S(20), y + S(10), bounds.Width, S(18)),
            theme.TextTertiary, (float)S(Metrics.FontCaption), bold: true);

        y += S(34);

        var footer = S(48);
        var list = new Rect(bounds.X, y, bounds.Width, bounds.Bottom - y - footer);
        DrawHistory(ui, target, list);

        DrawSidebarFooter(ui, new Rect(bounds.X, bounds.Bottom - footer, bounds.Width, footer));
    }

    /// <summary>The version stamped onto the assembly at build time (<c>-p:Version</c>). Read rather
    /// than hardcoded, so a tagged release cannot ship a badge that disagrees with it.</summary>
    private static readonly string AppVersion = FormatVersion();

    private static string FormatVersion()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? string.Empty : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>The mark's points in client pixels, and the bounds they were laid out for.</summary>
    private sealed record BrandMarkPoints(
        Rect Bounds, float Radius, float Thickness,
        Point[] Diagonal, Point[] UpperMark, Point[] LowerMark);

    private BrandMarkPoints? _brandMark;

    /// <summary>The app mark, drawn rather than loaded: a slate tile split by a 135° diagonal, and
    /// two crop marks, on the 960-unit grid of the icon source.
    ///
    /// The point arrays are laid out once per size: the mark is redrawn every frame, and only a
    /// resize or a DPI change moves any of it.</summary>
    private void DrawBrandMark(Ui ui, Rect bounds)
    {
        if (_brandMark is not { } mark || mark.Bounds != bounds)
        {
            const double unit = 960;
            var k = bounds.Width / unit;

            double X(double u) => bounds.X + u * k;
            double Y(double u) => bounds.Y + u * k;

            mark = new BrandMarkPoints(
                bounds,
                Radius: (float)(220 * k),
                // Crop marks: corners on the diagonal, so each arm crosses both halves.
                Thickness: (float)(84 * k),
                Diagonal: [new Point(X(0), Y(0)), new Point(X(unit), Y(0)), new Point(X(0), Y(unit))],
                UpperMark: [new Point(X(268), Y(488)), new Point(X(268), Y(268)), new Point(X(488), Y(268))],
                LowerMark: [new Point(X(472), Y(692)), new Point(X(692), Y(692)), new Point(X(692), Y(472))]);

            _brandMark = mark;
        }

        // The slate tile, then the half above the 135° diagonal in cyan - intersected with the tile,
        // so it inherits the rounded corners rather than overhanging them.
        ui.FillRounded(bounds, mark.Radius, Tile);
        ui.FillRoundedRegion(bounds, mark.Radius, mark.Diagonal, Cyan);

        ui.Polyline(mark.UpperMark, Marks, mark.Thickness);
        ui.Polyline(mark.LowerMark, Marks, mark.Thickness);
    }

    private static readonly Rgba Tile = new(0x3A, 0x46, 0x52, 0xFF);
    private static readonly Rgba Cyan = new(0x46, 0xBA, 0xE3, 0xFF);
    private static readonly Rgba Marks = new(0x18, 0x22, 0x2B, 0xFF);

    private double DrawCaptureAction(
        Ui ui, Rect sidebar, double y, int id,
        string glyph, string label, string shortcut, CaptureMode mode)
    {
        var theme = ui.Theme;
        var row = new Rect(sidebar.X + S(10), y, sidebar.Width - S(20), S(38));

        // Posted: this runs inside Render, and capture hides the window and spins the overlay's own
        // message loop.
        if (ui.Interact(id, row)) Post(() => CaptureRequested?.Invoke(mode));

        var fill = ui.IsActive(id) ? theme.FillPressed : ui.IsHot(id) ? theme.FillHover : default;
        if (fill.A > 0) ui.FillRounded(row, (float)S(Metrics.RadiusControl), fill);

        // The icon carries the accent: it is the only colour in an otherwise neutral row.
        ui.Icon(glyph, new Rect(row.X + S(8), row.Y, S(20), row.Height), theme.Accent, S(15));

        ui.Text(label, new Rect(row.X + S(38), row.Y, row.Width - S(48), row.Height),
            theme.TextPrimary, (float)S(Metrics.FontBody));

        if (shortcut.Length != 0)
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

        // Clipped, not just culled: a row straddling an edge draws in full, and would paint over the
        // RECENT rule above and the footer's border below. The clip takes the pointer with it.
        ui.PushClip(bounds);

        for (var i = 0; i < _history.Count; i++)
        {
            var item = _history[i];
            var row = new Rect(bounds.X + S(10), y, bounds.Width - S(20), rowHeight);
            y += rowHeight + gap;

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

        _historyViewport = Math.Max(1, bounds.Height);
        _historyHeight = _history.Count * (rowHeight + gap);

        ui.Scrollbar(bounds, _historyHeight, _scroll);
        ui.PopClip();
    }

    /// <summary>The list's content and visible heights, measured as it is drawn, so the wheel
    /// handler scrolls against the list that exists rather than an estimate of it.</summary>
    private double _historyHeight;
    private double _historyViewport = 1;

    private void DrawSidebarFooter(Ui ui, Rect bounds)
    {
        var theme = ui.Theme;
        ui.FillRect(new Rect(bounds.X, bounds.Y, bounds.Width, 1), theme.StrokeSubtle);

        var size = S(32);
        var y = bounds.Y + (bounds.Height - size) / 2;

        // The toggle flips light and dark. "System" is a deliberate choice, made in Settings - a
        // button that cycles through three states leaves you guessing which one you are in.
        if (ui.Tile(30, new Rect(bounds.X + S(12), y, size, size), false,
            Icons.Theme, S(15), "Switch theme"))
        {
            _settings.Theme = SystemTheme.Resolve(_settings.Theme).IsDark
                ? AppTheme.Light
                : AppTheme.Dark;
            SaveSettings();
        }

        if (ui.Tile(31, new Rect(bounds.Right - S(12) - size, y, size, size), _settingsOpen,
            Icons.Settings, S(15), "Settings", neutral: true))
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

        var bitmap = GetPreviewBitmap(target, item, well);
        if (bitmap is null)
        {
            ui.Text(_previewLoading ? "Loading capture…" : "Could not open this capture", well, theme.TextTertiary,
                (float)S(Metrics.FontBody), align: TextAlign.Center);
        }
        else
        {
            // Inset from the well, then fill it: the image floats inside the frame rather than
            // touching it, but a small capture still uses the space it was given.
            var fit = well.Deflate(S(20)).Fit(new Size(bitmap.Width, bitmap.Height), enlarge: true);
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

        // Actions, right-aligned. Buttons hug their content rather than being fixed-width blocks.
        var y = bounds.Y + (bounds.Height - S(32)) / 2;
        var right = bounds.Right - S(24);

        var font = S(Metrics.FontBody);
        var glyph = S(14);

        // Edit carries the accent: it is what this pane is for.
        var edit = ui.ButtonWidth("Edit", font, glyph);
        right -= edit;
        if (ui.Button(23, new Rect(right, y, edit, S(32)), "Edit",
            primary: true, glyph: Icons.Edit, glyphSize: glyph, fontSize: font))
            Post(() => EditRequested?.Invoke(item));

        var copy = ui.ButtonWidth("Copy", font, glyph);
        right -= copy + S(8);
        if (ui.Button(22, new Rect(right, y, copy, S(32)), "Copy",
            glyph: Icons.Copy, glyphSize: glyph, fontSize: font))
            ClipboardImage.Copy(item.FilePath);

        // The icon-only actions sit in a taller box with a larger glyph: at 14px they read as
        // afterthoughts next to the labelled buttons they share a row with.
        var icon = S(36);
        var iconGlyph = S(17);
        var iconY = bounds.Y + (bounds.Height - icon) / 2;

        right -= icon + S(6);
        if (ui.Tile(21, new Rect(right, iconY, icon, icon), false, Icons.Delete, iconGlyph, "Remove"))
            Post(() => Delete(item));

        right -= icon + S(4);
        if (ui.Tile(20, new Rect(right, iconY, icon, icon), false, Icons.Reveal, iconGlyph,
            "Show in Explorer"))
            Reveal(item.FilePath);

        // Close sits apart from the pair that act on the file: it only dismisses the view.
        right -= icon + S(14);
        if (ui.Tile(24, new Rect(right, iconY, icon, icon), false, Icons.Close, iconGlyph,
            "Close  (Esc)"))
            Deselect();
    }

    /// <summary>The pane with nothing shown. What it says depends on whether the history is empty:
    /// "no captures yet" in front of a list of them would be wrong.</summary>
    private void DrawEmptyState(Ui ui, Rect bounds)
    {
        var theme = ui.Theme;
        var centre = bounds.Center;
        var empty = _history.Count == 0;

        ui.Icon(Icons.EmptyState,
            new Rect(bounds.X, centre.Y - S(66), bounds.Width, S(48)),
            theme.TextTertiary, S(38));

        ui.Text(empty ? "No captures yet" : "Nothing selected",
            new Rect(bounds.X, centre.Y - S(6), bounds.Width, S(24)),
            theme.TextSecondary, (float)S(Metrics.FontSubtitle), align: TextAlign.Center);

        ui.Text(
            empty
                ? "Press Ctrl + Shift + S to capture a region"
                : "Pick a capture from the list, or press Ctrl + Shift + S for a new one",
            new Rect(bounds.X, centre.Y + S(20), bounds.Width, S(20)),
            theme.TextTertiary, (float)S(Metrics.FontBody), align: TextAlign.Center);
    }

    // ============================  SETTINGS  ============================

    /// <summary>
    /// Settings replace the detail pane in place rather than opening a dialog: every change applies
    /// immediately, so there is nothing to confirm or cancel. Rows sit directly in the column with
    /// hairline separators doing the grouping, rather than being boxed into nested cards.
    /// </summary>
    private void DrawSettings(Ui ui, Rect bounds)
    {
        var theme = ui.Theme;

        // The header starts below the caption, so its title and close button clear the window
        // controls floating over this pane's top-right rather than crowding them.
        var header = new Rect(
            bounds.X,
            bounds.Y + CaptionHeight,
            bounds.Width,
            S(14) * 2 + S(Metrics.FontTitle) + S(4));

        ui.Text("Settings", new Rect(header.X + S(28), header.Y, header.Width, header.Height),
            theme.TextPrimary, (float)S(Metrics.FontTitle), bold: true);

        if (ui.Tile(60, new Rect(header.Right - S(28) - S(36), header.Center.Y - S(18), S(36), S(36)),
            false, Icons.Close, S(14), "Close settings"))
        {
            // The click that got here already committed any focused box, on the way in.
            _settingsOpen = false;
            CloseDropdowns();
        }
        ui.FillRect(new Rect(bounds.X, header.Bottom, bounds.Width, 1), theme.StrokeSubtle);

        // The scrollable body. Clipped, so a long list cannot paint over the header.
        var body = new Rect(bounds.X, header.Bottom, bounds.Width, bounds.Bottom - header.Bottom);
        _settingsViewport = Math.Max(1, body.Height);
        ui.PushClip(body);

        // Centred column: MaxWidth=640, Margin="32,0,32,32". Sections and rows provide their
        // own vertical rhythm; keeping it here instead of at each call site makes the whole pane
        // feel like one system rather than a stack of individually nudged controls.
        var width = Math.Min(S(640), body.Width - S(64));
        var x = body.X + (body.Width - width) / 2;

        // Settled before anything is positioned: clamping after the layout would draw one frame at
        // the bad offset and snap back on the next.
        _settingsScroll = Math.Clamp(
            _settingsScroll, 0, Math.Max(0, _settingsHeight - _settingsViewport));

        // Laid out from a fixed origin and shifted by the scroll, so the extent measured below is a
        // property of the content alone - an extent that moved with the scroll position would feed
        // back into the next frame's layout.
        var top = body.Y - _settingsScroll;
        var y = top;

        y = Section(ui, "CAPTURE", x, y, width);

        y = Row(ui, x, y, width, "Save folder", Shorten(_settings.ScreenshotFolder, 52),
            row =>
            {
                if (!ui.Button(40, ActionSlot(row, S(92)), "Change…",
                    fontSize: S(Metrics.FontCaption))) return;

                // The picker is modal and would pump its loop mid-frame.
                Post(() =>
                {
                    if (FolderPicker.Pick(Handle, _settings.ScreenshotFolder) is not { } folder)
                        return;

                    _settings.ScreenshotFolder = folder;
                    SaveSettings();
                    Invalidate();
                });
            });

        y = Row(ui, x, y, width, "Default capture mode", null,
            row => _captureModeBox.Field(ui, 41, ActionSlot(row, S(148)),
                ["Region", "Full screen", "Active window"],
                (int)_settings.DefaultCaptureMode,
                index =>
                {
                    _settings.DefaultCaptureMode = (CaptureMode)index;
                    SaveSettings();
                }));

        y = Row(ui, x, y, width,
            "Copy to clipboard automatically",
            "Every capture lands on the clipboard, ready to paste.",
            row => Switch(ui, 42, ActionSlot(row, S(44)), _settings.CopyToClipboardAutomatically,
                value =>
                {
                    _settings.CopyToClipboardAutomatically = value;
                    SaveSettings();
                }));

        y = Row(ui, x, y, width,
            "Save screenshots automatically",
            "Captures are written straight into the save folder.",
            row => Switch(ui, 43, ActionSlot(row, S(44)), _settings.SaveAutomatically,
                value =>
                {
                    _settings.SaveAutomatically = value;
                    SaveSettings();
                }));

        y = Section(ui, "SHORTCUTS", x, y, width);

        // On the header line: a reset belongs to the group, not to a row of its own.
        if (!HotkeysAreDefault())
        {
            var reset = new Rect(x + width - S(132), y - S(36), S(132), S(28));
            if (ui.Button(55, reset, "Restore defaults",
                glyph: Icons.Undo, glyphSize: S(12), fontSize: S(Metrics.FontCaption)))
                ResetHotkeys();
        }

        ui.Text(
            "Click a shortcut, then press the new keys. Backspace unbinds, Delete restores that one, Esc cancels.",
            new Rect(x, y, width, S(16)),
            theme.TextTertiary, (float)S(Metrics.FontCaption), middle: false);
        y += S(30);

        var hotkeyWidth = HotkeyWidth(ui);

        y = Hotkey(ui, 44, x, y, width, hotkeyWidth, "Capture region", _settings.CaptureRegionHotkey);
        y = Hotkey(ui, 45, x, y, width, hotkeyWidth, "Capture full screen", _settings.CaptureFullScreenHotkey);
        y = Hotkey(ui, 46, x, y, width, hotkeyWidth, "Capture active window", _settings.CaptureActiveWindowHotkey);
        y = Hotkey(ui, 47, x, y, width, hotkeyWidth, "Open NexusShot", _settings.OpenMainWindowHotkey);

        if (_hotkeyWarning is { } warning)
        {
            ui.Text(warning, new Rect(x, y + S(6), width, S(20)),
                theme.Danger, (float)S(Metrics.FontCaption), middle: false);
            y += S(28);
        }

        y = Section(ui, "PREVIEW", x, y, width);

        y = Row(ui, x, y, width,
            "Auto-dismiss after",
            "Seconds before a floating preview disappears. 0 keeps it open.",
            row => NumberField(ui, 48, ActionSlot(row, S(120)), _settings.PreviewDismissSeconds, 0, 120,
                value =>
                {
                    _settings.PreviewDismissSeconds = value;
                    SaveSettings();
                }));

        y = Section(ui, "GENERAL", x, y, width);

        y = Row(ui, x, y, width, "Theme", null,
            row => _themeBox.Field(ui, 49, ActionSlot(row, S(148)),
                ["System", "Light", "Dark"],
                (int)_settings.Theme,
                index =>
                {
                    _settings.Theme = (AppTheme)index;
                    SaveSettings();
                }));

        y = Row(ui, x, y, width,
            "Start NexusShot with Windows", null,
            row => Switch(ui, 50, ActionSlot(row, S(44)), _settings.StartWithWindows,
                value =>
                {
                    _settings.StartWithWindows = value;
                    Startup.Set(value);
                    SaveSettings();
                }));

        // Last frame's extent, which the scroll position was clamped against above - so the thumb
        // and the rows agree.
        ui.Scrollbar(body, _settingsHeight, _settingsScroll);
        ui.PopClip();

        // Open lists paint after the clip is popped: a list is allowed to overhang the rows below it
        // and the body's own edge, which is the point of a dropdown.
        _captureModeBox.DrawOpen(ui, body);
        _themeBox.DrawOpen(ui, body);

        // The trailing margin is part of the content: added to the extent after measuring, it would
        // buy scroll travel no pixel occupies.
        y += S(32);
        _settingsHeight = y - top;
    }

    private readonly Dropdown _captureModeBox = new();
    private readonly Dropdown _themeBox = new();

    /// <summary>An open list is anchored to a row, so anything that moves or hides that row - a
    /// scroll, Escape, leaving settings - has to take the list with it.</summary>
    private void CloseDropdowns()
    {
        _captureModeBox.Close();
        _themeBox.Close();
    }

    private bool DropdownOpen => _captureModeBox.IsOpen || _themeBox.IsOpen;

    /// <summary>SectionHeaderStyle: caption, SemiBold, TextTertiary. A generous leading gap and a
    /// smaller trailing gap make the heading belong to the rows below it, while keeping adjacent
    /// groups visually distinct.</summary>
    private double Section(Ui ui, string title, double x, double y, double width)
    {
        y += S(28);
        ui.Text(title, new Rect(x, y, width, S(16)),
            ui.Theme.TextTertiary, (float)S(Metrics.FontCaption), bold: true, middle: false);
        return y + S(16) + S(10);
    }

    /// <summary>
    /// SettingRowStyle: at least 48 high, bottom border StrokeSubtle, ColumnSpacing=24.
    /// A title, an optional caption, and a control on the right. The control draws itself into
    /// the slot the row hands it. The minimum height deliberately leaves eight pixels above and
    /// below a 32-pixel control, so consecutive buttons never read as one clumped control stack.
    /// </summary>
    private double Row(
        Ui ui, double x, double y, double width,
        string title, string? caption, Action<Rect> control)
    {
        var theme = ui.Theme;
        var pad = S(10);

        var textHeight = caption is null ? S(18) : S(18) + S(4) + S(18);
        var height = Math.Max(S(48), pad * 2 + textHeight);
        var row = new Rect(x, y, width, height);

        var textWidth = width - S(260) - S(24);

        if (caption is null)
        {
            ui.Text(title, new Rect(x, row.Y, textWidth, height),
                theme.TextPrimary, (float)S(Metrics.FontBody));
        }
        else
        {
            ui.Text(title, new Rect(x, row.Y + pad, textWidth, S(18)),
                theme.TextPrimary, (float)S(Metrics.FontBody), middle: false);
            ui.Text(caption, new Rect(x, row.Y + pad + S(18) + S(4), textWidth, S(18)),
                theme.TextTertiary, (float)S(Metrics.FontCaption), middle: false, wrap: true);
        }

        control(row);

        ui.FillRect(new Rect(x, row.Bottom, width, 1), theme.StrokeSubtle);
        return row.Bottom + 1;
    }

    /// <summary>The right-aligned slot a row's control sits in. ShellButtonStyle is 32 tall.</summary>
    private Rect ActionSlot(Rect row, double width) =>
        new(row.Right - width, row.Center.Y - S(16), width, S(32));

    /// <summary>A toggle switch: a track with a knob that slides.</summary>
    private void Switch(Ui ui, int id, Rect slot, bool value, Action<bool> set)
    {
        var track = new Rect(slot.Right - S(40), slot.Center.Y - S(10), S(40), S(20));
        if (ui.Interact(id, track)) set(!value);

        ui.FillRounded(track, (float)S(10),
            value ? ui.Theme.Accent
            : ui.IsHot(id) ? ui.Theme.StrokeStrong
            : ui.Theme.StrokeDefault);

        var knob = new Point(value ? track.Right - S(10) : track.X + S(10), track.Center.Y);
        ui.FillCircle(knob, (float)S(7), Rgba.White);
    }

    /// <summary>An editable number box. Clicking focuses it; the window's key handler types into it.
    /// Commits on Enter or on clicking away, clamped to the range. An empty box means the minimum.</summary>
    private void NumberField(Ui ui, int id, Rect slot, int value, int min, int max, Action<int> set)
    {
        var focused = _editingNumber == id;
        if (focused) _numberBounds = slot;

        if (ui.Interact(id, slot) && !focused)
        {
            _editingNumber = id;
            _numberDraft = value.ToString();
        }

        var radius = (float)S(Metrics.RadiusControl);
        ui.FillRounded(slot, radius, ui.Theme.SurfaceOverlay);
        ui.StrokeRounded(slot, radius,
            focused ? ui.Theme.Accent
            : ui.IsHot(id) ? ui.Theme.StrokeStrong
            : ui.Theme.StrokeDefault,
            focused ? 1.5f : 1f);

        var text = focused ? _numberDraft : value.ToString();
        var inner = slot.Deflate(S(10));

        ui.Text(text, inner, ui.Theme.TextPrimary, (float)S(Metrics.FontBody));

        if (!focused) return;

        // A caret, so a focused empty box does not read as a dead one.
        var caretX = inner.X + ui.MeasureText(text, S(Metrics.FontBody)) + S(1);
        ui.FillRect(new Rect(caretX, slot.Y + S(8), S(1.5), slot.Height - S(16)), ui.Theme.TextPrimary);

        // Held so the key handler can commit into the right setting without knowing which row it is.
        _numberCommit = () =>
        {
            var parsed = int.TryParse(_numberDraft, out var typed) ? typed : min;
            set(Math.Clamp(parsed, min, max));
        };
    }

    /// <summary>The number box being typed into, if any, and the text as typed.</summary>
    private int? _editingNumber;
    private string _numberDraft = "";

    /// <summary>Where the focused box sits, so a click landing anywhere else commits it.</summary>
    private Rect _numberBounds;

    /// <summary>Writes the focused box's draft back into whichever setting it belongs to.</summary>
    private Action? _numberCommit;

    /// <summary>Commits and unfocuses the number box, if one is focused.</summary>
    private void CommitNumberField()
    {
        if (_editingNumber is null) return;

        _numberCommit?.Invoke();
        _numberCommit = null;
        _editingNumber = null;
    }

    private const string Recording = "Press keys…";

    /// <summary>The width every recorder shares: the widest label any of them can show. Sizing each
    /// to its own label leaves a ragged column, and a button that resizes when armed jumps.</summary>
    private double HotkeyWidth(Ui ui)
    {
        var font = S(Metrics.FontCaption);
        var widest = ui.MeasureText(Recording, font);

        foreach (var id in HotkeyIds)
        {
            if (Binding(id) is not { } binding) continue;
            widest = Math.Max(widest, ui.MeasureText(Describe(binding), font));
        }

        return Math.Max(S(96), widest + S(28));
    }

    /// <summary>A hotkey recorder. Clicking arms it; the next key press becomes the binding. The
    /// window's key handler does the recording - there is nothing here to listen with.</summary>
    private double Hotkey(
        Ui ui, int id, double x, double y, double width, double slotWidth,
        string title, HotkeyBinding binding)
    {
        return Row(ui, x, y, width, title, null, row =>
        {
            var recording = _recordingHotkey == id;
            var label = recording ? Recording : Describe(binding);

            // The clear gutter is always reserved, so clearing does not shift the recorder.
            var clearable = binding.Key != 0 && !recording;
            var full = ActionSlot(row, slotWidth);
            var clear = new Rect(full.Right - S(22), full.Center.Y - S(11), S(22), S(22));
            var slot = new Rect(full.X - S(26), full.Y, full.Width, full.Height);

            if (clearable)
            {
                if (ui.Interact(id + 7, clear))
                {
                    binding.Modifiers = 0;
                    binding.Key = 0;
                    _hotkeyWarning = null;
                    SaveSettings();
                    HotkeysChanged?.Invoke();
                    Invalidate();
                }

                if (ui.IsHot(id + 7))
                    ui.FillRounded(clear, (float)S(Metrics.RadiusControl), ui.Theme.FillHover);

                ui.Text("✕", clear,
                    ui.IsHot(id + 7) ? ui.Theme.TextPrimary : ui.Theme.TextTertiary,
                    (float)S(Metrics.FontCaption), align: TextAlign.Center);
            }

            // The click lands after `recording` was read, so this frame still draws the old label.
            // Without the repaint, "Press keys…" would not appear until the next mouse move.
            if (ui.Interact(id, slot))
            {
                _recordingHotkey = recording ? null : id;
                _hotkeyWarning = null;
                RecordingChanged?.Invoke(_recordingHotkey is not null);
                Invalidate();
            }

            ui.FillRounded(slot, (float)S(Metrics.RadiusControl),
                recording ? ui.Theme.FillSelected
                : ui.IsHot(id) ? ui.Theme.FillHover
                : ui.Theme.SurfaceOverlay);

            ui.StrokeRounded(slot, (float)S(Metrics.RadiusControl),
                recording ? ui.Theme.Accent : ui.Theme.StrokeSubtle,
                recording ? 1.5f : 1f);

            ui.Text(label, slot,
                recording ? ui.Theme.Accent : ui.Theme.TextSecondary,
                (float)S(Metrics.FontCaption), align: TextAlign.Center);
        });
    }

    /// <summary>Describe() for a sidebar hint: unbound draws nothing rather than the word "None".</summary>
    private static string Hint(HotkeyBinding binding) => binding.Key == 0 ? "" : Describe(binding);

    /// <summary>A binding as text: "Ctrl + Shift + S".</summary>
    private static string Describe(HotkeyBinding binding)
    {
        if (binding.Key == 0) return "None";

        var parts = new List<string>(4);
        if ((binding.Modifiers & 0x0002) != 0) parts.Add("Ctrl");
        if ((binding.Modifiers & 0x0004) != 0) parts.Add("Shift");
        if ((binding.Modifiers & 0x0001) != 0) parts.Add("Alt");
        if ((binding.Modifiers & 0x0008) != 0) parts.Add("Win");
        parts.Add(KeyName(binding.Key));

        return string.Join(" + ", parts);
    }

    private static string KeyName(uint key) => key switch
    {
        >= 0x70 and <= 0x87 => $"F{key - 0x6F}",           // F1..F24
        0x2C => "PrtScn",
        0x2D => "Insert",
        0x2E => "Delete",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PgUp",
        0x22 => "PgDn",
        0x20 => "Space",
        >= 0x30 and <= 0x5A => ((char)key).ToString(),      // 0-9, A-Z
        _ => $"0x{key:X2}",
    };

    /// <summary>A path, shortened from the middle so both ends stay readable.</summary>
    private static string Shorten(string path, int limit)
    {
        if (path.Length <= limit) return path;
        var keep = (limit - 3) / 2;
        return $"{path[..keep]}…{path[^keep..]}";
    }

    // ============================  DATA  ============================

    private void DrawThumbnail(
        Ui ui, IComObject<ID2D1RenderTarget> target, ScreenshotHistoryItem item, Rect slot)
    {
        var bitmap = GetThumbnail(target, item);
        if (bitmap is null) return;

        // Aspect-fill, clipped to the chip. A letterboxed thumbnail in a 52x34 cell is mostly empty
        // background; filling it makes the row scannable, which is the whole job of a thumbnail.
        var fit = slot.Cover(new Size(bitmap.Width, bitmap.Height));

        target.Object.PushAxisAlignedClip(
            AnnotationRenderer.ToRect(slot), D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_ALIASED);
        target.DrawBitmap(
            bitmap.Bitmap, 1f,
            D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR,
            AnnotationRenderer.ToRect(fit));
        target.Object.PopAxisAlignedClip();
    }

    /// <summary>
    /// The bitmap for a row's thumbnail, or null while it is still being decoded.
    ///
    /// The decode runs off the UI thread: inflating a PNG costs tens of milliseconds even when the
    /// result is a 52x34 chip. The upload has to happen here, on the thread that owns the device, and
    /// the row fills in on the next frame.
    /// </summary>
    private ImageSurface? GetThumbnail(IComObject<ID2D1RenderTarget> target, ScreenshotHistoryItem item)
    {
        if (_thumbnails.TryGetValue(item.FilePath, out var cached)) return cached;

        // Decoded and waiting: upload it now that we are on the thread that owns the device.
        if (_decoded.TryRemove(item.FilePath, out var pixels))
        {
            using var uploadContext = target.AsDeviceContext();
            if (uploadContext is null) { pixels.Dispose(); return null; }

            // The pixels exist only to reach the GPU; the surface is what the cache keeps.
            using (pixels)
            {
                var surface = ImageSurface.Upload(pixels, uploadContext);

                // The evicted surface owns a GPU bitmap: dropping the reference would leak it.
                if (_thumbnails.Add(item.FilePath, surface, out var evicted)) evicted?.Dispose();
                return surface;
            }
        }

        StartDecode(item.FilePath);
        return null;
    }

    /// <summary>Decoded thumbnail pixels waiting to be uploaded, keyed by file.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DecodedImage> _decoded = new();

    /// <summary>Drops pixels for files no longer in the history. An entry is only consumed when its
    /// row is drawn, so one deleted first would be held forever.</summary>
    public void SweepDecoded()
    {
        if (_decoded.IsEmpty) return;

        var live = _history.Select(item => item.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _decoded.Keys)
            if (!live.Contains(path) && _decoded.TryRemove(path, out var pixels)) pixels.Dispose();
    }

    private void StartDecode(string path)
    {
        if (!_decodes.TryStart(path)) return;
        var generation = _decodes.Generation;

        Task.Run(() =>
        {
            DecodedImage? pixels = null;
            try
            {
                // 2x the chip, so it stays crisp on a scaled display.
                pixels = ImageSurface.DecodeScaled(path, maxWidth: 160, maxHeight: 160);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException
                or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
            {
                // A capture that will not decode simply has no thumbnail.
                Log.Error("thumbnail.decode", exception, path);
            }

            Post(() =>
            {
                // Rejected pixels are freed here: this callback is their only owner, so returning
                // without disposing would strand a decode's worth of memory per stale repaint.
                if (_decodes.Finish(path, generation, pixels is not null) != DecodeOutcome.Accept)
                {
                    pixels?.Dispose();
                }
                else if (_decoded.TryGetValue(path, out var superseded) && !ReferenceEquals(superseded, pixels))
                {
                    _decoded[path] = pixels!;
                    superseded.Dispose();
                }
                else _decoded[path] = pixels!;

                Invalidate();
            });
        });
    }

    /// <summary>
    /// One detail decode at a time. Completed pixels are admitted on the UI thread only if their
    /// cache generation and selection still match.
    /// </summary>
    private bool _previewLoading;

    /// <summary>Pixels decoded off-thread, waiting for a render to upload them.</summary>
    private (string Path, DecodedImage Image, (int Width, int Height) Size)? _pendingPreview;

    /// <summary>
    /// The display-sized bitmap for the detail preview, decoded on a worker.
    ///
    /// The upload needs a live device, so it happens here - inside a render call - rather than from
    /// the worker's completion: a render target handed to one frame can be resized or disposed
    /// before an asynchronous callback would run.
    /// </summary>
    private ImageSurface? GetPreviewBitmap(IComObject<ID2D1RenderTarget> target, ScreenshotHistoryItem item, Rect well)
    {
        var path = item.FilePath;
        // Round up to avoid restarting a decode for every pixel of a window resize. These are
        // physical pixels, so this remains sharp on a high-DPI monitor without storing a 4K image
        // behind an 800-pixel preview. Editors and exports still use the original file.
        var size = (Width: Math.Max(256, (int)Math.Ceiling(well.Width / 256) * 256),
            Height: Math.Max(256, (int)Math.Ceiling(well.Height / 256) * 256));

        if (_pendingPreview is { } pending && pending.Path == path)
        {
            _pendingPreview = null;
            using (pending.Image)
            {
                using var context = target.AsDeviceContext();
                if (context is not null)
                {
                    _preview?.Dispose();
                    _preview = ImageSurface.Upload(pending.Image, context);
                    _previewPath = path;
                    _previewSize = pending.Size;
                }
            }
        }

        if (_previewPath == path && _preview is not null
            && _previewSize.Width >= size.Width && _previewSize.Height >= size.Height) return _preview;
        if (_previewLoading || !File.Exists(path) || !_previewDecodes.TryStart(path)) return null;

        _previewLoading = true;
        var generation = _previewDecodes.Generation;
        _ = Task.Run(() =>
        {
            DecodedImage? decoded = null;
            try
            {
                decoded = ImageSurface.DecodeScaled(path, size.Width, size.Height);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException
                or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
            {
                // A capture that will not decode shows the empty preview rather than failing the frame.
                Log.Error("preview.decode", exception, path);
            }

            Post(() =>
            {
                _previewLoading = false;

                // The selection is part of this decode's world: pixels for a capture the user has
                // already moved off are as stale as pixels from an older generation.
                var outcome = _previewDecodes.Finish(path, generation, decoded is not null,
                    stillWanted: _selected?.FilePath == path);

                if (outcome != DecodeOutcome.Accept) decoded?.Dispose();
                else
                {
                    // A pending decode that was never drawn still owns its pixels.
                    DropPendingPreview();
                    _pendingPreview = (path, decoded!, size);
                }
                Invalidate();
            });
        });
        return null;
    }

    /// <summary>Frees a decode that arrived but was never uploaded. The pending slot owns its
    /// pixels, so every path that clears it goes through here.</summary>
    private void DropPendingPreview()
    {
        _pendingPreview?.Image.Dispose();
        _pendingPreview = null;
    }

    /// <summary>Closes the capture back to the empty state, releasing its full-resolution bitmap.</summary>
    private void Deselect()
    {
        _selected = null;
        DropPendingPreview();

        _preview?.Dispose();
        _preview = null;
        _previewPath = null;

        Invalidate();
    }

    private void Delete(ScreenshotHistoryItem item)
    {
        _history.Remove(item);

        // Deleting what you were looking at lands on the empty state, rather than decoding whichever
        // capture happens to be next.
        if (ReferenceEquals(_selected, item)) Deselect();

        DropCache(item.FilePath);

        // history.json is editable on disk, so its paths are not trusted. Delete only under a root
        // we own: the screenshot folder, or temp for a capture that was never saved.
        try
        {
            var full = Path.GetFullPath(item.FilePath);
            if (IsUnder(full, _settings.ScreenshotFolder) || IsUnder(full, Path.GetTempPath()))
                File.Delete(full);
            else
                Log.Error("history.delete_outside_root", new InvalidOperationException(full));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The file may be open elsewhere; the history entry still goes.
        }

        _storage.SaveHistory(_history);
        Invalidate();
    }

    /// <summary>Whether the path sits inside root. The trailing separator matters: without it
    /// "C:\Shots-elsewhere" prefix-matches "C:\Shots".</summary>
    private static bool IsUnder(string fullPath, string root)
    {
        try
        {
            var normalized = Path.GetFullPath(root).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(normalized, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return false;
        }
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
            case UiThreadDispatch.Message:
                _dispatch.Drain();
                return new LRESULT { Value = 0 };

            case SystemTheme.WM_SETTINGCHANGE:
                // The only signal an unpackaged app gets that the user flipped the system theme.
                if (SystemTheme.IsColorSetChange(msg, (IntPtr)lParam.Value.ToInt64())
                    && _settings.Theme == AppTheme.System)
                    ApplyTheme();
                break;

            case WmLButtonDown:
                _pointer = ClientPoint(lParam);
                _pointerDown = true;

                // Clicking away from a focused box commits it, the way moving focus off a real text
                // box does. A click inside it is the box's own.
                if (_editingNumber is not null && !_numberBounds.Contains(_pointer))
                    CommitNumberField();

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
                // A notched mouse sends 120 at a time; a precision touchpad sends a stream of much
                // smaller deltas. Acting on each one immediately turns a two-finger drag into a
                // burst of sub-pixel scrolls, so the remainder is carried and only whole units spent.
                var delta = (short)((wParam.Value.ToUInt64() >> 16) & 0xFFFF);
                var step = delta / WheelDelta * S(50);

                // Whichever pane the pointer is over gets the wheel.
                double before, after;
                if (_settingsOpen && _pointer.X > S(248))
                {
                    CloseDropdowns();
                    before = _settingsScroll;
                    var maximum = Math.Max(0, _settingsHeight - _settingsViewport);
                    _settingsScroll = after = Math.Clamp(_settingsScroll - step, 0, maximum);
                }
                else
                {
                    before = _scroll;
                    var maximum = Math.Max(0, _historyHeight - _historyViewport);
                    _scroll = after = Math.Clamp(_scroll - step, 0, maximum);
                }

                // A touchpad keeps sending deltas after the clamp has pinned the view at an end;
                // repainting on those is what made the pane shake against its own bottom.
                if (after != before) Invalidate();
                return new LRESULT { Value = 0 };
            }

            case WmKeyDown:
            {
                var key = (VIRTUAL_KEY)(ulong)wParam.Value;

                if (_recordingHotkey is not null)
                {
                    RecordHotkey(key);
                    return new LRESULT { Value = 0 };
                }

                // A focused number box owns the keyboard. Its digits arrive as WM_CHAR; only the
                // editing keys are handled here.
                if (_editingNumber is not null)
                {
                    switch (key)
                    {
                        case VIRTUAL_KEY.VK_BACK:
                            if (_numberDraft.Length > 0) _numberDraft = _numberDraft[..^1];
                            break;

                        case VIRTUAL_KEY.VK_RETURN:
                            CommitNumberField();
                            break;

                        case VIRTUAL_KEY.VK_ESCAPE:
                            // Abandons the edit rather than committing it, and keeps the pane open.
                            _numberCommit = null;
                            _editingNumber = null;
                            break;

                        default:
                            return new LRESULT { Value = 0 };
                    }

                    Invalidate();
                    return new LRESULT { Value = 0 };
                }

                if (key == VIRTUAL_KEY.VK_ESCAPE)
                {
                    // Escape peels one layer: the open list, then settings, then the capture on
                    // show, then the window.
                    if (DropdownOpen) CloseDropdowns();
                    else if (_settingsOpen) _settingsOpen = false;
                    else if (_selected is not null) Deselect();
                    else { Hide(); return new LRESULT { Value = 0 }; }

                    Invalidate();
                    return new LRESULT { Value = 0 };
                }
                break;
            }

            case WmChar:
            {
                if (_editingNumber is null) break;

                // Digits only, and never more than the three a 0-120 value can need.
                var character = (char)(ulong)wParam.Value;
                if (char.IsAsciiDigit(character) && _numberDraft.Length < 3)
                {
                    _numberDraft += character;
                    Invalidate();
                }
                return new LRESULT { Value = 0 };
            }

            case WmClose:
                Hide();
                return new LRESULT { Value = 0 };

            case 0x0018 when wParam.Value == 0: // WM_SHOWWINDOW: every path to hidden releases pixels.
                ReleaseVisuals();
                if (_recordingHotkey is not null)
                {
                    _recordingHotkey = null;
                    RecordingChanged?.Invoke(false);
                }
                break;
        }
        return base.WindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Turns the key press into a binding for the armed row.
    ///
    /// A bare modifier is not a shortcut, so those are ignored and recording stays armed until a
    /// real key arrives. Esc cancels, Backspace unbinds, Delete restores the default - and a single key such as F9
    /// or PrtScn is a legitimate shortcut, so no modifier is required.
    /// </summary>
    private void RecordHotkey(VIRTUAL_KEY key)
    {
        if (_recordingHotkey is not { } id) return;

        if (key is VIRTUAL_KEY.VK_ESCAPE)
        {
            _recordingHotkey = null;
            RecordingChanged?.Invoke(false);
            Invalidate();
            return;
        }

        // Modifiers alone are the user still assembling the chord.
        if (key is VIRTUAL_KEY.VK_CONTROL or VIRTUAL_KEY.VK_SHIFT or VIRTUAL_KEY.VK_MENU
            or VIRTUAL_KEY.VK_LWIN or VIRTUAL_KEY.VK_RWIN
            or VIRTUAL_KEY.VK_LCONTROL or VIRTUAL_KEY.VK_RCONTROL
            or VIRTUAL_KEY.VK_LSHIFT or VIRTUAL_KEY.VK_RSHIFT
            or VIRTUAL_KEY.VK_LMENU or VIRTUAL_KEY.VK_RMENU)
            return;

        var target = Binding(id);
        if (target is null)
        {
            _recordingHotkey = null;
            RecordingChanged?.Invoke(false);
            return;
        }

        // Backspace unbinds - key 0 is never registered. Delete puts the default back.
        if (key == VIRTUAL_KEY.VK_BACK)
        {
            target.Modifiers = 0;
            target.Key = 0;
        }
        else if (key == VIRTUAL_KEY.VK_DELETE)
        {
            var restored = Binding(id, new AppSettings())!;
            target.Modifiers = restored.Modifiers;
            target.Key = restored.Key;
        }
        else
        {
            uint modifiers = 0;
            if (Down(VIRTUAL_KEY.VK_CONTROL)) modifiers |= 0x0002;
            if (Down(VIRTUAL_KEY.VK_SHIFT)) modifiers |= 0x0004;
            if (Down(VIRTUAL_KEY.VK_MENU)) modifiers |= 0x0001;
            if (Down(VIRTUAL_KEY.VK_LWIN) || Down(VIRTUAL_KEY.VK_RWIN)) modifiers |= 0x0008;

            target.Modifiers = modifiers;
            target.Key = (uint)key;
        }

        _recordingHotkey = null;
        SaveSettings();
        HotkeysChanged?.Invoke();
        Invalidate();

        static bool Down(VIRTUAL_KEY key) => (Functions.GetKeyState((int)key) & 0x8000) != 0;
    }

    private static readonly int[] HotkeyIds = [44, 45, 46, 47];

    /// <summary>Whether every binding already matches a fresh AppSettings.</summary>
    private bool HotkeysAreDefault()
    {
        var defaults = new AppSettings();
        foreach (var id in HotkeyIds)
        {
            if (Binding(id) is not { } current || Binding(id, defaults) is not { } fallback) continue;
            if (!current.IsSameGesture(fallback)) return false;
        }
        return true;
    }

    private void ResetHotkeys()
    {
        var defaults = new AppSettings();
        foreach (var id in HotkeyIds)
        {
            if (Binding(id) is not { } current || Binding(id, defaults) is not { } fallback) continue;
            current.Modifiers = fallback.Modifiers;
            current.Key = fallback.Key;
        }

        _recordingHotkey = null;
        _hotkeyWarning = null;
        SaveSettings();
        HotkeysChanged?.Invoke();
        Invalidate();
    }

    /// <summary>The binding a hotkey row edits.</summary>
    private HotkeyBinding? Binding(int id, AppSettings? from = null)
    {
        var settings = from ?? _settings;
        return id switch
        {
            44 => settings.CaptureRegionHotkey,
            45 => settings.CaptureFullScreenHotkey,
            46 => settings.CaptureActiveWindowHotkey,
            47 => settings.OpenMainWindowHotkey,
            _ => null,
        };
    }

    /// <summary>Reports bindings that another application already owns, so the user can see which
    /// one clashed rather than wondering why nothing happens.</summary>
    public void ReportHotkeyConflicts(IReadOnlyList<HotkeyId> failed)
    {
        _hotkeyWarning = failed.Count == 0
            ? null
            : $"Another app already owns: {string.Join(", ", failed.Select(Describe))}.";
        Invalidate();

        static string Describe(HotkeyId id) => id switch
        {
            HotkeyId.CaptureRegion => "Capture region",
            HotkeyId.CaptureFullScreen => "Capture full screen",
            HotkeyId.CaptureActiveWindow => "Capture active window",
            HotkeyId.OpenMainWindow => "Open NexusShot",
            _ => id.ToString(),
        };
    }

    private static Point ClientPoint(LPARAM lParam)
    {
        var value = lParam.Value.ToInt64();
        return new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
    }

    protected override void Dispose(bool disposing)
    {
        _dispatch?.Clear();
        ReleaseVisuals();
        base.Dispose(disposing);
    }
}
