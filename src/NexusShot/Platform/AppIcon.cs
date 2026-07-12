using System.Runtime.InteropServices;

namespace NexusShot.Platform;

/// <summary>
/// The app icon. The taskbar reads a window's ICON_SMALL and Alt+Tab reads its ICON_BIG, so a window
/// needs both - setting only the small one leaves Alt+Tab blank. Loaded from the exe's own resource
/// table, not a file path, which would depend on the working directory.
/// </summary>
public static class AppIcon
{
    private const uint WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;

    private const int IMAGE_ICON = 1;
    private const uint LR_SHARED = 0x8000;

    private const int SM_CXSMICON = 49;
    private const int SM_CXICON = 11;

    /// <summary>
    /// The ordinal ApplicationIcon assigns to the embedded icon group.
    ///
    /// Not 1. Resource 1 is not the icon group, so LoadImage returns nothing - the tray icon is then
    /// invisible and the taskbar falls back to a stretched default.
    /// </summary>
    private const int IconResourceId = 32512;   // IDI_APPLICATION

    /// <summary>The tray-sized icon. Shared, so it must not be destroyed.</summary>
    public static IntPtr Small { get; } = Load(GetSystemMetrics(SM_CXSMICON));

    /// <summary>The Alt+Tab-sized icon.</summary>
    public static IntPtr Large { get; } = Load(GetSystemMetrics(SM_CXICON));

    /// <summary>
    /// Gives a window both icons.
    ///
    /// Both, not one: the taskbar and Alt+Tab read different slots, and a window with only
    /// ICON_SMALL set shows a blank square in the Alt+Tab switcher.
    /// </summary>
    public static void Apply(IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        if (Small != IntPtr.Zero) SendMessageW(window, WM_SETICON, ICON_SMALL, Small);
        if (Large != IntPtr.Zero) SendMessageW(window, WM_SETICON, ICON_BIG, Large);
    }

    /// <summary>
    /// The Alt+Tab and taskbar icon, without the small one the caption draws.
    ///
    /// WS_EX_DLGMODALFRAME suppresses the caption icon; the big icon still feeds the shell, so the
    /// switcher and the taskbar keep theirs.
    /// </summary>
    public static void ApplyLargeOnly(IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        if (Large != IntPtr.Zero) SendMessageW(window, WM_SETICON, ICON_BIG, Large);

        // D2DRenderWindow may already have populated ICON_SMALL before OnCreated runs. Merely not
        // assigning our own small icon therefore does not remove the caption logo; clear the slot
        // explicitly while leaving ICON_BIG available to Alt+Tab and the taskbar.
        SendMessageW(window, WM_SETICON, ICON_SMALL, IntPtr.Zero);

        var style = GetWindowLongPtrW(window, GWL_EXSTYLE);
        SetWindowLongPtrW(window, GWL_EXSTYLE, style | WS_EX_DLGMODALFRAME);

        // Frame styles only take effect on a recomputed frame.
        SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    /// <summary>Blanks the caption text. The window keeps its icon for the shell.</summary>
    public static void ClearCaption(IntPtr window)
    {
        if (window != IntPtr.Zero) SetWindowTextW(window, string.Empty);
    }

    private const int GWL_EXSTYLE = -20;
    private const nint WS_EX_DLGMODALFRAME = 0x00000001;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(IntPtr window, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowTextW(IntPtr window, string text);

    private static IntPtr Load(int size)
    {
        var module = GetModuleHandleW(null);
        if (module == IntPtr.Zero) return IntPtr.Zero;

        // LR_SHARED: the system owns the handle, so it outlives us and must not be destroyed.
        return LoadImageW(module, IconResourceId, IMAGE_ICON, size, size, LR_SHARED);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, int wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(
        IntPtr instance, nint name, int type, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);
}
