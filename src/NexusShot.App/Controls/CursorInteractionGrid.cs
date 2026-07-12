using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace NexusShot.App.Controls;

/// <summary>
/// A Grid that can change its pointer cursor. <see cref="Microsoft.UI.Xaml.UIElement.ProtectedCursor"/>
/// is protected, so plain panels cannot offer resize/move cursor feedback; this exposes it with a
/// change check, since the editor updates the cursor on every pointer move.
/// </summary>
public sealed partial class CursorInteractionGrid : Grid
{
    private static readonly Guid CursorInteropId = new("ac6f5065-90c4-46ce-beb7-05e138e54117");
    private static InputCursor? _pencilCursor;
    private InputCursor? _brushCursor;
    private BrushCursorKey? _brushCursorKey;
    private InputSystemCursorShape? _currentShape;
    private bool _isPencilCursor;
    private bool _isBrushCursor;

    public void SetCursorShape(InputSystemCursorShape shape)
    {
        if (_currentShape == shape) return;
        _currentShape = shape;
        _isPencilCursor = false;
        _isBrushCursor = false;
        ProtectedCursor = InputSystemCursor.Create(shape);
    }

    public void SetPencilCursor()
    {
        if (_isPencilCursor) return;
        _currentShape = null;
        _isPencilCursor = true;
        _isBrushCursor = false;
        try
        {
            ProtectedCursor = _pencilCursor ??= CreatePencilCursor();
        }
        catch
        {
            _isPencilCursor = false;
            SetCursorShape(InputSystemCursorShape.Cross);
        }
    }

    /// <summary>
    /// Uses the native pointer surface for brush and eraser feedback. Windows advances an
    /// HCURSOR from the newest physical pointer position independently of the XAML UI thread,
    /// so a busy or frame-limited compositor cannot leave the footprint preview behind.
    /// </summary>
    public void SetBrushCursor(double physicalDiameter, double rasterizationScale, Windows.UI.Color fill)
    {
        var diameter = Math.Max(1, (int)Math.Round(physicalDiameter));
        var outerStroke = Math.Max(1, (int)Math.Round(3 * rasterizationScale));
        var innerStroke = Math.Max(1, (int)Math.Round(rasterizationScale));
        var color = Color.FromArgb(fill.A, fill.R, fill.G, fill.B);
        var key = new BrushCursorKey(diameter, outerStroke, innerStroke, color.ToArgb());
        if (_isBrushCursor && _brushCursorKey == key) return;

        // Leave room for the outline outside the exact paint-footprint diameter. The hotspot is
        // the footprint centre, including for even-sized cursors.
        var padding = outerStroke + 2;
        var bitmapSize = diameter + padding * 2;
        using var bitmap = new Bitmap(bitmapSize, bitmapSize, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var fillBrush = new SolidBrush(color))
        using (var outer = new Pen(Color.Black, outerStroke))
        using (var inner = new Pen(Color.White, innerStroke))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var inset = outerStroke / 2f;
            var bounds = new RectangleF(
                padding + inset,
                padding + inset,
                Math.Max(0.5f, diameter - outerStroke),
                Math.Max(0.5f, diameter - outerStroke));
            graphics.FillEllipse(fillBrush, bounds);
            graphics.DrawEllipse(outer, bounds);
            graphics.DrawEllipse(inner, bounds);
        }

        var hotspot = (uint)(padding + diameter / 2);
        var cursor = CreateNativeCursor(bitmap, hotspot, hotspot);
        var previous = _brushCursor;
        _brushCursor = cursor;
        _brushCursorKey = key;
        _currentShape = null;
        _isPencilCursor = false;
        _isBrushCursor = true;
        ProtectedCursor = cursor;
        // Freed only once the replacement is live, so the active cursor is never a dead handle.
        previous?.Dispose();
    }

    private static InputCursor CreatePencilCursor()
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Segoe Fluent Icons", 24, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var outline = new SolidBrush(Color.Black))
        using (var fill = new SolidBrush(Color.White))
        {
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.DrawString("\uE70F", font, outline, 2, 2);
            graphics.DrawString("\uE70F", font, fill, 0, 0);
        }

        return CreateNativeCursor(bitmap, 2, 27);
    }

    private static InputCursor CreateNativeCursor(Bitmap bitmap, uint hotspotX, uint hotspotY)
    {
        var icon = bitmap.GetHicon();
        if (!NexusShot.App.Native.LayeredWindowNative.GetIconInfo(icon, out var info))
        {
            NexusShot.App.Native.LayeredWindowNative.DestroyIcon(icon);
            throw new InvalidOperationException("Could not create the pencil cursor.");
        }

        info.fIcon = false;
        info.xHotspot = hotspotX;
        info.yHotspot = hotspotY;
        var cursorHandle = NexusShot.App.Native.LayeredWindowNative.CreateIconIndirect(ref info);
        NexusShot.App.Native.LayeredWindowNative.DeleteObject(info.hbmColor);
        NexusShot.App.Native.LayeredWindowNative.DeleteObject(info.hbmMask);
        NexusShot.App.Native.LayeredWindowNative.DestroyIcon(icon);
        if (cursorHandle == IntPtr.Zero)
            throw new InvalidOperationException("Could not create the pencil cursor handle.");

        try
        {
            using var factory = WinRT.ActivationFactory.Get("Microsoft.UI.Input.InputCursor", CursorInteropId);
            var vtable = Marshal.ReadIntPtr(factory.ThisPtr);
            var method = Marshal.ReadIntPtr(vtable, 6 * IntPtr.Size);
            var create = Marshal.GetDelegateForFunctionPointer<CreateFromHCursor>(method);
            var error = create(factory.ThisPtr, cursorHandle, out var cursor);
            Marshal.ThrowExceptionForHR(error);
            try { return WinRT.MarshalInspectable<InputCursor>.FromAbi(cursor)!; }
            finally { WinRT.MarshalInspectable<InputCursor>.DisposeAbi(cursor); }
        }
        finally
        {
            NexusShot.App.Native.LayeredWindowNative.DestroyCursor(cursorHandle);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateFromHCursor(IntPtr instance, IntPtr cursor, out IntPtr result);

    private readonly record struct BrushCursorKey(
        int Diameter,
        int OuterStroke,
        int InnerStroke,
        int Argb);
}
