using NexusShot.App.Enums;

namespace NexusShot.App.Models;

/// <summary>
/// A persisted global shortcut: raw Win32 modifier flags (MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4,
/// MOD_WIN=8) plus a virtual-key code. Modifier-less bindings are valid — a single key like F9 or
/// PrtScn can be a capture shortcut, as in the system snipping tool.
/// </summary>
public sealed class HotkeyBinding
{
    public uint Modifiers { get; set; }
    public uint Key { get; set; }

    public HotkeyBinding Clone() => new() { Modifiers = Modifiers, Key = Key };

    public bool IsSameGesture(HotkeyBinding other) => Modifiers == other.Modifiers && Key == other.Key;
}

/// <summary>Application settings persisted by the storage service.</summary>
public sealed class AppSettings
{
    private const uint ControlShift = 0x0002 | 0x0004;

    public string ScreenshotFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "NexusShot");
    public CaptureMode DefaultCaptureMode { get; set; } = CaptureMode.Region;
    /// <summary>Seconds before a floating preview auto-dismisses. Zero keeps it until acted on.</summary>
    public int PreviewDismissSeconds { get; set; }
    public bool CopyToClipboardAutomatically { get; set; } = true;
    public bool SaveAutomatically { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.System;

    public HotkeyBinding CaptureRegionHotkey { get; set; } = new() { Modifiers = ControlShift, Key = 'S' };
    public HotkeyBinding CaptureFullScreenHotkey { get; set; } = new() { Modifiers = ControlShift, Key = 'F' };
    public HotkeyBinding CaptureActiveWindowHotkey { get; set; } = new() { Modifiers = ControlShift, Key = 'W' };
    public HotkeyBinding OpenMainWindowHotkey { get; set; } = new() { Modifiers = ControlShift, Key = 'N' };
}
