using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>
/// The shell: a sidebar that browses, a pane that previews and acts. Annotating opens the editor as
/// its own window rather than docking it here, so the sidebar's width is never taken from the image.
/// </summary>
public sealed partial class MainWindow : CaptionWindow
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
    private const uint WmTimer = 0x0113;

    private const nuint CopyFeedbackTimerId = 1;

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
    /// <summary>
    /// Queues work for the UI thread.
    ///
    /// The dispatcher needs the window's handle, which only exists once the window has been created,
    /// so it is built on first use rather than in OnCreated - callers post during startup, before
    /// the message loop has run and raised that event.
    /// </summary>
    public void Post(Action work) => (_dispatch ??= new UiThreadDispatch(Handle)).Post(work);

    private UiThreadDispatch? _dispatch;

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

        y = DrawCaptureAction(ui, bounds, y, Ui.Id("capture.region"), Icons.CaptureRegion, "Region",
            Hint(_settings.CaptureRegionHotkey), CaptureMode.Region);
        y = DrawCaptureAction(ui, bounds, y, Ui.Id("capture.fullscreen"), Icons.CaptureScreen, "Full screen",
            Hint(_settings.CaptureFullScreenHotkey), CaptureMode.FullScreen);
        y = DrawCaptureAction(ui, bounds, y, Ui.Id("capture.window"), Icons.CaptureWindow, "Active window",
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

            var id = Ui.Id(HistoryRow, i);
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
        if (ui.Tile(Ui.Id("main.newcapture"), new Rect(bounds.X + S(12), y, size, size), false,
            Icons.Theme, S(15), "Switch theme"))
        {
            _settings.Theme = SystemTheme.Resolve(_settings.Theme).IsDark
                ? AppTheme.Light
                : AppTheme.Dark;
            SaveSettings();
        }

        if (ui.Tile(Ui.Id("main.settings"), new Rect(bounds.Right - S(12) - size, y, size, size), _settingsOpen,
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
        if (ui.Button(Ui.Id("main.edit"), new Rect(right, y, edit, S(32)), "Edit",
            primary: true, glyph: Icons.Edit, glyphSize: glyph, fontSize: font))
            Post(() => EditRequested?.Invoke(item));

        var copy = ui.ButtonWidth("Copy", font, glyph);
        right -= copy + S(8);
        if (ui.Button(Ui.Id("main.copy"), new Rect(right, y, copy, S(32)), "Copy",
            glyph: Icons.Copy, glyphSize: glyph, fontSize: font,
            confirmation: _copied.Progress(Environment.TickCount64)))
            CopyToClipboard(item);

        // The icon-only actions sit in a taller box with a larger glyph: at 14px they read as
        // afterthoughts next to the labelled buttons they share a row with.
        var icon = S(36);
        var iconGlyph = S(17);
        var iconY = bounds.Y + (bounds.Height - icon) / 2;

        right -= icon + S(6);
        if (ui.Tile(Ui.Id("main.remove"), new Rect(right, iconY, icon, icon), false, Icons.Delete, iconGlyph, "Remove",
            destructive: true))
            Post(() => Delete(item));

        right -= icon + S(4);
        if (ui.Tile(Ui.Id("main.reveal"), new Rect(right, iconY, icon, icon), false, Icons.Reveal, iconGlyph,
            "Show in Explorer"))
            Reveal(item.FilePath);

        // Close sits apart from the pair that act on the file: it only dismisses the view.
        right -= icon + S(14);
        if (ui.Tile(Ui.Id("main.dismiss"), new Rect(right, iconY, icon, icon), false, Icons.Close, iconGlyph,
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

    // ============================  INPUT  ============================

    protected override LRESULT? WindowProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        if (MessageIntercept is { } intercept
            && intercept(msg, (long)wParam.Value, lParam.Value.ToInt64()))
            return new LRESULT { Value = 0 };

        switch (msg)
        {
            case UiThreadDispatch.Message:
                _dispatch?.Drain();
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

            case WmTimer when (nuint)wParam.Value == CopyFeedbackTimerId:
                StepCopyFeedback();
                return new LRESULT { Value = 0 };

            case WmClose:
                Hide();
                return new LRESULT { Value = 0 };

            case 0x0018 when wParam.Value == 0: // WM_SHOWWINDOW: every path to hidden releases pixels.
                ReleaseVisuals();
                StopCopyFeedback();
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

    /// <summary>The hotkey rows, in the order they are drawn. One table: the recorder, the reset,
    /// the defaults check and the row itself all read the binding from here, so adding a hotkey
    /// cannot leave one of them behind.</summary>
    private static readonly (int Id, Func<AppSettings, HotkeyBinding> Binding, string Title)[] Hotkeys =
    [
        (Ui.Id("hotkey.region"), settings => settings.CaptureRegionHotkey, "Capture region"),
        (Ui.Id("hotkey.fullscreen"), settings => settings.CaptureFullScreenHotkey, "Capture full screen"),
        (Ui.Id("hotkey.window"), settings => settings.CaptureActiveWindowHotkey, "Capture active window"),
        (Ui.Id("hotkey.open"), settings => settings.OpenMainWindowHotkey, "Open NexusShot"),
    ];

    /// <summary>Whether every binding already matches a fresh AppSettings.</summary>
    private bool HotkeysAreDefault()
    {
        var defaults = new AppSettings();
        foreach (var (_, binding, _) in Hotkeys)
            if (!binding(_settings).IsSameGesture(binding(defaults))) return false;

        return true;
    }

    private void ResetHotkeys()
    {
        var defaults = new AppSettings();
        foreach (var (_, binding, _) in Hotkeys)
        {
            var current = binding(_settings);
            var fallback = binding(defaults);
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
        foreach (var (rowId, binding, _) in Hotkeys)
            if (rowId == id) return binding(from ?? _settings);

        return null;
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
