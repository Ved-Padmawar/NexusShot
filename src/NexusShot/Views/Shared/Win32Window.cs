using DirectN;
using NexusShot.Core;

namespace NexusShot.Views;

/// <summary>The Win32 messages, styles and positioning flags the views' own windows use, declared once
/// and imported with <c>using static</c>.</summary>
internal static class Win32Window
{
    public const uint WmActivate = 0x0006;
    public const uint WmClose = 0x0010;
    public const uint WmSetCursor = 0x0020;
    public const uint WmKeyDown = 0x0100;
    public const uint WmKeyUp = 0x0101;
    public const uint WmChar = 0x0102;
    public const uint WmSysKeyDown = 0x0104;
    public const uint WmSysChar = 0x0106;
    public const uint WmTimer = 0x0113;
    public const uint WmMouseMove = 0x0200;
    public const uint WmLButtonDown = 0x0201;
    public const uint WmLButtonUp = 0x0202;
    public const uint WmRButtonDown = 0x0204;
    public const uint WmMButtonDown = 0x0207;
    public const uint WmMButtonUp = 0x0208;
    public const uint WmMouseWheel = 0x020A;
    public const uint WmMouseHWheel = 0x020E;
    public const uint WmCaptureChanged = 0x0215;
    public const uint WmMouseLeave = 0x02A3;

    public const uint WS_POPUP = 0x80000000;
    public const uint WS_EX_TOPMOST = 0x00000008;
    public const uint WS_EX_TRANSPARENT = 0x00000020;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_NOACTIVATE = 0x08000000;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_HIDEWINDOW = 0x0080;

    public static readonly IntPtr HWND_TOPMOST = new(-1);

    /// <summary>The client position a mouse message carries. Signed: a captured pointer left of or
    /// above the window reports negative coordinates.</summary>
    public static Point ClientPoint(LPARAM lParam)
    {
        var value = lParam.Value.ToInt64();
        return new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
    }
}
