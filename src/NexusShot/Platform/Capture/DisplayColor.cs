using System.Runtime.InteropServices;

namespace NexusShot.Platform;

/// <summary>A monitor that is in HDR mode: where it sits on the desktop, and the brightness the user
/// gave SDR content on it.</summary>
internal readonly record struct HdrDisplay(RectInt Bounds, float SdrWhiteNits)
{
    public bool Contains(int x, int y) => x >= Bounds.X && x < Bounds.Right && y >= Bounds.Y && y < Bounds.Bottom;
}

/// <summary>
/// Which monitors are in HDR mode right now, asked of the display configuration each time: HDR is a
/// per-monitor switch the user flips in Settings, and so is the SDR brightness slider.
/// </summary>
internal static unsafe partial class DisplayColor
{
    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    private const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;
    private const uint GET_ADVANCED_COLOR_INFO = 9;
    private const uint GET_SDR_WHITE_LEVEL = 11;
    private const uint AdvancedColorEnabled = 1 << 1;

    /// <summary>The SDR white level is reported in thousandths of the 80-nit reference.</summary>
    private const float NitsPerLevelUnit = 80f / 1000f;

    public static List<HdrDisplay> HdrDisplays()
    {
        var found = new List<HdrDisplay>();
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0) return found;

        var paths = new PathInfo[pathCount];
        var modes = new ModeInfo[modeCount];
        fixed (PathInfo* pathBuffer = paths)
        fixed (ModeInfo* modeBuffer = modes)
        {
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, pathBuffer, ref modeCount, modeBuffer, IntPtr.Zero) != 0)
                return found;
        }

        for (var i = 0; i < pathCount; i++)
        {
            var path = paths[i];
            if (path.SourceModeIndex >= modeCount || modes[path.SourceModeIndex].InfoType != DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
                continue;
            if (!IsHdr(path.TargetAdapter, path.TargetId, out var whiteNits)) continue;

            var source = modes[path.SourceModeIndex];
            found.Add(new HdrDisplay(new RectInt(source.X, source.Y, (int)source.Width, (int)source.Height), whiteNits));
        }
        return found;
    }

    private static bool IsHdr(Luid adapter, uint target, out float whiteNits)
    {
        whiteNits = 0;
        var color = new AdvancedColorInfo { Header = new(GET_ADVANCED_COLOR_INFO, (uint)sizeof(AdvancedColorInfo), adapter, target) };
        if (DisplayConfigGetDeviceInfo(ref color) != 0 || (color.Flags & AdvancedColorEnabled) == 0) return false;

        var white = new SdrWhiteLevel { Header = new(GET_SDR_WHITE_LEVEL, (uint)sizeof(SdrWhiteLevel), adapter, target) };
        if (DisplayConfigGetDeviceInfo(ref white) != 0 || white.Level == 0) return false;

        whiteNits = white.Level * NitsPerLevelUnit;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo
    {
        public Luid SourceAdapter;
        public uint SourceId;
        public uint SourceModeIndex;
        public uint SourceStatus;
        public Luid TargetAdapter;
        public uint TargetId;
        public uint TargetModeIndex;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public uint RefreshNumerator;
        public uint RefreshDenominator;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint TargetStatus;
        public uint Flags;
    }

    /// <summary>Only the source-mode view of the union is read; the size pads to the native 64 bytes.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct ModeInfo
    {
        public uint InfoType;
        public uint Id;
        public Luid Adapter;
        public uint Width;
        public uint Height;
        public uint PixelFormat;
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader(uint type, uint size, Luid adapter, uint id)
    {
        public uint Type = type;
        public uint Size = size;
        public Luid Adapter = adapter;
        public uint Id = id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedColorInfo
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SdrWhiteLevel
    {
        public DeviceInfoHeader Header;
        public uint Level;
    }

    [LibraryImport("user32.dll")]
    private static partial int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [LibraryImport("user32.dll")]
    private static partial int QueryDisplayConfig(uint flags, ref uint pathCount, PathInfo* paths,
        ref uint modeCount, ModeInfo* modes, IntPtr topology);

    [LibraryImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static partial int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo packet);

    [LibraryImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static partial int DisplayConfigGetDeviceInfo(ref SdrWhiteLevel packet);
}
