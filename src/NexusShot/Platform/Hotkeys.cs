using System.Runtime.InteropServices;
using NexusShot.Core;

namespace NexusShot.Platform;

public enum HotkeyId
{
    CaptureRegion = 1,
    CaptureFullScreen = 2,
    CaptureActiveWindow = 3,
    OpenMainWindow = 4,
}

/// <summary>
/// Application-wide hotkeys.
///
/// NOREPEAT is always added: without it, holding the key streams captures rather than taking one.
/// Registration is best-effort - if the user picks a combination another application already owns,
/// that one binding fails and the rest still work, rather than the whole set being lost.
/// </summary>
public sealed class Hotkeys(IntPtr window) : IDisposable
{
    public const uint WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly HashSet<HotkeyId> _registered = [];

    /// <summary>The bindings that could not be registered, so the UI can say which ones clashed.</summary>
    public IReadOnlyList<HotkeyId> Apply(AppSettings settings)
    {
        UnregisterAll();

        var failed = new List<HotkeyId>();
        Register(HotkeyId.CaptureRegion, settings.CaptureRegionHotkey, failed);
        Register(HotkeyId.CaptureFullScreen, settings.CaptureFullScreenHotkey, failed);
        Register(HotkeyId.CaptureActiveWindow, settings.CaptureActiveWindowHotkey, failed);
        Register(HotkeyId.OpenMainWindow, settings.OpenMainWindowHotkey, failed);
        return failed;
    }

    private void Register(HotkeyId id, HotkeyBinding binding, List<HotkeyId> failed)
    {
        if (binding.Key == 0) return;

        if (RegisterHotKey(window, (int)id, binding.Modifiers | MOD_NOREPEAT, binding.Key))
            _registered.Add(id);
        else
            failed.Add(id);
    }

    /// <summary>Resolves a WM_HOTKEY wParam, or null if it is not one of ours.</summary>
    public HotkeyId? Resolve(long wParam)
    {
        var id = (HotkeyId)wParam;
        return _registered.Contains(id) ? id : null;
    }

    public void UnregisterAll()
    {
        foreach (var id in _registered) UnregisterHotKey(window, (int)id);
        _registered.Clear();
    }

    public void Dispose() => UnregisterAll();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
