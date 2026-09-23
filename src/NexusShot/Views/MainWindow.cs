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
    /// Queues work for the UI thread. The folder watcher fires on a thread pool thread, and mutating
    /// the history from there would be doing it underneath a frame that is drawing it.
    ///
    /// The dispatcher needs the window's handle, which only exists once the window has been created,
    /// so it is built on first use rather than in OnCreated - callers post during startup, before
    /// the message loop has run and raised that event.
    /// </summary>
    public void Post(Action work) => (_dispatch ??= new UiThreadDispatch(Handle)).Post(work);

    private UiThreadDispatch? _dispatch;

    public event Action<CaptureMode>? CaptureRequested;
    public event Action<ScreenshotHistoryItem>? EditRequested;
    public event Action<IReadOnlyList<string>>? OpenRequested;

    /// <summary>
    /// Lets the app see raw messages first. The tray icon and the global hotkeys both post to this
    /// window's handle - it is the app's message pump - so the app needs a way in without the window
    /// having to know what a tray or a hotkey is. Returning true marks the message handled.
    /// </summary>
    public Func<uint, long, long, bool>? MessageIntercept { get; set; }

    public MainWindow(Storage storage, AppSettings settings, List<ScreenshotHistoryItem> history)
        : base(Platform.SingleInstance.MainWindowTitle)
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
        FileDrop.Accept(Handle);
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
        _history.RemoveAll(existing => string.Equals(existing.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase));
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

            case FileDrop.WM_DROPFILES:
                OpenRequested?.Invoke(FileDrop.Paths((nint)wParam.Value));
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
