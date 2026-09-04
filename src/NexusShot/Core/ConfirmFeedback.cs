namespace NexusShot.Core;

/// <summary>
/// The cross-fade a button runs after a one-shot action: the glyph swaps to a tick, holds, and
/// swaps back.
///
/// Timing and easing only - no window, no timer. A view starts it, asks for the progress while
/// drawing, and asks how long until the next frame is worth painting.
/// </summary>
public sealed class ConfirmFeedback
{
    private const int TransitionMs = 160;
    private const int HoldUntilMs = 1640;
    private const int DurationMs = HoldUntilMs + TransitionMs;

    private long? _startedAt;

    public bool IsRunning => _startedAt is not null;

    public void Start(long now) => _startedAt = now;

    public void Stop() => _startedAt = null;

    /// <summary>0 for the plain glyph, 1 for the tick, eased through the swap in both directions.</summary>
    public double Progress(long now)
    {
        if (_startedAt is not { } started) return 0;

        var elapsed = now - started;
        var progress = elapsed < TransitionMs
            ? elapsed / (double)TransitionMs
            : (DurationMs - elapsed) / (double)TransitionMs;

        progress = Math.Clamp(progress, 0, 1);
        return progress * progress * (3 - 2 * progress);
    }

    /// <summary>
    /// Milliseconds until the next frame worth drawing, or null once the feedback is over and the
    /// caller should stop its timer. The hold between the two swaps is one long wait rather than a
    /// second of repaints that would each draw the same tick.
    /// </summary>
    public uint? NextFrameDelay(long now)
    {
        if (_startedAt is not { } started) return null;

        var elapsed = now - started;
        if (elapsed >= DurationMs)
        {
            _startedAt = null;
            return null;
        }

        return elapsed >= TransitionMs && elapsed < HoldUntilMs
            ? (uint)(HoldUntilMs - elapsed)
            : 16u;
    }
}
