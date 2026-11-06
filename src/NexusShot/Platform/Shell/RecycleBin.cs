using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace NexusShot.Platform;

/// <summary>Deletes through the shell, so a capture removed by mistake can be restored from the
/// Recycle Bin rather than being gone.</summary>
internal static partial class RecycleBin
{
    private const uint FOF_SILENT = 0x0004;
    private const uint FOF_NOCONFIRMATION = 0x0010;
    private const uint FOF_NOERRORUI = 0x0400;
    private const uint FOF_WANTNUKEWARNING = 0x4000;
    private const uint FOFX_RECYCLEONDELETE = 0x00080000;
    private const uint TSF_DELETE_RECYCLE_IF_POSSIBLE = 0x80;
    private const uint CLSCTX_INPROC_SERVER = 1;
    private const int E_ABORT = unchecked((int)0x80004004);

    private static readonly Guid CLSID_FileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");
    private static readonly Guid IID_IFileOperation = new("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8");
    private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    /// <summary>Deletes a file the user asked to delete. Where it cannot be recycled the shell asks
    /// before destroying it, since our prompt promised recycling; declining keeps it. Throws
    /// <see cref="IOException"/> when the file did not go.</summary>
    public static void Delete(string path) => Run(path, FOF_WANTNUKEWARNING, sink: null);

    /// <summary>Recycles a file or leaves it, never asking: housekeeping nobody is watching must not
    /// destroy what the Recycle Bin cannot take. Throws <see cref="IOException"/> when the file did
    /// not go.</summary>
    public static void Recycle(string path) => Run(path, 0, new RecycleOnly());

    private static unsafe void Run(string path, uint flags, IFileOperationProgressSink? sink)
    {
        if (CoCreateInstance(CLSID_FileOperation, IntPtr.Zero, CLSCTX_INPROC_SERVER, IID_IFileOperation, out var raw) != 0)
            throw new IOException("The shell file operation is unavailable.");
        try
        {
            var operation = ComInterfaceMarshaller<IFileOperation>.ConvertToManaged((void*)raw)!;
            using var item = ShellItems.FromPath(path, IID_IShellItem) ?? throw new IOException("The file could not be found.");
            operation.SetOperationFlags(FOFX_RECYCLEONDELETE | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | flags);
            operation.DeleteItem(item.Pointer, sink);
            var result = operation.PerformOperations();
            operation.GetAnyOperationsAborted(out var aborted);
            if (result != 0 || aborted != 0)
                throw new IOException($"The shell did not delete the file (0x{result:X}).");
        }
        catch (COMException exception)
        {
            throw new IOException("The shell could not delete the file.", exception);
        }
        finally
        {
            Marshal.Release(raw);
        }
    }

    /// <summary>Cancels a delete the shell would carry out permanently. It clears
    /// TSF_DELETE_RECYCLE_IF_POSSIBLE on an item the Recycle Bin cannot take: a drive without one, a
    /// bin turned off or too small, a file too large.</summary>
    [GeneratedComClass]
    internal sealed partial class RecycleOnly : IFileOperationProgressSink
    {
        public int PreDeleteItem(uint flags, IntPtr item) =>
            (flags & TSF_DELETE_RECYCLE_IF_POSSIBLE) != 0 ? 0 : E_ABORT;

        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint flags, IntPtr item, string? newName) => 0;
        public int PostRenameItem(uint flags, IntPtr item, string? newName, int result, IntPtr created) => 0;
        public int PreMoveItem(uint flags, IntPtr item, IntPtr folder, string? newName) => 0;
        public int PostMoveItem(uint flags, IntPtr item, IntPtr folder, string? newName, int result, IntPtr created) => 0;
        public int PreCopyItem(uint flags, IntPtr item, IntPtr folder, string? newName) => 0;
        public int PostCopyItem(uint flags, IntPtr item, IntPtr folder, string? newName, int result, IntPtr created) => 0;
        public int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr created) => 0;
        public int PreNewItem(uint flags, IntPtr folder, string? newName) => 0;
        public int PostNewItem(uint flags, IntPtr folder, string? newName, string? template, uint attributes, int result, IntPtr created) => 0;
        public int UpdateProgress(uint total, uint done) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr instance);

    /// <summary>Only flags, DeleteItem and the run are used; the rest keep the vtable in order.</summary>
    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
    internal partial interface IFileOperation
    {
        void Advise(IFileOperationProgressSink sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage(string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(nint owner);
        void ApplyPropertiesToItem(IntPtr item);
        void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IntPtr item, string newName, IFileOperationProgressSink? sink);
        void RenameItems(IntPtr items, string newName);
        void MoveItem(IntPtr item, IntPtr folder, string? newName, IFileOperationProgressSink? sink);
        void MoveItems(IntPtr items, IntPtr folder);
        void CopyItem(IntPtr item, IntPtr folder, string? newName, IFileOperationProgressSink? sink);
        void CopyItems(IntPtr items, IntPtr folder);
        void DeleteItem(IntPtr item, IFileOperationProgressSink? sink);
        void DeleteItems(IntPtr items);
        void NewItem(IntPtr folder, uint attributes, string name, string? template, IFileOperationProgressSink? sink);
        [PreserveSig] int PerformOperations();
        void GetAnyOperationsAborted(out int aborted);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2")]
    internal partial interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IntPtr item, string? newName);
        [PreserveSig] int PostRenameItem(uint flags, IntPtr item, string? newName, int result, IntPtr created);
        [PreserveSig] int PreMoveItem(uint flags, IntPtr item, IntPtr folder, string? newName);
        [PreserveSig] int PostMoveItem(uint flags, IntPtr item, IntPtr folder, string? newName, int result, IntPtr created);
        [PreserveSig] int PreCopyItem(uint flags, IntPtr item, IntPtr folder, string? newName);
        [PreserveSig] int PostCopyItem(uint flags, IntPtr item, IntPtr folder, string? newName, int result, IntPtr created);
        [PreserveSig] int PreDeleteItem(uint flags, IntPtr item);
        [PreserveSig] int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr created);
        [PreserveSig] int PreNewItem(uint flags, IntPtr folder, string? newName);
        [PreserveSig] int PostNewItem(uint flags, IntPtr folder, string? newName, string? template, uint attributes, int result, IntPtr created);
        [PreserveSig] int UpdateProgress(uint total, uint done);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }
}
