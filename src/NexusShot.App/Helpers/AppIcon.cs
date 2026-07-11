using Microsoft.UI;
using Microsoft.UI.Windowing;
using NexusShot.App.Native;

namespace NexusShot.App.Helpers;

/// <summary>
/// Loads the application icon out of the executable's own resources.
///
/// The <c>ApplicationIcon</c> build property embeds nexus-shot.ico into the exe, which is what
/// Explorer shows for the file. It does <em>not</em> set an icon on any window: the taskbar reads
/// its icon from the HWND (via WM_SETICON), so a window that never calls <see cref="Apply"/> shows
/// the generic WinUI placeholder no matter what the exe contains.
/// </summary>
internal static class AppIcon
{
    /// <summary>Resource ordinal that <c>ApplicationIcon</c> assigns to the embedded icon group.</summary>
    private static readonly IntPtr ResourceId = (IntPtr)NativeMethods.IdiApplication;

    /// <summary>
    /// Gives <paramref name="window"/> the app icon, at the size Windows asks for.
    ///
    /// Uses the HICON overload rather than <c>AppWindow.SetIcon(string)</c>: the string overload
    /// resolves a filesystem path, which in an unpackaged app depends on the working directory and
    /// breaks whenever the exe is launched from elsewhere. The module's resource table always works.
    ///
    /// <c>AppWindow.SetIcon</c> alone is not enough for Alt-Tab: it reads the window's ICON_BIG,
    /// which SetIcon does not reliably populate, so the switcher falls back to a blank tile.
    /// Both sizes are therefore also pushed onto the HWND directly via WM_SETICON.
    /// </summary>
    public static void Apply(AppWindow window, IntPtr handle)
    {
        var large = Load(NativeMethods.SmCxIcon, NativeMethods.SmCyIcon);
        if (large == IntPtr.Zero) return;
        window.SetIcon(Win32Interop.GetIconIdFromIcon(large));

        // LR_SHARED handles: owned by the resource table, safe to hand to the window untracked.
        NativeMethods.SendMessage(handle, NativeMethods.WmSetIcon, NativeMethods.IconBig, large);
        var small = LoadSmall();
        if (small != IntPtr.Zero)
            NativeMethods.SendMessage(handle, NativeMethods.WmSetIcon, NativeMethods.IconSmall, small);
    }

    /// <summary>The small icon, sized for the notification area at the current DPI.</summary>
    public static IntPtr LoadSmall() => Load(NativeMethods.SmCxSmIcon, NativeMethods.SmCySmIcon);

    /// <summary>
    /// Loads the embedded icon at the size named by the given system metrics. Returns
    /// <see cref="IntPtr.Zero"/> if the resource is missing, so callers can fall back.
    /// </summary>
    private static IntPtr Load(int widthMetric, int heightMetric)
    {
        var module = NativeMethods.GetModuleHandle(null);
        if (module == IntPtr.Zero) return IntPtr.Zero;

        // LR_SHARED: the handle belongs to the module's resource table and must not be destroyed.
        return NativeMethods.LoadImage(
            module,
            ResourceId,
            NativeMethods.ImageIcon,
            NativeMethods.GetSystemMetrics(widthMetric),
            NativeMethods.GetSystemMetrics(heightMetric),
            NativeMethods.LrDefaultColor | NativeMethods.LrShared);
    }
}
