using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace NexusShot.Platform;

/// <summary>
/// Drags a file out as CF_HDROP - what Explorer, browsers and mail clients read as "here is a file".
///
/// The COM interfaces are declared here rather than taken from System.Runtime.InteropServices
/// .ComTypes: those are marshalled by the classic runtime marshaller, which AOT trims away
/// (IL2050/SYSLIB1095), and the drag would then fail silently in the shipped exe.
///
/// Runs on its own STA thread - DoDragDrop pumps a modal loop that would otherwise freeze every
/// window the app owns.
/// </summary>
public static partial class FileDrag
{
    private const uint DROPEFFECT_COPY = 1;
    private const int S_OK = 0;
    private const int S_FALSE = 1;
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const int DV_E_FORMATETC = unchecked((int)0x80040064);
    private const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
    private const int DRAGDROP_S_DROP = 0x00040100;
    private const int DRAGDROP_S_CANCEL = 0x00040101;
    private const int DRAGDROP_S_USEDEFAULTCURSORS = 0x00040102;

    private const short CF_HDROP = 15;
    private const uint TYMED_HGLOBAL = 1;
    private const uint GMEM_MOVEABLE = 0x0002;

    public static void Start(string path)
    {
        if (!File.Exists(path)) return;

        var thread = new Thread(() =>
        {
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
                if (data == IntPtr.Zero || source == IntPtr.Zero) return;

                _ = DoDragDrop(data, source, DROPEFFECT_COPY, out _);
            }
            catch (COMException)
            {
                // A drag the target refuses is not worth surfacing.
            }
            finally
            {
                if (data != IntPtr.Zero) Marshal.Release(data);
                if (source != IntPtr.Zero) Marshal.Release(source);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    [GeneratedComClass]
    internal sealed partial class FileDataObject(string path) : IDataObject
    {
        public int GetData(in FORMATETC format, out STGMEDIUM medium)
        {
            medium = default;
            if (format.cfFormat != CF_HDROP || (format.tymed & TYMED_HGLOBAL) == 0)
                return DV_E_FORMATETC;

            medium = new STGMEDIUM
            {
                tymed = TYMED_HGLOBAL,
                unionmember = BuildDropFiles(path),
                pUnkForRelease = IntPtr.Zero,
            };
            return S_OK;
        }

        public int GetDataHere(in FORMATETC format, ref STGMEDIUM medium) => E_NOTIMPL;

        public int QueryGetData(in FORMATETC format) =>
            format.cfFormat == CF_HDROP && (format.tymed & TYMED_HGLOBAL) != 0
                ? S_OK
                : DV_E_FORMATETC;

        public int GetCanonicalFormatEtc(in FORMATETC format, out FORMATETC result)
        {
            result = default;
            return E_NOTIMPL;
        }

        public int SetData(in FORMATETC format, in STGMEDIUM medium, int release) => E_NOTIMPL;

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
    }

    [GeneratedComClass]
    internal sealed partial class FormatEnumerator : IEnumFORMATETC
    {
        private int _index;

        public int Next(uint count, [MarshalUsing(CountElementName = nameof(count))] FORMATETC[] formats,
            out uint fetched)
        {
            if (count == 0 || _index > 0)
            {
                fetched = 0;
                return S_FALSE;
            }

            formats[0] = new FORMATETC
            {
                cfFormat = CF_HDROP,
                dwAspect = 1,          // DVASPECT_CONTENT
                lindex = -1,
                ptd = IntPtr.Zero,
                tymed = TYMED_HGLOBAL,
            };

            _index = 1;
            fetched = 1;
            return S_OK;
        }

        public int Skip(uint count)
        {
            _index += (int)count;
            return _index > 1 ? S_FALSE : S_OK;
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

    [DllImport("ole32.dll")]
    private static extern int DoDragDrop(IntPtr data, IntPtr source, uint allowed, out uint effect);

    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
}
