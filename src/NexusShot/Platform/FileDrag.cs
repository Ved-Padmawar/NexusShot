using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace NexusShot.Platform;

/// <summary>
/// Drags a file out of the app.
///
/// Two formats: CF_HDROP is what Explorer, browsers and mail clients read as "here is a file", and
/// CF_UNICODETEXT is what makes a drop onto a text field paste the path rather than do nothing - a
/// text field cannot accept a file at all.
///
/// The COM interfaces are declared here rather than taken from System.Runtime.InteropServices
/// .ComTypes: those are marshalled by the classic runtime marshaller, which AOT trims away
/// (IL2050/SYSLIB1095), and the drag would then fail silently in the shipped exe.
/// </summary>
public static partial class FileDrag
{
    private const uint DROPEFFECT_NONE = 0;
    private const uint DROPEFFECT_COPY = 1;
    private const uint CLSCTX_INPROC_SERVER = 1;
    private const int S_OK = 0;
    private const int S_FALSE = 1;
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const int DV_E_FORMATETC = unchecked((int)0x80040064);
    private const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
    private const int DRAGDROP_S_DROP = 0x00040100;
    private const int DRAGDROP_S_CANCEL = 0x00040101;
    private const int DRAGDROP_S_USEDEFAULTCURSORS = 0x00040102;

    private const short CF_HDROP = 15;
    private const short CF_UNICODETEXT = 13;

    /// <summary>The formats offered, in preference order: a real file first, its path as a fallback.</summary>
    private static readonly short[] Formats = [CF_HDROP, CF_UNICODETEXT];

    private const uint TYMED_HGLOBAL = 1;
    private const uint GMEM_MOVEABLE = 0x0002;

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
            unsafe
            {
                data = (IntPtr)ComInterfaceMarshaller<IDataObject>
                    .ConvertToUnmanaged(new FileDataObject(path));
                source = (IntPtr)ComInterfaceMarshaller<IDropSource>
                    .ConvertToUnmanaged(new DropSource());
            }
            if (data == IntPtr.Zero || source == IntPtr.Zero) return false;

            if (image is not null) AttachImage(data, image);

            var hr = DoDragDrop(data, source, DROPEFFECT_COPY, out var effect);

            // Anything else - a cancel, an Escape, a target that refused it - is not a drop.
            return hr == DRAGDROP_S_DROP && effect != DROPEFFECT_NONE;
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            if (data != IntPtr.Zero) Marshal.Release(data);
            if (source != IntPtr.Zero) Marshal.Release(source);
        }
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
    internal sealed partial class FileDataObject(string path) : IDataObject
    {
        /// <summary>Formats the drag-image helper stored on us. InitializeFromBitmap does not keep the
        /// picture itself - it writes it into the data object under private formats and reads them
        /// back during the drag, so a data object that refuses SetData gets no drag image at all.</summary>
        private readonly Dictionary<short, STGMEDIUM> _stored = [];

        public int GetData(in FORMATETC format, out STGMEDIUM medium)
        {
            medium = default;

            if (_stored.TryGetValue(format.cfFormat, out var stored))
            {
                medium = stored;
                return S_OK;
            }

            if ((format.tymed & TYMED_HGLOBAL) == 0) return DV_E_FORMATETC;

            var block = format.cfFormat switch
            {
                CF_HDROP => BuildDropFiles(path),
                CF_UNICODETEXT => BuildText(path),
                _ => IntPtr.Zero,
            };
            if (block == IntPtr.Zero) return DV_E_FORMATETC;

            medium = new STGMEDIUM
            {
                tymed = TYMED_HGLOBAL,
                unionmember = block,
                pUnkForRelease = IntPtr.Zero,
            };
            return S_OK;
        }

        public int GetDataHere(in FORMATETC format, ref STGMEDIUM medium) => E_NOTIMPL;

        public int QueryGetData(in FORMATETC format)
        {
            if (_stored.ContainsKey(format.cfFormat)) return S_OK;

            return Array.IndexOf(Formats, format.cfFormat) >= 0 && (format.tymed & TYMED_HGLOBAL) != 0
                ? S_OK
                : DV_E_FORMATETC;
        }

        public int GetCanonicalFormatEtc(in FORMATETC format, out FORMATETC result)
        {
            result = default;
            return E_NOTIMPL;
        }

        public int SetData(in FORMATETC format, in STGMEDIUM medium, int release)
        {
            _stored[format.cfFormat] = medium;
            return S_OK;
        }

        public int EnumFormatEtc(uint direction, out IEnumFORMATETC? enumerator)
        {
            const uint DATADIR_GET = 1;
            enumerator = direction == DATADIR_GET ? new FormatEnumerator() : null;
            return enumerator is null ? E_NOTIMPL : S_OK;
        }

        public int DAdvise(in FORMATETC format, uint advf, IntPtr sink, out uint connection)
        {
            connection = 0;
            return OLE_E_ADVISENOTSUPPORTED;
        }

        public int DUnadvise(uint connection) => OLE_E_ADVISENOTSUPPORTED;

        public int EnumDAdvise(out IntPtr enumerator)
        {
            enumerator = IntPtr.Zero;
            return OLE_E_ADVISENOTSUPPORTED;
        }

        /// <summary>A DROPFILES header, then the path, double-null-terminated.</summary>
        private static IntPtr BuildDropFiles(string file)
        {
            var header = Marshal.SizeOf<DROPFILES>();
            var bytes = (file.Length + 2) * 2;

            var memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)(header + bytes));
            if (memory == IntPtr.Zero) throw new OutOfMemoryException();

            var block = GlobalLock(memory);
            if (block == IntPtr.Zero)
            {
                GlobalFree(memory);
                throw new OutOfMemoryException();
            }

            try
            {
                Marshal.StructureToPtr(new DROPFILES { pFiles = (uint)header, fWide = 1 }, block, false);

                var text = block + header;
                Marshal.Copy(file.ToCharArray(), 0, text, file.Length);

                // Two nulls: one ends the string, one ends the list.
                Marshal.WriteInt16(text, file.Length * 2, 0);
                Marshal.WriteInt16(text, (file.Length + 1) * 2, 0);
            }
            finally
            {
                GlobalUnlock(memory);
            }

            return memory;
        }

        /// <summary>The path as a null-terminated wide string, for a target that takes text.</summary>
        private static IntPtr BuildText(string text)
        {
            var bytes = (text.Length + 1) * 2;

            var memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes);
            if (memory == IntPtr.Zero) throw new OutOfMemoryException();

            var block = GlobalLock(memory);
            if (block == IntPtr.Zero)
            {
                GlobalFree(memory);
                throw new OutOfMemoryException();
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
    }

    [GeneratedComClass]
    internal sealed partial class FormatEnumerator : IEnumFORMATETC
    {
        private int _index;

        public int Next(uint count, [MarshalUsing(CountElementName = nameof(count))] FORMATETC[] formats,
            out uint fetched)
        {
            fetched = 0;

            while (fetched < count && _index < Formats.Length)
            {
                formats[fetched] = new FORMATETC
                {
                    cfFormat = Formats[_index],
                    dwAspect = 1,          // DVASPECT_CONTENT
                    lindex = -1,
                    ptd = IntPtr.Zero,
                    tymed = TYMED_HGLOBAL,
                };

                _index++;
                fetched++;
            }

            return fetched == count ? S_OK : S_FALSE;
        }

        public int Skip(uint count)
        {
            _index += (int)count;
            return _index > Formats.Length ? S_FALSE : S_OK;
        }

        public int Reset()
        {
            _index = 0;
            return S_OK;
        }

        public int Clone(out IEnumFORMATETC? clone)
        {
            clone = new FormatEnumerator { _index = _index };
            return S_OK;
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

    [GeneratedComInterface]
    [Guid("0000010e-0000-0000-C000-000000000046")]
    internal partial interface IDataObject
    {
        [PreserveSig] int GetData(in FORMATETC format, out STGMEDIUM medium);
        [PreserveSig] int GetDataHere(in FORMATETC format, ref STGMEDIUM medium);
        [PreserveSig] int QueryGetData(in FORMATETC format);
        [PreserveSig] int GetCanonicalFormatEtc(in FORMATETC format, out FORMATETC result);
        [PreserveSig] int SetData(in FORMATETC format, in STGMEDIUM medium, int release);
        [PreserveSig] int EnumFormatEtc(uint direction, out IEnumFORMATETC? enumerator);
        [PreserveSig] int DAdvise(in FORMATETC format, uint advf, IntPtr sink, out uint connection);
        [PreserveSig] int DUnadvise(uint connection);
        [PreserveSig] int EnumDAdvise(out IntPtr enumerator);
    }

    [GeneratedComInterface]
    [Guid("00000103-0000-0000-C000-000000000046")]
    internal partial interface IEnumFORMATETC
    {
        [PreserveSig] int Next(uint count,
            [MarshalUsing(CountElementName = nameof(count))] FORMATETC[] formats, out uint fetched);
        [PreserveSig] int Skip(uint count);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumFORMATETC? clone);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct DROPFILES
    {
        public uint pFiles;
        public int X;
        public int Y;
        public int fNC;
        public int fWide;
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

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr instance);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);

    [DllImport("ole32.dll")]
    private static extern int DoDragDrop(IntPtr data, IntPtr source, uint allowed, out uint effect);

    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
}

/// <summary>The picture that follows the cursor during a drag. The shell takes ownership of the
/// bitmap once the drag starts.</summary>
public sealed record DragImage(IntPtr Bitmap, int Width, int Height, int HotspotX, int HotspotY)
{
    /// <summary>A 32-bit premultiplied-BGRA DIB section, which is what the drag helper wants: an
    /// ordinary bitmap drags as an opaque block with no alpha.</summary>
    public static unsafe DragImage? FromPixels(
        byte[] pixels, int width, int height, int hotspotX, int hotspotY)
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

        Marshal.Copy(pixels, 0, bits, Math.Min(pixels.Length, width * height * 4));
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

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr dc, ref BITMAPINFOHEADER header, uint usage, out IntPtr bits,
        IntPtr section, uint offset);
}
