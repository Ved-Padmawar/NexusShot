using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace NexusShot.Platform;

/// <summary>
/// The system folder picker: IFileDialog with FOS_PICKFOLDERS.
///
/// Created through CoCreateInstance rather than Activator.CreateInstance(Type.GetTypeFromCLSID),
/// which resolves the type through reflection and does not survive AOT trimming.
/// </summary>
public static partial class FolderPicker
{
    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const uint CLSCTX_INPROC_SERVER = 1;

    private const int ERROR_CANCELLED = unchecked((int)0x800704C7);

    private static readonly Guid CLSID_FileOpenDialog = new("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");
    private static readonly Guid IID_IFileOpenDialog = new("d57c7288-d4ad-4768-be02-9d969532d960");

    /// <summary>The chosen folder, or null if the user cancelled.</summary>
    public static unsafe string? Pick(nint owner, string? initial)
    {
        var hr = CoCreateInstance(CLSID_FileOpenDialog, IntPtr.Zero, CLSCTX_INPROC_SERVER,
            IID_IFileOpenDialog, out var raw);
        if (hr != 0 || raw == IntPtr.Zero) return null;

        var dialog = ComInterfaceMarshaller<IFileOpenDialog>.ConvertToManaged((void*)raw);
        if (dialog is null) return null;

        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);

            if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)
                && SHCreateItemFromParsingName(initial, IntPtr.Zero, IID_IShellItem, out var start) == 0)
            {
                dialog.SetFolder(start);
            }

            dialog.Show(owner);
            dialog.GetResult(out var item);
            item.GetDisplayName(SIGDN_FILESYSPATH, out var path);
            return path;
        }
        catch (COMException exception) when (exception.HResult == ERROR_CANCELLED)
        {
            return null;
        }
        finally
        {
            Marshal.Release(raw);
        }
    }

    private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr instance);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(
        string path, IntPtr bindContext, in Guid riid,
        [MarshalUsing(typeof(ComInterfaceMarshaller<IShellItem>))] out IShellItem item);

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
    internal partial interface IFileDialog
    {
        void Show(nint owner);
        void SetFileTypes(uint count, IntPtr filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName(string name);
        void GetFileName(out string name);
        void SetTitle(string title);
        void SetOkButtonLabel(string text);
        void SetFileNameLabel(string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem place, int where);
        void SetDefaultExtension(string extension);
        void Close(int result);
        void SetClientGuid(in Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
    internal partial interface IFileOpenDialog : IFileDialog
    {
        void GetResults(out IntPtr items);
        void GetSelectedItems(out IntPtr items);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    internal partial interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, in Guid handler, in Guid riid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint form, out string name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }
}
