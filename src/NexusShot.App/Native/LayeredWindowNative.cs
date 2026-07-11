using System.Runtime.InteropServices;

namespace NexusShot.App.Native;

/// <summary>
/// Win32 surface for a layered, per-pixel-alpha overlay window. WinUI 3 cannot host XAML in a
/// transparent window, so the region selector is a plain Win32 window composited with
/// <see cref="UpdateLayeredWindow"/>.
/// </summary>
internal static class LayeredWindowNative
{
    internal const int CsHRedraw = 0x0002;
    internal const int CsVRedraw = 0x0001;

    internal const uint WsPopup = 0x80000000;
    internal const uint WsVisible = 0x10000000;

    internal const int WsExLayered = 0x00080000;
    internal const int WsExTopmost = 0x00000008;
    internal const int WsExToolWindow = 0x00000080;
    internal const int WsExNoActivate = 0x08000000;

    internal const uint WmDestroy = 0x0002;
    internal const uint WmTimer = 0x0113;
    internal const uint WmClose = 0x0010;
    internal const uint WmSetCursor = 0x0020;
    internal const uint WmKeyDown = 0x0100;
    internal const uint WmMouseMove = 0x0200;
    internal const uint WmLButtonDown = 0x0201;
    internal const uint WmLButtonUp = 0x0202;
    internal const uint WmRButtonDown = 0x0204;

    internal const int VkEscape = 0x1B;

    internal const uint UlwAlpha = 0x00000002;
    internal const byte AcSrcOver = 0x00;
    internal const byte AcSrcAlpha = 0x01;

    internal const int BiRgb = 0;
    internal const uint DibRgbColors = 0;

    internal const int IdcCross = 32515;

    internal const int SmXVirtualScreen = 76;
    internal const int SmYVirtualScreen = 77;
    internal const int SmCxVirtualScreen = 78;
    internal const int SmCyVirtualScreen = 79;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Size { public int Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public Point pt;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate IntPtr WndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassEx(ref WndClassEx wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowEx(
        int exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr DefWindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool UpdateLayeredWindow(
        IntPtr hWnd, IntPtr destinationDc, ref Point destinationPoint, ref Size size,
        IntPtr sourceDc, ref Point sourcePoint, uint colorKey, ref BlendFunction blend, uint flags);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr LoadCursor(IntPtr instance, int cursorName);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetIconInfo(IntPtr icon, out IconInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CreateIconIndirect(ref IconInfo info);

    [DllImport("user32.dll")]
    internal static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    internal static extern bool DestroyCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    internal static extern bool GetMessage(out Msg message, IntPtr hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll")]
    internal static extern IntPtr DispatchMessage(ref Msg message);

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateDIBSection(
        IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(IntPtr dc);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetTimer(IntPtr hWnd, IntPtr id, uint elapseMilliseconds, IntPtr callback);

    [DllImport("user32.dll")]
    internal static extern bool KillTimer(IntPtr hWnd, IntPtr id);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandle(string? moduleName);
}
