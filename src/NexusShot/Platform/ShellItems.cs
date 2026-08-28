using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace NexusShot.Platform;

/// <summary>
/// Deterministic lifetime for the IShellItem references the file dialogs hand out.
///
/// ComObject.FinalRelease only releases a wrapper created with CreateObjectFlags.UniqueInstance,
/// and the source-generated marshaller never uses that flag - so a marshalled `out IShellItem`
/// cannot be released on demand. Wrapping the raw pointer here with UniqueInstance gives a
/// reference that can be dropped when the dialog closes.
/// </summary>
internal static partial class ShellItems
{
    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>Wraps a raw IShellItem pointer, taking ownership of <paramref name="pointer"/>.</summary>
    internal static ShellItemRef Adopt(IntPtr pointer) => new(Wrappers, pointer);

    /// <summary>The shell item for a filesystem path, or null when the path cannot be resolved.</summary>
    internal static ShellItemRef? FromPath(string path, in Guid riid) =>
        SHCreateItemFromParsingName(path, IntPtr.Zero, riid, out var pointer) == 0 && pointer != IntPtr.Zero
            ? Adopt(pointer)
            : null;

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(
        string path, IntPtr bindContext, in Guid riid, out IntPtr item);
}

/// <summary>An owned IShellItem reference: both the wrapper and the underlying pointer are
/// released on dispose.</summary>
internal readonly struct ShellItemRef : IDisposable
{
    private readonly object _wrapper;
    private readonly IntPtr _pointer;

    internal ShellItemRef(StrategyBasedComWrappers wrappers, IntPtr pointer)
    {
        _pointer = pointer;
        _wrapper = wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
    }

    internal FolderPicker.IShellItem Item => (FolderPicker.IShellItem)_wrapper;

    public void Dispose()
    {
        if (_wrapper is System.Runtime.InteropServices.Marshalling.ComObject com)
            com.FinalRelease();
        if (_pointer != IntPtr.Zero)
            Marshal.Release(_pointer);
    }
}
