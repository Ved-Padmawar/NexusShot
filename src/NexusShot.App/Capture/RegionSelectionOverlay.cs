using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using NexusShot.App.Native;
using Windows.Graphics;
using GdiPoint = System.Drawing.Point;
using GdiRectangle = System.Drawing.Rectangle;

namespace NexusShot.App.Capture;

/// <summary>
/// A layered Win32 overlay for selecting a screen region over the <em>live</em> desktop.
/// WinUI 3 has no supported transparent window, so this is a plain Win32 window composited with
/// <c>UpdateLayeredWindow</c>. The selection area is left fully transparent, which is why video
/// keeps playing underneath while the user drags.
/// </summary>
public sealed class RegionSelectionOverlay
{
    private const byte DimAlpha = 110;
    private const int MinimumSelection = 4;

    // WM_MOUSEMOVE can arrive at 1000Hz from gaming mice; UpdateLayeredWindow pushes the whole
    // desktop-sized surface each call, so redraws are coalesced to at most one per interval and a
    // trailing timer paints whatever the throttle skipped.
    private const long RedrawIntervalMilliseconds = 7;
    private static readonly IntPtr RedrawTimerId = 1;
    private const uint RedrawTimerMilliseconds = 15;

    private static readonly Color SelectionStroke = Color.FromArgb(255, 10, 132, 255);

    private readonly RectInt32 _desktop;
    private readonly TaskCompletionSource<RectInt32?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Kept alive for the window's lifetime: the WndProc delegate is called from native code.
    private LayeredWindowNative.WndProc? _wndProc;

    private IntPtr _hwnd;
    private IntPtr _cursor;
    private bool _ownsCursor;
    private GdiPoint _anchor;
    private GdiPoint _cursorPosition;
    private bool _isDragging;
    private bool _hasSelection;
    private long _lastRedrawTick;
    private bool _redrawPending;

    // The drawing surface is created once and reused: WM_MOUSEMOVE arrives at pointer rate, and
    // reallocating a full-desktop DIB per move would churn tens of megabytes a second.
    private IntPtr _screenDc;
    private IntPtr _memoryDc;
    private IntPtr _dib;
    private IntPtr _previousBitmap;
    private Bitmap? _surface;
    private Graphics? _graphics;

    // Cached for the drag's duration: allocating pens, brushes and fonts per frame shows up as
    // GC pressure at pointer-move rate.
    private SolidBrush? _dimBrush;
    private SolidBrush? _clearBrush;
    private Pen? _selectionPen;
    private SolidBrush? _badgeBrush;
    private SolidBrush? _badgeTextBrush;
    private Font? _badgeFont;

    /// <summary>The area the previous frame's selection chrome covered, so it can be repainted over.</summary>
    private GdiRectangle _lastDirty;

    private RegionSelectionOverlay(RectInt32 desktop) => _desktop = desktop;

    /// <summary>
    /// Shows the overlay and resolves with the selected region in physical desktop pixels,
    /// or <see langword="null"/> if the user cancelled.
    /// </summary>
    public static Task<RectInt32?> SelectAsync()
    {
        var desktop = new RectInt32(
            LayeredWindowNative.GetSystemMetrics(LayeredWindowNative.SmXVirtualScreen),
            LayeredWindowNative.GetSystemMetrics(LayeredWindowNative.SmYVirtualScreen),
            LayeredWindowNative.GetSystemMetrics(LayeredWindowNative.SmCxVirtualScreen),
            LayeredWindowNative.GetSystemMetrics(LayeredWindowNative.SmCyVirtualScreen));

        var overlay = new RegionSelectionOverlay(desktop);

        // The overlay owns a blocking message loop, so it gets its own thread rather than
        // starving the WinUI dispatcher.
        var thread = new Thread(overlay.RunMessageLoop) { IsBackground = true, Name = "NexusShot.RegionOverlay" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return overlay._result.Task;
    }

    private void RunMessageLoop()
    {
        try
        {
            CreateWindow();
            CreateSurface();
            Redraw();

            while (LayeredWindowNative.GetMessage(out var message, IntPtr.Zero, 0, 0))
            {
                LayeredWindowNative.TranslateMessage(ref message);
                LayeredWindowNative.DispatchMessage(ref message);
            }
        }
        catch (Exception exception)
        {
            _result.TrySetException(exception);
        }
        finally
        {
            DestroySurface();
            Close();
            if (_ownsCursor) LayeredWindowNative.DestroyCursor(_cursor);

            // Falling out of the loop without a selection means the user cancelled.
            _result.TrySetResult(null);
        }
    }

    private void CreateWindow()
    {
        var instance = LayeredWindowNative.GetModuleHandle(null);
        _cursor = CreateCrosshairCursor();
        _ownsCursor = _cursor != IntPtr.Zero;
        if (!_ownsCursor) _cursor = LayeredWindowNative.LoadCursor(IntPtr.Zero, LayeredWindowNative.IdcCross);
        _wndProc = WindowProcedure;

        // The class name must be unique per overlay: RegisterClassEx fails if the class already exists.
        var className = $"NexusShotRegionOverlay_{Guid.NewGuid():N}";
        var wndClass = new LayeredWindowNative.WndClassEx
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LayeredWindowNative.WndClassEx>(),
            style = LayeredWindowNative.CsHRedraw | LayeredWindowNative.CsVRedraw,
            lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            hCursor = _cursor,
            lpszClassName = className,
        };

        if (LayeredWindowNative.RegisterClassEx(ref wndClass) == 0)
            throw new InvalidOperationException("Could not register the region overlay window class.");

        _hwnd = LayeredWindowNative.CreateWindowEx(
            LayeredWindowNative.WsExLayered | LayeredWindowNative.WsExTopmost | LayeredWindowNative.WsExToolWindow,
            className,
            null,
            LayeredWindowNative.WsPopup | LayeredWindowNative.WsVisible,
            _desktop.X, _desktop.Y, _desktop.Width, _desktop.Height,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("Could not create the region overlay window.");

        LayeredWindowNative.SetForegroundWindow(_hwnd);
    }

    private static IntPtr CreateCrosshairCursor()
    {
        const int size = 32;
        const int center = size / 2;
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var fill = new Pen(Color.White, 3))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.DrawLine(fill, center, 2, center, size - 3);
            graphics.DrawLine(fill, 2, center, size - 3, center);
        }

        var icon = bitmap.GetHicon();
        if (!LayeredWindowNative.GetIconInfo(icon, out var info))
        {
            LayeredWindowNative.DestroyIcon(icon);
            return IntPtr.Zero;
        }

        info.fIcon = false;
        info.xHotspot = center;
        info.yHotspot = center;
        var cursor = LayeredWindowNative.CreateIconIndirect(ref info);
        LayeredWindowNative.DeleteObject(info.hbmColor);
        LayeredWindowNative.DeleteObject(info.hbmMask);
        LayeredWindowNative.DestroyIcon(icon);
        return cursor;
    }

    private IntPtr WindowProcedure(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case LayeredWindowNative.WmSetCursor:
                LayeredWindowNative.SetCursor(_cursor);
                return 1;

            case LayeredWindowNative.WmMouseMove:
                // Before a drag the overlay is a static dim, so pointer movement needs no repaint
                // at all; the cross cursor alone tracks the pointer, like the system snipping tool.
                if (!_isDragging) return IntPtr.Zero;
                _cursorPosition = ToPoint(lParam);
                RequestRedraw();
                return IntPtr.Zero;

            case LayeredWindowNative.WmTimer when wParam == RedrawTimerId:
                // Trailing edge of the throttle: paint the last skipped move once input goes quiet.
                if (_redrawPending) Redraw();
                return IntPtr.Zero;

            case LayeredWindowNative.WmLButtonDown:
                _anchor = ToPoint(lParam);
                _cursorPosition = _anchor;
                _isDragging = true;
                _hasSelection = true;
                LayeredWindowNative.SetCapture(hWnd);
                LayeredWindowNative.SetTimer(hWnd, RedrawTimerId, RedrawTimerMilliseconds, IntPtr.Zero);
                Redraw();
                return IntPtr.Zero;

            case LayeredWindowNative.WmLButtonUp:
                if (_isDragging)
                {
                    LayeredWindowNative.KillTimer(hWnd, RedrawTimerId);
                    CompleteDrag(ToPoint(lParam));
                }
                return IntPtr.Zero;

            case LayeredWindowNative.WmRButtonDown:
                Cancel();
                return IntPtr.Zero;

            case LayeredWindowNative.WmKeyDown when wParam.ToInt32() == LayeredWindowNative.VkEscape:
                Cancel();
                return IntPtr.Zero;

            case LayeredWindowNative.WmDestroy:
                LayeredWindowNative.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return LayeredWindowNative.DefWindowProc(hWnd, message, wParam, lParam);
    }

    private void CompleteDrag(GdiPoint release)
    {
        _isDragging = false;
        LayeredWindowNative.ReleaseCapture();

        var selection = Normalize(_anchor, release);

        // A click with no drag cancels rather than capturing a sliver.
        if (selection.Width < MinimumSelection || selection.Height < MinimumSelection)
        {
            Cancel();
            return;
        }

        // Window coordinates are relative to the virtual desktop origin, which may be negative.
        _result.TrySetResult(new RectInt32(
            _desktop.X + selection.X,
            _desktop.Y + selection.Y,
            selection.Width,
            selection.Height));

        Close();
    }

    private void Cancel()
    {
        _result.TrySetResult(null);
        Close();
    }

    private void Close()
    {
        if (_hwnd == IntPtr.Zero) return;
        LayeredWindowNative.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    /// <summary>Allocates the DIB-backed drawing surface once, sized to the whole virtual desktop.</summary>
    private void CreateSurface()
    {
        var width = _desktop.Width;
        var height = _desktop.Height;

        var header = new LayeredWindowNative.BitmapInfoHeader
        {
            biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LayeredWindowNative.BitmapInfoHeader>(),
            biWidth = width,
            biHeight = -height, // Negative height gives a top-down DIB, matching GDI+ orientation.
            biPlanes = 1,
            biBitCount = 32,
            biCompression = LayeredWindowNative.BiRgb,
        };

        _screenDc = LayeredWindowNative.GetDC(IntPtr.Zero);
        _memoryDc = LayeredWindowNative.CreateCompatibleDC(_screenDc);
        _dib = LayeredWindowNative.CreateDIBSection(_screenDc, ref header, LayeredWindowNative.DibRgbColors, out var bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero) throw new InvalidOperationException("Could not allocate the region overlay surface.");
        _previousBitmap = LayeredWindowNative.SelectObject(_memoryDc, _dib);

        // PArgb: UpdateLayeredWindow with AC_SRC_ALPHA expects premultiplied alpha.
        _surface = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
        _graphics = Graphics.FromImage(_surface);

        _dimBrush = new SolidBrush(Color.FromArgb(DimAlpha, 0, 0, 0));
        _clearBrush = new SolidBrush(Color.Transparent);
        _selectionPen = new Pen(SelectionStroke, 1.5f);
        _badgeBrush = new SolidBrush(Color.FromArgb(242, 10, 132, 255));
        _badgeTextBrush = new SolidBrush(Color.White);
        _badgeFont = new Font("Consolas", 12, FontStyle.Bold, GraphicsUnit.Pixel);
    }

    private void DestroySurface()
    {
        _dimBrush?.Dispose();
        _clearBrush?.Dispose();
        _selectionPen?.Dispose();
        _badgeBrush?.Dispose();
        _badgeTextBrush?.Dispose();
        _badgeFont?.Dispose();
        _dimBrush = _clearBrush = null;
        _selectionPen = null;
        _badgeBrush = _badgeTextBrush = null;
        _badgeFont = null;

        _graphics?.Dispose();
        _surface?.Dispose();
        _graphics = null;
        _surface = null;

        if (_memoryDc != IntPtr.Zero && _previousBitmap != IntPtr.Zero) LayeredWindowNative.SelectObject(_memoryDc, _previousBitmap);
        if (_dib != IntPtr.Zero) LayeredWindowNative.DeleteObject(_dib);
        if (_memoryDc != IntPtr.Zero) LayeredWindowNative.DeleteDC(_memoryDc);
        if (_screenDc != IntPtr.Zero) LayeredWindowNative.ReleaseDC(IntPtr.Zero, _screenDc);

        _dib = _memoryDc = _screenDc = _previousBitmap = IntPtr.Zero;
    }

    /// <summary>Coalesces redraw requests: paints immediately when the interval allows, otherwise
    /// leaves the work to the trailing timer so a 1000Hz mouse cannot outrun the compositor.</summary>
    private void RequestRedraw()
    {
        var now = Environment.TickCount64;
        if (now - _lastRedrawTick < RedrawIntervalMilliseconds)
        {
            _redrawPending = true;
            return;
        }
        Redraw();
    }

    /// <summary>Repaints the overlay surface and pushes it to the compositor.</summary>
    private void Redraw()
    {
        if (_hwnd == IntPtr.Zero || _graphics is null) return;

        _lastRedrawTick = Environment.TickCount64;
        _redrawPending = false;

        var full = new GdiRectangle(0, 0, _desktop.Width, _desktop.Height);

        if (!_hasSelection)
        {
            // Painted exactly once, before the first drag: a static full-screen dim.
            _graphics.CompositingMode = CompositingMode.SourceCopy;
            _graphics.SmoothingMode = SmoothingMode.None;
            _graphics.FillRectangle(_dimBrush!, full);
            Push();
            return;
        }

        var selection = Normalize(_anchor, _cursorPosition);
        var badge = ComputeBadgeBounds(selection, full, out var label);

        // Repaint only what changed: the previous frame's chrome plus this frame's. The dim
        // elsewhere is already on the surface, so GDI+ never touches the other megapixels.
        var dirty = GdiRectangle.Union(GdiRectangle.Inflate(selection, 3, 3), badge);
        var paintArea = GdiRectangle.Intersect(
            _lastDirty.IsEmpty ? dirty : GdiRectangle.Union(dirty, _lastDirty), full);
        _lastDirty = dirty;

        _graphics.SetClip(paintArea);
        _graphics.SmoothingMode = SmoothingMode.None;

        // SourceCopy writes the exact pixel values: dim over the repaint area, then a true
        // zero-alpha hole so the live desktop shows through the selection untinted.
        _graphics.CompositingMode = CompositingMode.SourceCopy;
        _graphics.FillRectangle(_dimBrush!, paintArea);
        _graphics.FillRectangle(_clearBrush!, selection);

        _graphics.CompositingMode = CompositingMode.SourceOver;
        _graphics.SmoothingMode = SmoothingMode.AntiAlias;
        _graphics.DrawRectangle(_selectionPen!, selection);
        _graphics.SmoothingMode = SmoothingMode.None;

        _graphics.FillRectangle(_badgeBrush!, badge);
        _graphics.DrawString(label, _badgeFont!, _badgeTextBrush!, badge.X + 8, badge.Y + 4);

        _graphics.ResetClip();
        Push();
    }

    /// <summary>Places the dimension badge below the selection, or above when there is no room.</summary>
    private GdiRectangle ComputeBadgeBounds(GdiRectangle selection, GdiRectangle full, out string label)
    {
        label = $"{selection.Width} × {selection.Height}";
        var textSize = _graphics!.MeasureString(label, _badgeFont!);

        var badgeWidth = (int)textSize.Width + 16;
        var badgeHeight = (int)textSize.Height + 8;

        var x = Math.Clamp(selection.Left, 0, Math.Max(0, full.Width - badgeWidth));
        var y = selection.Bottom + 8;
        if (y + badgeHeight > full.Height) y = Math.Max(0, selection.Top - badgeHeight - 8);

        return new GdiRectangle(x, y, badgeWidth, badgeHeight);
    }

    private void Push()
    {
        var destination = new LayeredWindowNative.Point { X = _desktop.X, Y = _desktop.Y };
        var size = new LayeredWindowNative.Size { Width = _desktop.Width, Height = _desktop.Height };
        var source = new LayeredWindowNative.Point { X = 0, Y = 0 };
        var blend = new LayeredWindowNative.BlendFunction
        {
            BlendOp = LayeredWindowNative.AcSrcOver,
            SourceConstantAlpha = 255,
            AlphaFormat = LayeredWindowNative.AcSrcAlpha,
        };

        LayeredWindowNative.UpdateLayeredWindow(
            _hwnd, _screenDc, ref destination, ref size, _memoryDc, ref source, 0, ref blend, LayeredWindowNative.UlwAlpha);
    }

    private static GdiRectangle Normalize(GdiPoint a, GdiPoint b) => new(
        Math.Min(a.X, b.X),
        Math.Min(a.Y, b.Y),
        Math.Abs(b.X - a.X),
        Math.Abs(b.Y - a.Y));

    /// <summary>Unpacks the signed x/y packed into an LPARAM. Coordinates can be negative on multi-monitor setups.</summary>
    private static GdiPoint ToPoint(IntPtr lParam)
    {
        var value = unchecked((int)lParam.ToInt64());
        return new GdiPoint((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
    }
}
