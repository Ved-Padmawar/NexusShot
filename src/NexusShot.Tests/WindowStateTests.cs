using NexusShot.Render;
using NexusShot.Views;

namespace NexusShot.Tests;

public class WindowStateTests
{
    [Fact]
    public void ClosingDispatchRejectsLateWorkerResults()
    {
        var dispatch = new UiThreadDispatch(0);
        var calls = 0;
        dispatch.Post(() => calls++);
        dispatch.Drain();
        Assert.Equal(1, calls);
        calls = 0;
        dispatch.Post(() => calls++);
        dispatch.Clear();
        dispatch.Post(() => calls++);
        dispatch.Drain();
        Assert.Equal(0, calls);
    }

    [Fact]
    public void EditorsKeepIndependentDpiMetrics()
    {
        // Layout metrics do not access the drawing target.
        var first = new EditorChrome(null!) { Scale = 1, CaptionHeight = 32 };
        var second = new EditorChrome(null!) { Scale = 2, CaptionHeight = 64 };
        Assert.Equal(78, first.ChromeTop);
        Assert.Equal(156, second.ChromeTop);
        second.Scale = 1.5;
        Assert.Equal(40, first.FooterHeight);
        Assert.Equal(60, second.FooterHeight);
    }
}
