using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using NexusShot.Core;

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

    /// <summary>The chosen path, or null if the user cancelled. <paramref name="formats"/> are the
    /// types offered, the first preselected; the path's extension names the one picked.</summary>
    public static unsafe string? SaveImage(nint owner, string suggestedName, string? initialFolder,
        IReadOnlyList<ImageFormat> formats)
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

            var size = Marshal.SizeOf<COMDLG_FILTERSPEC>();
            var buffer = Marshal.AllocHGlobal(size * formats.Count);
            try
            {
                for (var i = 0; i < formats.Count; i++)
                    Marshal.StructureToPtr(Filter(formats[i]), buffer + i * size, false);
                dialog.SetFileTypes((uint)formats.Count, buffer);
                // Without it the dialog would not append the picked type's extension to a bare name.
                dialog.SetDefaultExtension(ImageFiles.ExtensionOf(formats[0])[1..]);
                dialog.SetFileName(suggestedName);

                if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder)
                    && ShellItems.FromPath(initialFolder, IID_IShellItem) is { } start)
                {
                    using (start)
                        dialog.SetFolder(start.Item);
                }

                dialog.Show(owner);
                dialog.GetResult(out var result);
                if (result == IntPtr.Zero) return null;

                using var selected = ShellItems.Adopt(result);
                selected.Item.GetDisplayName(SIGDN_FILESYSPATH, out var path);
                return path;
            }
            finally
            {
                for (var i = 0; i < formats.Count; i++)
                    Marshal.DestroyStructure<COMDLG_FILTERSPEC>(buffer + i * size);
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

    private static COMDLG_FILTERSPEC Filter(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => new() { pszName = "JPEG image", pszSpec = "*.jpg;*.jpeg" },
        ImageFormat.Bmp => new() { pszName = "BMP image", pszSpec = "*.bmp" },
        _ => new() { pszName = "PNG image", pszSpec = "*.png" },
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct COMDLG_FILTERSPEC
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pszName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszSpec;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr instance);

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
