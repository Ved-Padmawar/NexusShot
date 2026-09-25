using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>
/// The library: every capture as a tile, grouped by day, with the capture actions across the top and
/// settings in a sheet over it. Annotating opens the editor as its own window, so the library never
/// gives up width to it.
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
    private const uint WmTimer = 0x0113;
    private const uint WmPowerBroadcast = 0x0218;
    private const ulong PbtApmResumeAutomatic = 0x12;
    private const uint WmClose = 0x0010;

    private readonly Storage _storage;
    private readonly AppSettings _settings;
    private readonly List<ScreenshotHistoryItem> _history;

    private D2DResources? _resources;
    private Ui? _ui;

    /// <summary>Tile-sized decodes, with the width each was decoded for. Bounded, or the cache grows
    /// with the history forever; the grid raises the cap to three screens of tiles, so scrolling never
    /// evicts a tile it is about to draw again.</summary>
    private readonly LruCache<string, (ImageSurface Surface, int Width)> _thumbnails = new(60);

    private readonly LibrarySelection _selection = new();

    /// <summary>Ctrl as it was at the press, from the message itself: read at draw time, a quick
    /// Ctrl+click had usually released Ctrl already.</summary>
    private bool _pressedWithControl;
    private Point _pointer;
    private bool _pointerDown;

    /// <summary>Set by a wheel scroll, cleared when the pointer really moves. Tiles slide under a
    /// still pointer while the grid scrolls, and each one played its hover as it passed - a flicker
    /// of lifts and rings, and a jump where the grid came to rest.</summary>
    private bool _hoverPaused;

    /// <summary>The grid's scroll, and the extents it is clamped against - measured as the grid is
    /// drawn, so the wheel scrolls the grid that exists rather than an estimate of it.</summary>
    private readonly ScrollState _scroll = new();
    private double _gridHeight;
    private double _gridViewport = 1;

    /// <summary>The search box's text. View state: it filters what is drawn, never the history.</summary>
    private string _query = "";

    private bool _settingsOpen;

    /// <summary>The hotkey row that is armed, if any. The next key press becomes its binding.</summary>
    private HotkeyId? _recordingHotkey;
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
    public event Action? CaptureTextRequested;
    public event Action? TimedCaptureRequested;
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
    }

    protected override void OnCreated(object? sender, EventArgs e)
    {
        base.OnCreated(sender, e);

        // Alt+Tab and the taskbar get the icon; the header carries the brand.
        AppIcon.ApplyLargeOnly(Handle);
        FileDrop.Accept(Handle);
        _clock = new FrameClock(Handle);
        _touchpad = TouchpadPan.Create(Handle, Pan, moving =>
        {
            _panning = moving;
            if (moving) StartClock();
        });
        ApplyTheme();
    }

    /// <summary>Null without DirectManipulation; the wheel path then carries the touchpad.</summary>
    private TouchpadPan? _touchpad;
    private bool _panning;

    private FrameClock? _clock;
    private long _clockStamp;

    /// <summary>The open settings sheet takes it instead of the grid.</summary>
    private void Pan(double delta)
    {
        if (_settingsOpen)
        {
            ScrollSettings(delta);
            return;
        }
        ApplyScroll(() => _scroll.Pan(delta));
    }

    /// <summary>Glides the grid; the settings sheet scrolls in steps.</summary>
    private void Wheel(double step)
    {
        if (_settingsOpen)
        {
            ScrollSettings(step);
            return;
        }
        _scroll.Wheel(step);
        _hoverPaused = true;
        StartClock();
    }

    /// <summary>Repaints only when it moved: repainting on deltas past a pinned end made the pane shake.</summary>
    private void ApplyScroll(Action change)
    {
        var before = _scroll.Position;
        change();
        _hoverPaused = true;
        if (_scroll.Position != before) Invalidate();
    }

    private void StartClock()
    {
        if (_clock is not { Running: false } clock) return;
        _clockStamp = System.Diagnostics.Stopwatch.GetTimestamp();
        clock.Start();
    }

    /// <summary>One displayed frame: the touchpad reports its movement, a wheel glide advances, and
    /// the clock stops once neither has anything left to do.</summary>
    private void OnFrameTick()
    {
        _clock!.Acknowledge();
        _touchpad?.Update();

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_clockStamp, now).TotalMilliseconds;
        _clockStamp = now;
        var gliding = false;
        ApplyScroll(() => gliding = _scroll.Step(elapsed));

        if (!gliding && !_panning) _clock.Stop();
    }

    private Theme CurrentTheme => SystemTheme.Resolve(_settings.Theme, _settings.Accent);

    /// <summary>The window paints its own caption, so only the frame's dark-mode flag still matters -
    /// it drives the shadow and the border DWM draws around us.</summary>
    private void ApplyTheme()
    {
        SystemTheme.ApplyFrame(Handle, CurrentTheme);
        Invalidate();
        ThemeChanged?.Invoke();
    }

    /// <summary>The header drags, except where its controls are.</summary>
    protected override bool IsDragRegion(Point client) =>
        client.X < ClientRect.Width - CaptionButtonsWidth && !_headerControls.Exists(rect => rect.Contains(client));

    protected override double DragBandHeight => S(56);

    /// <summary>The header's controls this frame, so the drag band leaves them clickable.</summary>
    private readonly List<Rect> _headerControls = [];

    /// <summary>Raised when the theme or accent moves, so open editors retheme with the library.</summary>
    public event Action? ThemeChanged;

    /// <summary>The single write-back for settings: persist, retheme, and tell the app.</summary>
    private void SaveSettings()
    {
        _storage.SaveSettings(_settings);
        ApplyTheme();
        SettingsChanged?.Invoke();
    }

    /// <summary>For a change made elsewhere - an editor saving a colour to the shared lists.</summary>
    public void PersistSettings() => _storage.SaveSettings(_settings);

    private double _scale = 1;

    /// <summary>Design units to physical pixels. Every metric goes through here.</summary>
    private double S(double units) => units * _scale;

    public void AddCapture(ScreenshotHistoryItem item)
    {
        _history.RemoveAll(existing => string.Equals(existing.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase));
        _history.Insert(0, item);
        _settingsOpen = false;
        _scroll.Reset();
        _storage.SaveHistory(_history);
        Invalidate();
    }

    /// <summary>Forgets a capture's cached tile, so the next frame re-decodes it. Used after an editor
    /// saves over a capture: the file has changed, and the cached pixels are the old ones.</summary>
    public void DropCache(string path)
    {
        _decodes.Invalidate(path);
        if (_thumbnails.Remove(path, out var thumbnail)) thumbnail.Surface.Dispose();
        if (_previews.Remove(path, out var preview)) preview.Dispose();
        if (_decoded.TryRemove(path, out var stale))
        {
            stale.Pixels.Dispose();
            stale.Preview.Dispose();
        }
    }

    // ============================  RENDER  ============================

    // The tray still needs this HWND, but not a D3D device and swap chain. The base class eagerly
    // creates them on WM_CREATE; defer that work until the first visible paint instead.
#pragma warning disable CS8774 // Intentionally lazy; RenderCore establishes the base class's target invariant.
    protected override void CreateRenderTarget() { }
#pragma warning restore CS8774

    /// <summary>The window's composition layers. See <see cref="CompositionLayers"/>.</summary>
    private CompositionLayers? _layers;

    private readonly BrandMark _brand = new();

    /// <summary>A frame, drawn into the layers instead of the base class's target: the grid first,
    /// then the chrome over it, then one commit so both appear together.</summary>
    protected override bool RenderCore()
    {
        if (!WindowInterop.IsWindowVisible(Handle)) return true;

        var layers = _layers ??= new CompositionLayers(Handle);
        using var resources = layers.Resources.AsRenderTarget2();
        _resources ??= new D2DResources(resources);
        _ui ??= new Ui(_resources);
        _ui.Theme = CurrentTheme;
        _scale = DpiScale;
        _ui.Scale = _scale;

        var client = ClientRect;
        var width = (double)client.Width;
        var height = (double)client.Height;
        layers.Resize(client.Width, client.Height, _ui.Theme.SurfaceWindow);

        _ui.BeginFrame(resources, _hoverPaused ? new Point(-1, -1) : _pointer, _pointerDown,
            new D2D_SIZE_F((float)width, (float)height));
        _headerControls.Clear();

        // A modal sheet takes the pointer: the library underneath draws, but must not react.
        _ui.Inert = _settingsOpen || ConfirmOpen;
        if (_history.Count > 0) DrawGrid(_ui, resources, layers, GridBounds(width, height));
        else
        {
            layers.ClearScroll();
            _bands.Clear();
        }

        using (var context = layers.BeginChrome())
        using (var chrome = context.AsRenderTarget2())
        {
            _ui.Retarget(chrome);
            DrawLibraryChrome(_ui, width, height);
            _ui.Inert = false;

            DrawSettings(_ui, width, height);
            DrawConfirm(_ui, width, height);
            DrawToast(_ui, width, height);

            // Last, so the buttons float over the app's own pixels rather than under them.
            DrawCaptionButtons(_ui, width);
            _ui.EndFrame();
            layers.EndChrome();
        }
        layers.Commit();

        if (_ui.ClickedThisFrame) Invalidate();
        Repaint(_ui.Animating ? FrameInterval
            : DateTime.UtcNow < _toastUntil ? 120
            : _ui.Blinking ? (uint)Ui.CaretBlink
            : null);
        return true;
    }

    /// <summary>Decides which worker decodes may still be used. See <see cref="DecodeCache"/>.</summary>
    private readonly DecodeCache _decodes = new();

    private void ReleaseVisuals()
    {
        _decodes.InvalidateAll();
        foreach (var decoded in _decoded.Values)
        {
            decoded.Pixels.Dispose();
            decoded.Preview.Dispose();
        }
        _decoded.Clear();
        foreach (var thumbnail in _thumbnails.Values) thumbnail.Surface.Dispose();
        _thumbnails.Clear();
        foreach (var preview in _previews.Values) preview.Dispose();
        _previews.Clear();
        _resources?.Dispose();
        _resources = null;
        _ui = null;
        _bands.Clear();
        _brand.Dispose();
        _layers?.Dispose();
        _layers = null;
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
                _pressedWithControl = ((ulong)wParam.Value & 0x0008) != 0; // MK_CONTROL
                _hoverPaused = false;
                Invalidate();
                return new LRESULT { Value = 0 };

            case WmMouseMove:
            {
                // Windows repeats the last position when the content under a still pointer changes.
                var moved = ClientPoint(lParam);
                if (moved != _pointer) _hoverPaused = false;
                _pointer = moved;
                Invalidate();
                return new LRESULT { Value = 0 };
            }

            case WmLButtonUp:
                _pointer = ClientPoint(lParam);
                _pointerDown = false;
                Invalidate();
                return new LRESULT { Value = 0 };

            case WmMouseWheel:
                Wheel((short)((wParam.Value.ToUInt64() >> 16) & 0xFFFF) / WheelDelta * S(60));
                return new LRESULT { Value = 0 };

            case FrameClock.Message:
                OnFrameTick();
                return new LRESULT { Value = 0 };

            case WmTimer when OnUpdateTimer((nuint)wParam.Value):
                return new LRESULT { Value = 0 };

            case WmPowerBroadcast when (ulong)wParam.Value == PbtApmResumeAutomatic:
                CheckAfterWake();
                break;

            case TouchpadPan.DM_POINTERHITTEST:
                _touchpad?.HitTest((nuint)wParam.Value);
                return new LRESULT { Value = 0 };

            case WmKeyDown:
                if (OnKeyDown((VIRTUAL_KEY)(ulong)wParam.Value)) return new LRESULT { Value = 0 };
                break;

            case WmChar:
                if (_ui is not { HasKeyboardFocus: true } ui) break;
                var character = (char)(ulong)wParam.Value;
                if (!char.IsControl(character)) ui.Char(character);
                Invalidate();
                return new LRESULT { Value = 0 };

            case WmClose:
                Hide();
                return new LRESULT { Value = 0 };

            case FileDrop.WM_DROPFILES:
                OpenRequested?.Invoke(FileDrop.Paths((nint)wParam.Value));
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

    private bool OnKeyDown(VIRTUAL_KEY key)
    {
        if (_recordingHotkey is not null)
        {
            RecordHotkey(key);
            return true;
        }

        // A focused field owns the keyboard; its characters arrive as WM_CHAR.
        if (_ui is { HasKeyboardFocus: true } ui)
        {
            var control = (Functions.GetKeyState((int)VIRTUAL_KEY.VK_CONTROL) & 0x8000) != 0;
            if (control && key == VIRTUAL_KEY.VK_V && ClipboardText.Paste() is { } pasted)
                foreach (var character in pasted.Trim()) ui.Char(character);
            else ui.Key(key, (Functions.GetKeyState((int)VIRTUAL_KEY.VK_SHIFT) & 0x8000) != 0);
            Invalidate();
            return true;
        }

        if (key == VIRTUAL_KEY.VK_OEM_COMMA && (Functions.GetKeyState((int)VIRTUAL_KEY.VK_CONTROL) & 0x8000) != 0)
        {
            if (_settingsOpen) CloseSettings(); else OpenSettings();
            return true;
        }

        if (key != VIRTUAL_KEY.VK_ESCAPE) return false;

        // Escape peels one layer: the delete prompt, an open list, settings, multi-select, the window.
        if (ConfirmOpen) _pendingDelete = null;
        else if (DropdownOpen) CloseDropdowns();
        else if (_settingsOpen) CloseSettings();
        else if (_selection.Active) _selection.End();
        else { Hide(); return true; }

        Invalidate();
        return true;
    }

    private static Point ClientPoint(LPARAM lParam)
    {
        var value = lParam.Value.ToInt64();
        return new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
    }

    /// <summary>A short confirmation at the foot of the window: an action that changes nothing visible
    /// still says it happened.</summary>
    private void ShowToast(string message)
    {
        _toast = message;
        _copiedPath = null;
        _toastUntil = DateTime.UtcNow.AddSeconds(1.6);
        Invalidate();
    }

    private string? _toast;
    private DateTime _toastUntil;

    /// <summary>The capture the last copy toast is about, whose copy button shows a tick meanwhile.</summary>
    private string? _copiedPath;

    private void DrawToast(Ui ui, double width, double height)
    {
        var visible = DateTime.UtcNow < _toastUntil;
        var shown = ui.Animate(Ui.Id("library.toast"), visible ? 1 : 0, Metrics.Motion);
        if (shown <= 0.01 || _toast is null) return;
        ui.Toast(_toast, width / 2, height - S(24), shown);
    }

    protected override void Dispose(bool disposing)
    {
        _touchpad?.Dispose();
        _touchpad = null;
        _clock?.Dispose();
        _clock = null;
        _dispatch?.Clear();
        ReleaseVisuals();
        base.Dispose(disposing);
    }
}
