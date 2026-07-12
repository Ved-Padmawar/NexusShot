using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace NexusShot.Platform;

/// <summary>The system Save dialog, for Save As.</summary>
public static partial class FilePicker
{
    private const uint FOS_OVERWRITEPROMPT = 0x00000002;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const uint CLSCTX_INPROC_SERVER = 1;

    private const int ERROR_CANCELLED = unchecked((int)0x800704C7);

    private static readonly Guid CLSID_FileSaveDialog = new("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B");
    private static readonly Guid IID_IFileSaveDialog = new("84bccd23-5fde-4cdb-aea4-af64b83d78ab");
    private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    /// <summary>The chosen path, or null if the user cancelled.</summary>
    public static unsafe string? SavePng(nint owner, string suggestedName, string? initialFolder)
    {
        var hr = CoCreateInstance(CLSID_FileSaveDialog, IntPtr.Zero, CLSCTX_INPROC_SERVER,
            IID_IFileSaveDialog, out var raw);
        if (hr != 0 || raw == IntPtr.Zero) return null;

        var dialog = ComInterfaceMarshaller<IFileSaveDialog>.ConvertToManaged((void*)raw);
        if (dialog is null) return null;

        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FOS_OVERWRITEPROMPT | FOS_FORCEFILESYSTEM);

            var filter = new COMDLG_FILTERSPEC { pszName = "PNG image", pszSpec = "*.png" };
            var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<COMDLG_FILTERSPEC>());
            try
            {
                Marshal.StructureToPtr(filter, buffer, false);
                dialog.SetFileTypes(1, buffer);
                dialog.SetDefaultExtension("png");
                dialog.SetFileName(suggestedName);

                if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder)
                    && SHCreateItemFromParsingName(initialFolder, IntPtr.Zero, IID_IShellItem, out var start) == 0)
                {
                    dialog.SetFolder(start);
                }

                dialog.Show(owner);
                dialog.GetResult(out var item);
                item.GetDisplayName(SIGDN_FILESYSPATH, out var path);
                return path;
            }
            finally
            {
                Marshal.DestroyStructure<COMDLG_FILTERSPEC>(buffer);
                Marshal.FreeHGlobal(buffer);
            }
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct COMDLG_FILTERSPEC
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pszName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszSpec;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr instance);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(
        string path, IntPtr bindContext, in Guid riid,
        [MarshalUsing(typeof(ComInterfaceMarshaller<FolderPicker.IShellItem>))]
        out FolderPicker.IShellItem item);

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("84bccd23-5fde-4cdb-aea4-af64b83d78ab")]
    internal partial interface IFileSaveDialog : FolderPicker.IFileDialog
    {
        void SetSaveAsItem(FolderPicker.IShellItem item);
        void SetProperties(IntPtr store);
        void SetCollectedProperties(IntPtr list, int appendDefault);
        void GetProperties(out IntPtr store);
        void ApplyProperties(FolderPicker.IShellItem item, IntPtr store, nint owner, IntPtr sink);
    }
}
