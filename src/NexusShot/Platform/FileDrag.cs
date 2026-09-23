using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using NexusShot.Core;

namespace NexusShot.Platform;

/// <summary>
/// Drags a file out of the app.
///
/// The data object is the shell's own, the one Explorer drags: a hand-built CF_HDROP-only object was
/// refused by targets that read the shell ID list or file contents. CF_UNICODETEXT is added so a
/// text field, which cannot take a file, gets the path.
///
/// The COM interfaces are declared here rather than taken from System.Runtime.InteropServices
/// .ComTypes: those are marshalled by the classic runtime marshaller, which AOT trims away
/// (IL2050/SYSLIB1095), and the drag would then fail silently in the shipped exe.
/// </summary>
public static partial class FileDrag
{
    private const uint DROPEFFECT_NONE = 0;
    private const uint DROPEFFECT_COPY = 1;
    private const uint DROPEFFECT_LINK = 4;
    private const uint CLSCTX_INPROC_SERVER = 1;
    private const int S_OK = 0;
    private const int DRAGDROP_S_DROP = 0x00040100;
    private const int DRAGDROP_S_CANCEL = 0x00040101;
    private const int DRAGDROP_S_USEDEFAULTCURSORS = 0x00040102;

    private const short CF_UNICODETEXT = 13;
    private const uint TYMED_HGLOBAL = 1;
    private const uint GMEM_MOVEABLE = 0x0002;

    private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly Guid IID_IDataObject = new("0000010e-0000-0000-C000-000000000046");
    private static readonly Guid BHID_DataObject = new("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");

    /// <summary>
    /// Runs the drag, returning true when it ended in a drop rather than a cancel. Blocks until then,
    /// and must be called on the thread that owns the window: DoDragDrop reads the live mouse state,
    /// and a thread that has none sees the button as released and ends the drag before it starts.
    ///
    /// <paramref name="image"/> is the picture that follows the cursor.
    /// </summary>
    public static bool Start(string path, DragImage? image = null)
    {
        if (!File.Exists(path)) return false;

        var data = IntPtr.Zero;
        var source = IntPtr.Zero;

        try
        {
            if (ShellItems.FromPath(path, IID_IShellItem) is not { } item) return false;
            using (item)
                item.Item.BindToHandler(IntPtr.Zero, BHID_DataObject, IID_IDataObject, out data);
            if (data == IntPtr.Zero) return false;

            AttachText(data, path);

            unsafe
            {
                source = (IntPtr)ComInterfaceMarshaller<IDropSource>.ConvertToUnmanaged(new DropSource());
            }
            if (source == IntPtr.Zero) return false;

            if (image is not null) AttachImage(data, image);

            // Never move: that would take the capture out of its folder and out of history.
            var hr = DoDragDrop(data, source, DROPEFFECT_COPY | DROPEFFECT_LINK, out var effect);

            // Anything else - a cancel, an Escape, a target that refused it - is not a drop.
            return hr == DRAGDROP_S_DROP && effect != DROPEFFECT_NONE;
        }
        catch (Exception exception) when (exception is COMException or IOException or UnauthorizedAccessException)
        {
            Log.Error("drag.start", exception, path);
            return false;
        }
        finally
        {
            if (data != IntPtr.Zero) Marshal.Release(data);
            if (source != IntPtr.Zero) Marshal.Release(source);
        }
    }

    /// <summary>Best-effort: a data object that refuses the extra format still drags the file.</summary>
    private static unsafe void AttachText(IntPtr data, string path)
    {
        var block = BuildText(path);
        if (block == IntPtr.Zero) return;

        var target = ComInterfaceMarshaller<IDataObject>.ConvertToManaged((void*)data);
        var format = new FORMATETC
        {
            cfFormat = CF_UNICODETEXT,
            dwAspect = 1,          // DVASPECT_CONTENT
            lindex = -1,
            tymed = TYMED_HGLOBAL,
        };
        var medium = new STGMEDIUM { tymed = TYMED_HGLOBAL, unionmember = block };

        // fRelease = TRUE hands the block over on success; on failure it is still ours.
        if (target is null || target.SetData(format, medium, 1) != S_OK) GlobalFree(block);
    }

    /// <summary>The path as a null-terminated wide string.</summary>
    private static IntPtr BuildText(string text)
    {
        var memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)((text.Length + 1) * 2));
        if (memory == IntPtr.Zero) return IntPtr.Zero;

        var block = GlobalLock(memory);
        if (block == IntPtr.Zero)
        {
            GlobalFree(memory);
            return IntPtr.Zero;
        }

        try
        {
            Marshal.Copy(text.ToCharArray(), 0, block, text.Length);
            Marshal.WriteInt16(block, text.Length * 2, 0);
        }
        finally
        {
            GlobalUnlock(memory);
        }

        return memory;
    }

    /// <summary>Gives the data object a drag picture, so the card follows the cursor.</summary>
    private static unsafe void AttachImage(IntPtr data, DragImage image)
    {
        var raw = IntPtr.Zero;
        try
        {
            if (CoCreateInstance(CLSID_DragDropHelper, IntPtr.Zero, CLSCTX_INPROC_SERVER,
                    IID_IDragSourceHelper, out raw) != S_OK || raw == IntPtr.Zero)
                return;

            var helper = ComInterfaceMarshaller<IDragSourceHelper>.ConvertToManaged((void*)raw);
            if (helper is null) return;

            var info = new SHDRAGIMAGE
            {
                sizeDragImage = new SIZE { cx = image.Width, cy = image.Height },
                ptOffset = new POINT { x = image.HotspotX, y = image.HotspotY },
                hbmpDragImage = image.Bitmap,
                crColorKey = unchecked((int)0xFFFFFFFF),
            };

            // The helper takes the bitmap on success, so it must not be deleted here.
            if (helper.InitializeFromBitmap(ref info, data) != S_OK) DeleteObject(image.Bitmap);
        }
        catch (COMException)
        {
            DeleteObject(image.Bitmap);
        }
        finally
        {
            if (raw != IntPtr.Zero) Marshal.Release(raw);
        }
    }

    [GeneratedComClass]
    internal sealed partial class DropSource : IDropSource
    {
        private const uint MK_LBUTTON = 0x0001;

        public int QueryContinueDrag(int escapePressed, uint keyState)
        {
            if (escapePressed != 0) return DRAGDROP_S_CANCEL;
            if ((keyState & MK_LBUTTON) == 0) return DRAGDROP_S_DROP;
            return S_OK;
        }

        public int GiveFeedback(uint effect) => DRAGDROP_S_USEDEFAULTCURSORS;
    }

    /// <summary>Only SetData is called; the rest are declared to keep the vtable in order.</summary>
    [GeneratedComInterface]
    [Guid("0000010e-0000-0000-C000-000000000046")]
    internal partial interface IDataObject
    {
        [PreserveSig] int GetData(in FORMATETC format, out STGMEDIUM medium);
        [PreserveSig] int GetDataHere(in FORMATETC format, ref STGMEDIUM medium);
        [PreserveSig] int QueryGetData(in FORMATETC format);
        [PreserveSig] int GetCanonicalFormatEtc(in FORMATETC format, out FORMATETC result);
        [PreserveSig] int SetData(in FORMATETC format, in STGMEDIUM medium, int release);
        [PreserveSig] int EnumFormatEtc(uint direction, out IntPtr enumerator);
        [PreserveSig] int DAdvise(in FORMATETC format, uint advf, IntPtr sink, out uint connection);
        [PreserveSig] int DUnadvise(uint connection);
        [PreserveSig] int EnumDAdvise(out IntPtr enumerator);
    }

    [GeneratedComInterface]
    [Guid("00000121-0000-0000-C000-000000000046")]
    internal partial interface IDropSource
    {
        [PreserveSig] int QueryContinueDrag(int escapePressed, uint keyState);
        [PreserveSig] int GiveFeedback(uint effect);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FORMATETC
    {
        public short cfFormat;
        public IntPtr ptd;
        public uint dwAspect;
        public int lindex;
        public uint tymed;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STGMEDIUM
    {
        public uint tymed;
        public IntPtr unionmember;
        public IntPtr pUnkForRelease;
    }

    // ============================  DRAG IMAGE  ============================

    private static readonly Guid CLSID_DragDropHelper = new("4657278A-411B-11d2-839A-00C04FD918D0");
    private static readonly Guid IID_IDragSourceHelper = new("DE5BF786-477A-11D2-839D-00C04FD918D0");

    [GeneratedComInterface]
    [Guid("DE5BF786-477A-11D2-839D-00C04FD918D0")]
    internal partial interface IDragSourceHelper
    {
        [PreserveSig] int InitializeFromBitmap(ref SHDRAGIMAGE image, IntPtr dataObject);
        [PreserveSig] int InitializeFromWindow(IntPtr window, IntPtr point, IntPtr dataObject);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SHDRAGIMAGE
    {
        public SIZE sizeDragImage;
        public POINT ptOffset;
        public IntPtr hbmpDragImage;
        public int crColorKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int x, y; }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr instance);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr handle);

    [LibraryImport("ole32.dll")]
    private static partial int DoDragDrop(IntPtr data, IntPtr source, uint allowed, out uint effect);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalLock(IntPtr memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr memory);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalFree(IntPtr memory);
}

/// <summary>The picture that follows the cursor during a drag. The shell takes ownership of the
/// bitmap once the drag starts.</summary>
public sealed partial record DragImage(IntPtr Bitmap, int Width, int Height, int HotspotX, int HotspotY)
{
    /// <summary>A 32-bit premultiplied-BGRA DIB section, which is what the drag helper wants: an
    /// ordinary bitmap drags as an opaque block with no alpha.</summary>
    public static unsafe DragImage? FromPixels(
        ReadOnlySpan<byte> pixels, int width, int height, int hotspotX, int hotspotY)
    {
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,          // top-down, matching the decoder
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,           // BI_RGB
        };

        var bitmap = CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) return null;

        var length = Math.Min(pixels.Length, width * height * 4);
        pixels[..length].CopyTo(new Span<byte>((void*)bits, length));
        return new DragImage(bitmap, width, height, hotspotX, hotspotY);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateDIBSection(
        IntPtr dc, ref BITMAPINFOHEADER header, uint usage, out IntPtr bits,
        IntPtr section, uint offset);
}
