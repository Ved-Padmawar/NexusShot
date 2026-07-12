using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>
/// The shell: a sidebar that browses, a pane that previews and acts. Annotating opens the editor as
/// its own window rather than docking it here, so the sidebar's width is never taken from the image.
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

    /// <summary>Thumbnail-sized decodes, one per row. Small enough to keep for the whole history.</summary>
    private readonly Dictionary<string, ImageSurface> _thumbnails = [];

    /// <summary>The full-resolution bitmap for the selected capture, and only that one.</summary>
    private ImageSurface? _preview;
    private string? _previewPath;

    private ScreenshotHistoryItem? _selected;
    private Point _pointer;
    private bool _pointerDown;
    private double _scroll;

    private bool _settingsOpen;
    private double _settingsScroll;
    private double _settingsHeight;

    /// <summary>The hotkey row that is armed, if any. The next key press becomes its binding.</summary>
    private int? _recordingHotkey;
    private string? _hotkeyWarning;

    /// <summary>Raised when the bindings change, so the app can re-register them.</summary>
    public event Action? HotkeysChanged;

    /// <summary>Raised when any setting changes, so the app can react (the watcher follows the save
    /// folder, for one).</summary>
    public event Action? SettingsChanged;

    /// <summary>
    /// Runs work on the UI thread.
    ///
    /// The folder watcher fires on a thread pool thread, and mutating the history from there would
    /// be doing it underneath a frame that is drawing it.
    /// </summary>
    public void Post(Action work)
    {
        lock (_posted) _posted.Enqueue(work);
        PostMessageW(Handle, WmRunPosted, IntPtr.Zero, IntPtr.Zero);
    }

    private readonly Queue<Action> _posted = new();
    private const uint WmRunPosted = 0x0400 + 2;   // WM_APP + 2

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

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

    protected override void OnCreated(object? sender, EventArgs e)
    {
        base.OnCreated(sender, e);
        AppIcon.Apply(Handle);
        ApplyTheme();
    }

    /// <summary>The titlebar is not ours to draw, so DWM has to be told separately - a dark app with
    /// a light titlebar looks broken however well the client area is themed.</summary>
    private void ApplyTheme() =>
        SystemTheme.ApplyTitleBar(Handle, SystemTheme.Resolve(_settings.Theme).IsDark);

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
        if (_thumbnails.Remove(path, out var thumbnail)) thumbnail.Dispose();

        if (_previewPath != path) return;
        _preview?.Dispose();
        _preview = null;
        _previewPath = null;
    }

    // ============================  RENDER  ============================

    protected override void Render(IComObject<ID2D1HwndRenderTarget> renderTarget)
    {
        using var target = renderTarget.AsRenderTarget();
        target.Object.SetDpi(96, 96);

        _resources ??= new D2DResources(target);
        _ui ??= new Ui(_resources);
        _ui.Theme = SystemTheme.Resolve(_settings.Theme);

        _scale = Functions.GetDpiForWindow(Handle) / 96.0;
        

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
        y = DrawCaptureAction(ui, bounds, y, 1, Icons.CaptureRegion, "Region", "Ctrl+Shift+S",
            CaptureMode.Region);
        y = DrawCaptureAction(ui, bounds, y, 2, Icons.CaptureScreen, "Full screen", "Ctrl+Shift+F",
            CaptureMode.FullScreen);
        y = DrawCaptureAction(ui, bounds, y, 3, Icons.CaptureWindow, "Active window", "Ctrl+Shift+W",
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
        string glyph, string label, string shortcut, CaptureMode mode)
    {
        var theme = ui.Theme;
        var row = new Rect(sidebar.X + S(10), y, sidebar.Width - S(20), S(38));

        if (ui.Interact(id, row)) CaptureRequested?.Invoke(mode);

        var fill = ui.IsActive(id) ? theme.FillPressed : ui.IsHot(id) ? theme.FillHover : default;
        if (fill.A > 0) ui.FillRounded(row, (float)S(Metrics.RadiusControl), fill);

        // The icon carries the accent: it is the only colour in an otherwise neutral row.
        ui.Icon(glyph, new Rect(row.X + S(8), row.Y, S(20), row.Height), theme.Accent, S(15));

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
            Icons.Settings, S(15), "Settings"))
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

        var bitmap = GetFullBitmap(target, item);
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

        // Actions, right-aligned. Buttons hug their content - 14px of padding either side, as the
        // XAML style had - rather than being fixed-width blocks.
        var y = bounds.Y + (bounds.Height - S(32)) / 2;
        var right = bounds.Right - S(24);

        var font = S(Metrics.FontBody);
        var glyph = S(14);

        // Edit carries the accent: it is what this pane is for.
        var edit = ButtonWidth(ui, "Edit", font, glyph);
        right -= edit;
        if (ui.Button(23, new Rect(right, y, edit, S(32)), "Edit",
            primary: true, glyph: Icons.Edit, glyphSize: glyph, fontSize: font))
            EditRequested?.Invoke(item);

        var copy = ButtonWidth(ui, "Copy", font, glyph);
        right -= copy + S(8);
        if (ui.Button(22, new Rect(right, y, copy, S(32)), "Copy",
            glyph: Icons.Copy, glyphSize: glyph, fontSize: font))
            ClipboardImage.Copy(item.FilePath);

        right -= S(40);
        if (ui.Tile(21, new Rect(right, y, S(32), S(32)), false, Icons.Delete, glyph, "Remove"))
            Delete(item);

        right -= S(40);
        if (ui.Tile(20, new Rect(right, y, S(32), S(32)), false, Icons.Reveal, glyph,
            "Show in Explorer"))
            Reveal(item.FilePath);
    }

    /// <summary>A button sized to its content: 14px of padding either side, as the XAML style had.</summary>
    private double ButtonWidth(Ui ui, string label, double font, double glyph = 0)
    {
        var content = ui.MeasureText(label, font, bold: true);
        if (glyph > 0) content += glyph + glyph * 0.55;
        return Math.Round(content + S(28));
    }

    private void DrawEmptyState(Ui ui, Rect bounds)
    {
        var theme = ui.Theme;
        var centre = bounds.Center;

        ui.Icon(Icons.EmptyState,
            new Rect(bounds.X, centre.Y - S(66), bounds.Width, S(48)),
            theme.TextTertiary, S(38));

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
    /// immediately, so there is nothing to confirm or cancel. Rows sit directly in the column with
    /// hairline separators doing the grouping, rather than being boxed into nested cards.
    /// </summary>
    private void DrawSettings(Ui ui, Rect bounds)
    {
        var theme = ui.Theme;

        // Header, over a hairline.
        var header = new Rect(bounds.X, bounds.Y, bounds.Width, S(64));
        ui.Text("Settings", new Rect(header.X + S(28), header.Y, header.Width, header.Height),
            theme.TextPrimary, (float)S(Metrics.FontTitle), bold: true);

        if (ui.Tile(60, new Rect(header.Right - S(56), header.Center.Y - S(18), S(36), S(36)),
            false, Icons.Close, S(14), "Close settings"))
        {
            _settingsOpen = false;
        }
        ui.FillRect(new Rect(bounds.X, header.Bottom, bounds.Width, 1), theme.StrokeSubtle);

        // The scrollable body. Clipped, so a long list cannot paint over the header.
        var body = new Rect(bounds.X, header.Bottom, bounds.Width, bounds.Bottom - header.Bottom);
        ui.PushClip(body);

        // A capped, centred column: rows fill it, and it centres itself in the pane.
        var width = Math.Min(S(600), body.Width - S(64));
        var x = body.X + (body.Width - width) / 2;
        var y = body.Y + S(20) - _settingsScroll;

        y = Section(ui, "CAPTURE", x, y, width);

        y = Row(ui, x, y, width, "Save folder", Shorten(_settings.ScreenshotFolder, 52),
            row =>
            {
                if (!ui.Button(40, ActionSlot(row, S(92)), "Change…",
                    fontSize: S(Metrics.FontCaption))) return;

                if (FolderPicker.Pick(Handle, _settings.ScreenshotFolder) is { } folder)
                {
                    _settings.ScreenshotFolder = folder;
                    SaveSettings();
                }
            });

        y = Row(ui, x, y, width, "Default capture mode", null,
            row => Choice(ui, 41, ActionSlot(row, S(240)),
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

        ui.Text(
            "Click a shortcut, then press the new keys. A single key such as F9 or PrtScn works too. "
            + "Backspace restores the default, Esc cancels.",
            new Rect(x, y, width, S(32)),
            theme.TextTertiary, (float)S(Metrics.FontCaption), middle: false, wrap: true);
        y += S(38);

        y = Hotkey(ui, 44, x, y, width, "Capture region", _settings.CaptureRegionHotkey);
        y = Hotkey(ui, 45, x, y, width, "Capture full screen", _settings.CaptureFullScreenHotkey);
        y = Hotkey(ui, 46, x, y, width, "Capture active window", _settings.CaptureActiveWindowHotkey);
        y = Hotkey(ui, 47, x, y, width, "Open NexusShot", _settings.OpenMainWindowHotkey);

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
            row => Stepper(ui, 48, ActionSlot(row, S(112)), _settings.PreviewDismissSeconds, 0, 120,
                value =>
                {
                    _settings.PreviewDismissSeconds = value;
                    SaveSettings();
                }));

        y = Section(ui, "GENERAL", x, y, width);

        y = Row(ui, x, y, width, "Theme", null,
            row => Choice(ui, 49, ActionSlot(row, S(210)),
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

        ui.PopClip();

        // The scroll extent, so the wheel handler knows where the bottom is.
        _settingsHeight = y + _settingsScroll - body.Y + S(24);
    }

    /// <summary>A section header: a caption-sized label with room above it.</summary>
    private double Section(Ui ui, string title, double x, double y, double width)
    {
        ui.Text(title, new Rect(x, y + S(16), width, S(20)),
            ui.Theme.TextTertiary, (float)S(Metrics.FontCaption), bold: true, middle: false);
        return y + S(42);
    }

    /// <summary>
    /// A settings row: a title, an optional caption, and a control on the right, over a hairline.
    /// The control draws itself into the slot the row hands it.
    /// </summary>
    private double Row(
        Ui ui, double x, double y, double width,
        string title, string? caption, Action<Rect> control)
    {
        var theme = ui.Theme;
        var height = caption is null ? S(48) : S(60);
        var row = new Rect(x, y, width, height);

        var textWidth = width - S(260);

        if (caption is null)
        {
            ui.Text(title, new Rect(x, row.Y, textWidth, row.Height),
                theme.TextPrimary, (float)S(Metrics.FontBody));
        }
        else
        {
            ui.Text(title, new Rect(x, row.Y + S(11), textWidth, S(18)),
                theme.TextPrimary, (float)S(Metrics.FontBody), middle: false);
            ui.Text(caption, new Rect(x, row.Y + S(31), textWidth, S(18)),
                theme.TextTertiary, (float)S(Metrics.FontCaption), middle: false);
        }

        control(row);

        ui.FillRect(new Rect(x, row.Bottom, width, 1), theme.StrokeSubtle);
        return row.Bottom + 1;
    }

    /// <summary>The right-aligned slot a row's control sits in.</summary>
    private static Rect ActionSlot(Rect row, double width) =>
        new(row.Right - width, row.Center.Y - 14, width, 28);

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

    /// <summary>
    /// A segmented choice. A dropdown would need a popup layer and a hit-test that outlives the
    /// frame; for three mutually exclusive options, segments say the same thing in one pass and
    /// show the alternatives without a click.
    /// </summary>
    private void Choice(Ui ui, int id, Rect slot, string[] options, int selected, Action<int> set)
    {
        var segment = slot.Width / options.Length;

        ui.FillRounded(slot, (float)S(Metrics.RadiusControl), ui.Theme.SurfaceSunken);
        ui.StrokeRounded(slot, (float)S(Metrics.RadiusControl), ui.Theme.StrokeSubtle);

        for (var i = 0; i < options.Length; i++)
        {
            var bounds = new Rect(slot.X + i * segment, slot.Y, segment, slot.Height);
            var isSelected = i == selected;

            if (ui.Interact(id * 10 + i, bounds) && !isSelected) set(i);

            if (isSelected)
                ui.FillRounded(bounds.Deflate(S(2)), (float)S(4), ui.Theme.Accent);
            else if (ui.IsHot(id * 10 + i))
                ui.FillRounded(bounds.Deflate(S(2)), (float)S(4), ui.Theme.FillHover);

            ui.Text(options[i], bounds,
                isSelected ? ui.Theme.TextOnAccent : ui.Theme.TextSecondary,
                (float)S(Metrics.FontCaption), align: TextAlign.Center);
        }
    }

    /// <summary>A number stepper: minus, value, plus.</summary>
    private void Stepper(Ui ui, int id, Rect slot, int value, int min, int max, Action<int> set)
    {
        var button = S(28);

        if (ui.Button(id * 10, new Rect(slot.X, slot.Y, button, slot.Height), "−",
            enabled: value > min, fontSize: S(Metrics.FontBody)))
            set(Math.Max(min, value - 1));

        ui.Text(value.ToString(),
            new Rect(slot.X + button, slot.Y, slot.Width - button * 2, slot.Height),
            ui.Theme.TextPrimary, (float)S(Metrics.FontBody), align: TextAlign.Center);

        if (ui.Button(id * 10 + 1, new Rect(slot.Right - button, slot.Y, button, slot.Height), "+",
            enabled: value < max, fontSize: S(Metrics.FontBody)))
            set(Math.Min(max, value + 1));
    }

    /// <summary>
    /// A hotkey recorder. Clicking arms it; the next key press becomes the binding.
    ///
    /// The window's key handler does the recording, because a hotkey is defined by a real key event
    /// and there is nothing here to listen with.
    /// </summary>
    private double Hotkey(
        Ui ui, int id, double x, double y, double width, string title, HotkeyBinding binding)
    {
        return Row(ui, x, y, width, title, null, row =>
        {
            var slot = ActionSlot(row, S(180));
            var recording = _recordingHotkey == id;

            if (ui.Interact(id, slot))
            {
                _recordingHotkey = recording ? null : id;
                _hotkeyWarning = null;
            }

            ui.FillRounded(slot, (float)S(Metrics.RadiusControl),
                recording ? ui.Theme.FillSelected
                : ui.IsHot(id) ? ui.Theme.FillHover
                : ui.Theme.SurfaceOverlay);

            ui.StrokeRounded(slot, (float)S(Metrics.RadiusControl),
                recording ? ui.Theme.Accent : ui.Theme.StrokeSubtle,
                recording ? 1.5f : 1f);

            ui.Text(recording ? "Press keys…" : Describe(binding), slot,
                recording ? ui.Theme.Accent : ui.Theme.TextSecondary,
                (float)S(Metrics.FontCaption), align: TextAlign.Center);
        });
    }

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
        var fit = Cover(bitmap.Width, bitmap.Height, slot);

        target.Object.PushAxisAlignedClip(
            AnnotationRenderer.ToRect(slot), D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_ALIASED);
        target.DrawBitmap(
            bitmap.Bitmap, 1f,
            D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR,
            AnnotationRenderer.ToRect(fit));
        target.Object.PopAxisAlignedClip();
    }

    /// <summary>
    /// The bitmap for a row's thumbnail: decoded down to thumbnail size, not full resolution.
    ///
    /// A full-resolution GPU bitmap is ~33 MB for a 4K capture, and a history of them adds up fast
    /// for something that ends up in a 52x34 chip. WIC scales during the decode, so the big bitmap
    /// never exists.
    /// </summary>
    private ImageSurface? GetThumbnail(IComObject<ID2D1RenderTarget> target, ScreenshotHistoryItem item)
    {
        if (_thumbnails.TryGetValue(item.FilePath, out var cached)) return cached;

        using var context = target.AsDeviceContext();
        if (context is null || !File.Exists(item.FilePath)) return null;

        try
        {
            // 2x the chip, so it stays crisp on a scaled display.
            var surface = ImageSurface.LoadScaled(item.FilePath, context, maxWidth: 160, maxHeight: 160);
            _thumbnails[item.FilePath] = surface;
            return surface;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The full-resolution bitmap, for the detail preview only.
    ///
    /// Exactly one is held: the preview shows one capture at a time, so caching more would pin
    /// tens of megabytes each for images nothing is drawing. This is what makes the preview exact
    /// rather than an upscaled thumbnail.
    /// </summary>
    private ImageSurface? GetFullBitmap(IComObject<ID2D1RenderTarget> target, ScreenshotHistoryItem item)
    {
        if (_previewPath == item.FilePath && _preview is not null) return _preview;

        using var context = target.AsDeviceContext();
        if (context is null || !File.Exists(item.FilePath)) return null;

        _preview?.Dispose();
        _preview = null;
        _previewPath = null;

        try
        {
            _preview = ImageSurface.Load(item.FilePath, context);
            _previewPath = item.FilePath;
            return _preview;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return null;
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
            case WmRunPosted:
            {
                Action[] work;
                lock (_posted)
                {
                    work = [.. _posted];
                    _posted.Clear();
                }
                foreach (var item in work) item();
                return new LRESULT { Value = 0 };
            }

            case SystemTheme.WM_SETTINGCHANGE:
                // The only signal an unpackaged app gets that the user flipped the system theme.
                if (SystemTheme.IsColorSetChange(msg, (IntPtr)lParam.Value.ToInt64())
                    && _settings.Theme == AppTheme.System)
                {
                    ApplyTheme();
                    Invalidate();
                }
                break;

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
                var step = delta / 120.0 * S(50);

                // Whichever pane the pointer is over gets the wheel.
                if (_settingsOpen && _pointer.X > S(248))
                {
                    var viewport = Math.Max(1, ClientRect.Height - S(64));
                    var maximum = Math.Max(0, _settingsHeight - viewport);
                    _settingsScroll = Math.Clamp(_settingsScroll - step, 0, maximum);
                }
                else
                {
                    var content = _history.Count * S(50);
                    var viewport = Math.Max(1, ClientRect.Height - S(300));
                    _scroll = Math.Clamp(_scroll - step, 0, Math.Max(0, content - viewport));
                }

                Invalidate();
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

                if (key == VIRTUAL_KEY.VK_ESCAPE)
                {
                    if (_settingsOpen) { _settingsOpen = false; Invalidate(); }
                    else Hide();
                    return new LRESULT { Value = 0 };
                }
                break;
            }

            case WmClose:
                // A capture tool lives in the tray: closing the window hides it rather than exiting,
                // or the hotkeys would die with it.
                Hide();
                return new LRESULT { Value = 0 };
        }
        return base.WindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Turns the key press into a binding for the armed row.
    ///
    /// A bare modifier is not a shortcut, so those are ignored and recording stays armed until a
    /// real key arrives. Esc cancels, Backspace restores the default - and a single key such as F9
    /// or PrtScn is a legitimate shortcut, so no modifier is required.
    /// </summary>
    private void RecordHotkey(VIRTUAL_KEY key)
    {
        if (_recordingHotkey is not { } id) return;

        if (key is VIRTUAL_KEY.VK_ESCAPE)
        {
            _recordingHotkey = null;
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
            return;
        }

        if (key == VIRTUAL_KEY.VK_BACK)
        {
            var defaults = new AppSettings();
            var restored = Binding(id, defaults)!;
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
        foreach (var thumbnail in _thumbnails.Values) thumbnail.Dispose();
        _thumbnails.Clear();

        _preview?.Dispose();
        _preview = null;

        _resources?.Dispose();
        base.Dispose(disposing);
    }
}
