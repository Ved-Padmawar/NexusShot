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
        var first = new EditorChrome(null!) { Scale = 1 };
        var second = new EditorChrome(null!) { Scale = 2 };
        Assert.Equal(52, first.TopBand);
        Assert.Equal(104, second.TopBand);
        second.Scale = 1.5;
        Assert.Equal(new Core.Rect(76, 60, 884, 564), first.Well(1000, 700));
        Assert.Equal(new Core.Rect(114, 90, 826, 496), second.Well(1000, 700));
    }
}
