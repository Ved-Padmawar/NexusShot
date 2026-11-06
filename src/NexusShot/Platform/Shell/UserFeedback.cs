using System.Runtime.InteropServices;

namespace NexusShot.Platform;

/// <summary>
/// How results and failures reach the user: a Windows notification through the tray, since most
/// happen with no window of ours in front. If the tray icon could not be added, a message box.
/// </summary>
internal static partial class UserFeedback
{
    /// <summary>Set by the app once its tray icon exists.</summary>
    public static TrayIcon? Tray { private get; set; }

    public static void Error(IntPtr owner, string message)
    {
        if (Tray?.Notify(message, error: true) == true) return;
        MessageBoxW(owner, message, "NexusShot", 0x10);
    }

    public static void Info(IntPtr owner, string message)
    {
        if (Tray?.Notify(message, error: false) == true) return;
        MessageBoxW(owner, message, "NexusShot", 0x40);
    }

    // Default to Cancel, so an accidental Enter never discards work.
    public static int ConfirmSave(IntPtr owner, string fileName) =>
        MessageBoxW(owner, $"Save changes to {fileName}?", "NexusShot", 0x3 | 0x20 | 0x200);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
