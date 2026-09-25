using NexusShot.Core;

namespace NexusShot.Views;

/// <summary>Recording, resetting and reporting the global shortcuts the settings sheet edits.</summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// Turns the key press into a binding for the armed row.
    ///
    /// A bare modifier is not a shortcut, so those are ignored and recording stays armed until a real
    /// key arrives. Esc cancels, Backspace unbinds, Delete restores the default - and a single key such
    /// as F9 or PrtScn is a legitimate shortcut, so no modifier is required.
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

        var target = _settings.Hotkey(id);

        // Backspace unbinds - key 0 is never registered. Delete puts the default back.
        if (key == VIRTUAL_KEY.VK_BACK)
        {
            target.Modifiers = 0;
            target.Key = 0;
        }
        else if (key == VIRTUAL_KEY.VK_DELETE)
        {
            var restored = new AppSettings().Hotkey(id);
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

    /// <summary>Whether every binding already matches a fresh AppSettings.</summary>
    private bool HotkeysAreDefault()
    {
        var defaults = new AppSettings();
        return HotkeyIds.All.All(id => _settings.Hotkey(id).IsSameGesture(defaults.Hotkey(id)));
    }

    private void ResetHotkeys()
    {
        var defaults = new AppSettings();
        foreach (var id in HotkeyIds.All)
        {
            var current = _settings.Hotkey(id);
            var fallback = defaults.Hotkey(id);
            current.Modifiers = fallback.Modifiers;
            current.Key = fallback.Key;
        }

        _recordingHotkey = null;
        _hotkeyWarning = null;
        SaveSettings();
        HotkeysChanged?.Invoke();
        Invalidate();
    }

    /// <summary>Reports bindings that another application already owns, so the user can see which
    /// one clashed rather than wondering why nothing happens.</summary>
    public void ReportHotkeyConflicts(IReadOnlyList<HotkeyId> failed)
    {
        _hotkeyWarning = failed.Count == 0
            ? null
            : $"Another app already owns: {string.Join(", ", failed.Select(id => id.Title()))}.";
        Invalidate();
    }
}
