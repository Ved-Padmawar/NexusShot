using System.Runtime.InteropServices;
using NexusShot.Platform;

namespace NexusShot.Views;

/// <summary>
/// The private message loop a full-screen picker runs until its window closes. The region picker and
/// the eyedropper are both modal acts - nothing else in the app can meaningfully happen while the
/// user is choosing - so each shows its window and blocks here.
/// </summary>
internal static partial class ModalLoop
{
    /// <summary>Covers <paramref name="desktop"/> with the window, brings it forward, and pumps its
    /// messages until it is destroyed.</summary>
    public static void Run(D2DRenderWindow window, RectInt desktop)
    {
        WindowInterop.SetWindowPos(window.Handle, IntPtr.Zero,
            desktop.X, desktop.Y, desktop.Width, desktop.Height, 0);
        window.Show();
        window.SetForeground();

        // Filtered at the API, so other messages stay queued for the main pump.
        var result = 0;
        while (window.IsWindow && (result = GetMessageW(out var message, window.Handle, 0, 0)) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }

        // WM_QUIT ignores the filter (a tray Exit): re-post it for the main loop. -1 is an error, not a quit.
        if (result == 0) PostQuitMessage(0);
    }

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static partial int GetMessageW(out MSG message, IntPtr window, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(ref MSG message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial IntPtr DispatchMessageW(ref MSG message);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int code);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }
}
