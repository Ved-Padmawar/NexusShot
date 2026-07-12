using System.Runtime.InteropServices;
using NexusShot.Core;

namespace NexusShot.Views;

/// <summary>
/// Inline text entry, hosted as a real Win32 EDIT control.
///
/// Hand-rolling a text box means hand-rolling a caret, selection, shift-arrow, double-click word
/// select, Ctrl+A, clipboard, undo *within the box*, and IME composition for anyone typing a
/// language that needs one. All of that already exists and is already correct in EDIT, so the
/// editor borrows it for the duration of the edit and takes the string back at the end.
///
/// The control is a child window sitting exactly over the annotation's box, styled to match the
/// canvas, and destroyed the moment editing finishes - so the rest of the app remains one D2D
/// surface with no widget tree.
/// </summary>
internal sealed class TextEditor : IDisposable
{
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int ES_MULTILINE = 0x0004;
    private const int ES_AUTOVSCROLL = 0x0040;
    private const int ES_WANTRETURN = 0x1000;

    private const uint WM_SETFONT = 0x0030;
    private const uint WM_SETTEXT = 0x000C;
    private const uint WM_GETTEXT = 0x000D;
    private const uint WM_GETTEXTLENGTH = 0x000E;
    private const uint EM_SETSEL = 0x00B1;

    private readonly IntPtr _handle;
    private readonly IntPtr _font;
    private IntPtr _background;

    /// <summary>The annotation being edited. The window commits back into it.</summary>
    public Annotation Annotation { get; }

    private TextEditor(IntPtr handle, IntPtr font, Annotation annotation)
    {
        _handle = handle;
        _font = font;
        Annotation = annotation;
    }

    /// <summary>
    /// Answers WM_CTLCOLOREDIT so the box paints the annotation's colour on the image's own
    /// backdrop instead of system white. Returns the brush the control should use for its
    /// background, or zero if the message is not ours.
    ///
    /// The parent must forward the message here: a child EDIT asks its parent what colours to use,
    /// which is the one place the host has to cooperate to make the box look like it belongs to the
    /// canvas rather than being a control dropped on top of it.
    /// </summary>
    public IntPtr OnCtlColor(IntPtr deviceContext, IntPtr control, Rgba backdrop)
    {
        if (control != _handle) return IntPtr.Zero;

        var text = Palette.Parse(Annotation.ColorHex);
        SetTextColor(deviceContext, Bgr(text));
        SetBkColor(deviceContext, Bgr(backdrop));

        if (_background != IntPtr.Zero) DeleteObject(_background);
        _background = CreateSolidBrush(Bgr(backdrop));
        return _background;
    }

    /// <summary>GDI colours are 0x00BBGGRR, not RGB.</summary>
    private static int Bgr(Rgba color) => color.R | (color.G << 8) | (color.B << 16);

    /// <summary>
    /// Opens an editor over <paramref name="bounds"/>, in client pixels. The font is built at the
    /// annotation's on-screen size, so what is typed sits where it will be drawn.
    /// </summary>
    public static TextEditor? Open(
        IntPtr parent, Annotation annotation, Rect bounds, double scale)
    {
        var height = (int)Math.Round(annotation.FontSize * scale);

        var font = CreateFontW(
            -height, 0, 0, 0,
            annotation.IsBold ? 700 : 400,
            annotation.IsItalic ? 1u : 0u,
            annotation.IsUnderline ? 1u : 0u,
            0,
            1,      // DEFAULT_CHARSET
            0, 0,
            4,      // CLEARTYPE_QUALITY... close enough; the EDIT is transient
            0,
            "Segoe UI");

        var handle = CreateWindowExW(
            0,
            "EDIT",
            annotation.Text,
            WS_CHILD | WS_VISIBLE | ES_MULTILINE | ES_AUTOVSCROLL | ES_WANTRETURN,
            (int)Math.Round(bounds.X), (int)Math.Round(bounds.Y),
            (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height),
            parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (handle == IntPtr.Zero)
        {
            if (font != IntPtr.Zero) DeleteObject(font);
            return null;
        }

        SendMessageW(handle, WM_SETFONT, font, 1);
        SetFocus(handle);

        // Select everything, so typing replaces a placeholder and an edit of existing text starts
        // from a sensible place.
        SendMessageW(handle, EM_SETSEL, 0, -1);

        return new TextEditor(handle, font, annotation);
    }

    /// <summary>The text currently in the box.</summary>
    public string Text
    {
        get
        {
            var length = (int)SendMessageW(_handle, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
            if (length <= 0) return string.Empty;

            var buffer = Marshal.AllocHGlobal((length + 1) * 2);
            try
            {
                SendMessageW(_handle, WM_GETTEXT, length + 1, buffer);
                return Marshal.PtrToStringUni(buffer) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    public bool Owns(IntPtr handle) => handle == _handle;

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) DestroyWindow(_handle);
        if (_font != IntPtr.Zero) DeleteObject(_font);
        if (_background != IntPtr.Zero) DeleteObject(_background);
    }

    [DllImport("gdi32.dll")]
    private static extern int SetTextColor(IntPtr deviceContext, int color);

    [DllImport("gdi32.dll")]
    private static extern int SetBkColor(IntPtr deviceContext, int color);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(int color);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int exStyle, string className, string? windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, int wParam, int lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, int wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr window);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(
        int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet,
        uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily,
        string faceName);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
}
