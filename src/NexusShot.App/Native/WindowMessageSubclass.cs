using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NexusShot.App.Native;

/// <summary>Routes native window messages without owning a second message loop.</summary>
public sealed class WindowMessageSubclass : IDisposable
{
    private readonly NativeMethods.WindowProc _windowProc;
    private IntPtr _windowHandle;
    private IntPtr _previousWindowProc;
    private bool _disposed;
    private bool _dispatching;
    private bool _restorePending;

    public WindowMessageSubclass(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero) throw new ArgumentException("A valid window handle is required.", nameof(windowHandle));
        _windowHandle = windowHandle;
        _windowProc = WndProc;
        Marshal.SetLastPInvokeError(0);
        _previousWindowProc = NativeMethods.SetWindowLongPtr(windowHandle, NativeMethods.GwlWndProc, Marshal.GetFunctionPointerForDelegate(_windowProc));
        if (_previousWindowProc == IntPtr.Zero && Marshal.GetLastPInvokeError() != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to subclass the application window.");
    }

    public event EventHandler<NativeWindowMessageEventArgs>? MessageReceived;

    private IntPtr WndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        _dispatching = true;
        try
        {
            var args = new NativeWindowMessageEventArgs(message, wParam, lParam);
            try { MessageReceived?.Invoke(this, args); }
            catch { /* Never allow managed exceptions to escape the native window procedure. */ }
            return args.Handled
                ? args.Result
                : CallPreviousWindowProcedure(hWnd, message, wParam, lParam);
        }
        finally
        {
            _dispatching = false;
            if (message == NativeMethods.WmNcDestroy)
            {
                _windowHandle = IntPtr.Zero;
                _previousWindowProc = IntPtr.Zero;
            }
            else if (_restorePending) RestoreWindowProcedure();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_dispatching) _restorePending = true;
        else RestoreWindowProcedure();
        GC.SuppressFinalize(this);
    }

    private IntPtr CallPreviousWindowProcedure(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam) =>
        _previousWindowProc == IntPtr.Zero
            ? NativeMethods.DefWindowProc(hWnd, message, wParam, lParam)
            : NativeMethods.CallWindowProc(_previousWindowProc, hWnd, message, wParam, lParam);

    private void RestoreWindowProcedure()
    {
        _restorePending = false;
        if (_windowHandle != IntPtr.Zero && _previousWindowProc != IntPtr.Zero)
            NativeMethods.SetWindowLongPtr(_windowHandle, NativeMethods.GwlWndProc, _previousWindowProc);
        _windowHandle = IntPtr.Zero;
        _previousWindowProc = IntPtr.Zero;
    }
}

public sealed class NativeWindowMessageEventArgs(uint message, IntPtr wParam, IntPtr lParam) : EventArgs
{
    public uint Message { get; } = message;
    public IntPtr WParam { get; } = wParam;
    public IntPtr LParam { get; } = lParam;
    public bool Handled { get; set; }
    public IntPtr Result { get; set; }
}
