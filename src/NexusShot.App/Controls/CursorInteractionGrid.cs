using System.Drawing;
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
    private static InputCursor? _hiddenCursor;
    private InputSystemCursorShape? _currentShape;

    public void SetCursorShape(InputSystemCursorShape shape)
    {
        if (_currentShape == shape) return;
        _currentShape = shape;
        ProtectedCursor = InputSystemCursor.Create(shape);
    }

    public void SetPencilCursor()
    {
        _currentShape = null;
        try
        {
            ProtectedCursor = _pencilCursor ??= CreatePencilCursor();
        }
        catch
        {
            SetCursorShape(InputSystemCursorShape.Cross);
        }
    }

    /// <summary>Hides the OS pointer while an image-space brush outline supplies exact feedback.</summary>
    public void SetHiddenCursor()
    {
        _currentShape = null;
        if (_hiddenCursor is null)
        {
            using var bitmap = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
            _hiddenCursor = CreateNativeCursor(bitmap, 0, 0);
        }
        ProtectedCursor = _hiddenCursor;
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
}
