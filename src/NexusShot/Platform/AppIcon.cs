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

    /// <summary>The first icon in the exe's resource table. The csproj's ApplicationIcon puts ours
    /// there, and 1 is where the linker writes it.</summary>
    private const int IconResourceId = 1;

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
