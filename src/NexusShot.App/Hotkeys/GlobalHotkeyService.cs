using NexusShot.App.Native;

namespace NexusShot.App.Hotkeys;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
    NoRepeat = 0x4000,
}

public enum NexusHotkey
{
    CaptureRegion = 1,
    CaptureFullScreen = 2,
    CaptureActiveWindow = 3,
    OpenMainWindow = 4,
}

public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, uint VirtualKey);

/// <summary>Registers application-wide hotkeys and emits them on the WinUI UI thread.</summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private readonly IntPtr _windowHandle;
    private readonly WindowMessageSubclass _subclass;
    private readonly HashSet<NexusHotkey> _registered = [];
    private bool _disposed;

    public GlobalHotkeyService(IntPtr windowHandle, WindowMessageSubclass subclass)
    {
        _windowHandle = windowHandle;
        _subclass = subclass;
        _subclass.MessageReceived += OnWindowMessage;
    }

    public event EventHandler<NexusHotkey>? HotkeyPressed;

    /// <summary>Maps the persisted bindings to registrable gestures, adding NoRepeat so a held
    /// key fires one capture rather than a stream of them.</summary>
    public static IReadOnlyDictionary<NexusHotkey, HotkeyGesture> GesturesFrom(Models.AppSettings settings) => new Dictionary<NexusHotkey, HotkeyGesture>
    {
        [NexusHotkey.CaptureRegion] = ToGesture(settings.CaptureRegionHotkey),
        [NexusHotkey.CaptureFullScreen] = ToGesture(settings.CaptureFullScreenHotkey),
        [NexusHotkey.CaptureActiveWindow] = ToGesture(settings.CaptureActiveWindowHotkey),
        [NexusHotkey.OpenMainWindow] = ToGesture(settings.OpenMainWindowHotkey),
    };

    private static HotkeyGesture ToGesture(Models.HotkeyBinding binding) =>
        new((HotkeyModifiers)binding.Modifiers | HotkeyModifiers.NoRepeat, binding.Key);

    /// <summary>
    /// Replaces all registrations, keeping every gesture that can be registered and reporting the
    /// ones that cannot (usually because another application owns the combination). Best-effort
    /// rather than transactional: one conflicting user-chosen shortcut must not disable the rest.
    /// </summary>
    public IReadOnlyList<NexusHotkey> Register(IReadOnlyDictionary<NexusHotkey, HotkeyGesture> gestures)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UnregisterAll();
        var failed = new List<NexusHotkey>();
        foreach (var (hotkey, gesture) in gestures)
        {
            if (NativeMethods.RegisterHotKey(_windowHandle, (int)hotkey, (uint)gesture.Modifiers, gesture.VirtualKey))
                _registered.Add(hotkey);
            else
                failed.Add(hotkey);
        }
        return failed;
    }

    public void UnregisterAll()
    {
        foreach (var hotkey in _registered) NativeMethods.UnregisterHotKey(_windowHandle, (int)hotkey);
        _registered.Clear();
    }

    private void OnWindowMessage(object? sender, NativeWindowMessageEventArgs args)
    {
        if (args.Message != NativeMethods.WmHotkey) return;
        var hotkey = (NexusHotkey)args.WParam.ToInt32();
        if (!_registered.Contains(hotkey)) return;
        args.Handled = true;
        HotkeyPressed?.Invoke(this, hotkey);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _subclass.MessageReceived -= OnWindowMessage;
        UnregisterAll();
        GC.SuppressFinalize(this);
    }
}

