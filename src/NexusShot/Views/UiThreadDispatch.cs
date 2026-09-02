using NexusShot.Platform;

namespace NexusShot.Views;

/// <summary>
/// Defers work onto the window's own message queue, to run once the current message handler (or
/// modal loop - DoDragDrop, a file picker, region-pick) has unwound. Needed because reflow, resource
/// reload, and reentrant handler calls touch SetWindowPos/render-target rebuilds, which fail with
/// D2DERR_WRONG_STATE between BeginDraw and EndDraw, or deadlock a modal loop that owns the pump.
/// </summary>
public sealed class UiThreadDispatch(IntPtr handle)
{
    /// <summary>The message every window drains on. One value for all of them: the id is scoped to
    /// the HWND it is posted to, so there is nothing to collide with.</summary>
    public const uint Message = 0x8000;   // WM_APP

    private readonly Queue<Action> _posted = new();
    private bool _closed;

    /// <summary>Queues <paramref name="work"/> and wakes the window's message loop to run it.</summary>
    public void Post(Action work)
    {
        lock (_posted)
        {
            if (_closed) return;
            _posted.Enqueue(work);
        }
        WindowInterop.PostMessageW(handle, Message, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Drops anything queued without running it. For a window being destroyed - work still
    /// pending outlived the frame it was going to run against.</summary>
    public void Clear()
    {
        lock (_posted)
        {
            _closed = true;
            _posted.Clear();
        }
    }

    /// <summary>Call from WindowProc when <see cref="Message"/> arrives. Runs everything queued so
    /// far, in order.</summary>
    public void Drain()
    {
        Action[] work;
        lock (_posted)
        {
            work = [.. _posted];
            _posted.Clear();
        }
        foreach (var item in work)
        {
            // A callback can destroy its own window. Clear must also stop this detached batch.
            lock (_posted) if (_closed) break;
            item();
        }
    }
}
