using System.Runtime.InteropServices;
using NexusShot.Core;

namespace NexusShot.Platform;

/// <summary>
/// Application-wide hotkeys.
///
/// NOREPEAT is always added: without it, holding the key streams captures rather than taking one.
/// Registration is best-effort - if the user picks a combination another application already owns,
/// that one binding fails and the rest still work, rather than the whole set being lost.
/// </summary>
public sealed partial class Hotkeys(IntPtr window) : IDisposable
{
    public const uint WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly HashSet<HotkeyId> _registered = [];

    /// <summary>The bindings that could not be registered, so the UI can say which ones clashed.</summary>
    public IReadOnlyList<HotkeyId> Apply(AppSettings settings)
    {
        UnregisterAll();

        var failed = new List<HotkeyId>();
        foreach (var id in HotkeyIds.All) Register(id, settings.Hotkey(id), failed);
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

    /// <summary>The id <see cref="ClaimEscape"/> registers under: the top of an application's range,
    /// clear of every <see cref="HotkeyId"/>.</summary>
    public const int EscapeId = 0xBFFF;

    /// <summary>
    /// Takes Esc system-wide until disposed, delivered to <paramref name="window"/> as WM_HOTKEY. For
    /// a full-screen picker Windows may have refused the foreground - a timed capture opens it from a
    /// timer, with no fresh input to earn it - so its own key messages would never arrive. If another
    /// app holds Esc, the picker is left with its keyboard focus alone.
    /// </summary>
    public static EscapeClaim ClaimEscape(IntPtr window) =>
        new(window, RegisterHotKey(window, EscapeId, MOD_NOREPEAT, (uint)VIRTUAL_KEY.VK_ESCAPE));

    public readonly struct EscapeClaim(IntPtr window, bool held) : IDisposable
    {
        public void Dispose()
        {
            if (held) UnregisterHotKey(window, EscapeId);
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr window, int id);
}
