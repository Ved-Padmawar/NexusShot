using NexusShot.Core;

namespace NexusShot.Tests;

/// <summary>
/// The confirm cross-fade's timing. It was written out inside FloatingPreview, where the only way
/// to check it was to click the button and watch.
/// </summary>
public class ConfirmFeedbackTests
{
    [Fact]
    public void IdleFeedbackShowsThePlainGlyph()
    {
        var feedback = new ConfirmFeedback();

        Assert.False(feedback.IsRunning);
        Assert.Equal(0, feedback.Progress(1000));
        Assert.Null(feedback.NextFrameDelay(1000));
    }

    [Fact]
    public void ProgressRisesToTheTickAndBackToTheGlyph()
    {
        var feedback = new ConfirmFeedback();
        feedback.Start(0);

        Assert.Equal(0, feedback.Progress(0), 3);
        Assert.True(feedback.Progress(80) is > 0 and < 1);
        Assert.Equal(1, feedback.Progress(160), 3);

        // Held at the tick, then eased back out.
        Assert.Equal(1, feedback.Progress(1000), 3);
        Assert.True(feedback.Progress(1720) is > 0 and < 1);
        Assert.Equal(0, feedback.Progress(1800), 3);
    }

    [Fact]
    public void TheHoldIsOneLongWaitRatherThanAFrameEveryTick()
    {
        var feedback = new ConfirmFeedback();
        feedback.Start(0);

        // Animating through the swap.
        Assert.Equal(16u, feedback.NextFrameDelay(0));

        // Holding: one wait until the swap back, not a repaint per frame of the same tick.
        Assert.Equal((uint)(1640 - 200), feedback.NextFrameDelay(200));

        // Animating again on the way out.
        Assert.Equal(16u, feedback.NextFrameDelay(1700));
    }

    [Fact]
    public void TheFeedbackStopsItselfOnceItIsOver()
    {
        var feedback = new ConfirmFeedback();
        feedback.Start(0);

        Assert.Null(feedback.NextFrameDelay(1800));
        Assert.False(feedback.IsRunning);
        Assert.Equal(0, feedback.Progress(1800));
    }

    [Fact]
    public void ASecondConfirmRestartsTheHold()
    {
        var feedback = new ConfirmFeedback();
        feedback.Start(0);
        feedback.Start(1000);

        Assert.Equal(0, feedback.Progress(1000), 3);
        Assert.Equal(1, feedback.Progress(1160), 3);
    }

    [Fact]
    public void StopClearsAFailedActionImmediately()
    {
        var feedback = new ConfirmFeedback();
        feedback.Start(0);
        feedback.Stop();

        Assert.False(feedback.IsRunning);
        Assert.Equal(0, feedback.Progress(80));
    }
}
