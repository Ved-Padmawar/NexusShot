using System.Runtime.InteropServices;

namespace NexusShot.Platform;

/// <summary>
/// The app icon. The taskbar reads a window's ICON_SMALL and Alt+Tab reads its ICON_BIG, so a window
/// needs both - setting only the small one leaves Alt+Tab blank. Loaded from the exe's own resource
/// table, not a file path, which would depend on the working directory.
/// </summary>
public static partial class AppIcon
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
    public static IntPtr Small { get; } = Load(WindowInterop.GetSystemMetrics(SM_CXSMICON));

    /// <summary>The Alt+Tab-sized icon.</summary>
    public static IntPtr Large { get; } = Load(WindowInterop.GetSystemMetrics(SM_CXICON));

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

        // D2DRenderWindow may have set ICON_SMALL already; clear it so the caption shows no logo.
        SendMessageW(window, WM_SETICON, ICON_SMALL, IntPtr.Zero);

        var style = WindowInterop.GetWindowLongPtrW(window, GWL_EXSTYLE);
        WindowInterop.SetWindowLongPtrW(window, GWL_EXSTYLE, style | WS_EX_DLGMODALFRAME);

        // Frame styles only take effect on a recomputed frame.
        WindowInterop.SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    private const int GWL_EXSTYLE = -20;
    private const nint WS_EX_DLGMODALFRAME = 0x00000001;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private static IntPtr Load(int size)
    {
        var module = GetModuleHandleW(null);
        if (module == IntPtr.Zero) return IntPtr.Zero;

        // LR_SHARED: the system owns the handle, so it outlives us and must not be destroyed.
        return LoadImageW(module, IconResourceId, IMAGE_ICON, size, size, LR_SHARED);
    }

    /// <summary>The icon at exactly <paramref name="size"/> pixels, for drawing. Not shared - Windows
    /// shares only standard sizes - so the caller passes it to <see cref="Destroy"/>.</summary>
    public static IntPtr LoadOwned(int size)
    {
        var module = GetModuleHandleW(null);
        return module == IntPtr.Zero ? IntPtr.Zero : LoadImageW(module, IconResourceId, IMAGE_ICON, size, size, 0);
    }

    public static void Destroy(IntPtr icon)
    {
        if (icon != IntPtr.Zero) DestroyIcon(icon);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr icon);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial IntPtr SendMessageW(IntPtr window, uint message, int wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW")]
    private static partial IntPtr LoadImageW(
        IntPtr instance, nint name, int type, int cx, int cy, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandleW(string? name);
}
