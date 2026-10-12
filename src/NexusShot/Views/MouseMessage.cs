using DirectN;
using NexusShot.Core;

namespace NexusShot.Views;

/// <summary>Reads the client position a mouse message carries. Signed: a captured pointer left of
/// or above the window reports negative coordinates.</summary>
internal static class MouseMessage
{
    public static Point ClientPoint(LPARAM lParam)
    {
        var value = lParam.Value.ToInt64();
        return new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
    }
}
