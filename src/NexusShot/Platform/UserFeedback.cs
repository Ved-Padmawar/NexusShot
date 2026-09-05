using System.Runtime.InteropServices;

namespace NexusShot.Platform;

internal static partial class UserFeedback
{
    public static void Error(IntPtr owner, string message) =>
        MessageBoxW(owner, message, "NexusShot", 0x10);

    // Default to Cancel, so an accidental Enter never discards work.
    public static int ConfirmSave(IntPtr owner, string fileName) =>
        MessageBoxW(owner, $"Save changes to {fileName}?", "NexusShot", 0x3 | 0x20 | 0x200);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
