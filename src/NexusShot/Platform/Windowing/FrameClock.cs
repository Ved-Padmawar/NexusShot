using System.Runtime.InteropServices;

namespace NexusShot.Platform;

/// <summary>
/// One tick per composed frame (DwmFlush), posted while something moves. WM_TIMER fires at best every
/// 15.6 ms - every other frame at 120 Hz. At most one tick is ever outstanding.
/// </summary>
public sealed partial class FrameClock : IDisposable
{
    public const uint Message = 0x8000 + 0x46; // WM_APP + 0x46

    private readonly IntPtr _window;
    private readonly ManualResetEventSlim _running = new(false);
    private readonly Thread _thread;
    private volatile bool _disposed;
    private int _pending;

    public FrameClock(IntPtr window)
    {
        _window = window;
        _thread = new Thread(Loop) { IsBackground = true, Name = "NexusShot frame clock" };
        _thread.Start();
    }

    public bool Running => _running.IsSet;

    public void Start() => _running.Set();

    public void Stop() => _running.Reset();

    /// <summary>Lets the next tick be posted.</summary>
    public void Acknowledge() => Interlocked.Exchange(ref _pending, 0);

    private void Loop()
    {
        while (!_disposed)
        {
            _running.Wait();
            if (_disposed) return;

            // DwmFlush fails while composition is off (a locked session): sleep a frame, never spin.
            if (DwmFlush() != 0) Thread.Sleep(8);
            if (_running.IsSet && Interlocked.Exchange(ref _pending, 1) == 0)
                PostMessageW(_window, Message, 0, 0);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _running.Set();
        _thread.Join(100);
        _running.Dispose();
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmFlush();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(IntPtr window, uint message, nuint wParam, nint lParam);
}
