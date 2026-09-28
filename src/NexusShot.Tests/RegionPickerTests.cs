using NexusShot.Core;

namespace NexusShot.Tests;

/// <summary>The region picker's rules: drag, click-to-window, square, move-whole, lasso, repeat.</summary>
public class RegionPickerTests
{
    private static readonly Size Desktop = new(1920, 1080);

    private static readonly Rect[] Screens = [new(0, 0, 1920, 1080)];

    private static RegionPicker NewPicker(Rect? last = null, params Rect[] windows) =>
        new(Desktop, windows, Screens, last, PickerMode.Region);

    [Fact]
    public void ADragSelectsWholePixels()
    {
        var picker = NewPicker();
        picker.Press(new Point(100.4, 100.6));
        picker.Move(new Point(300, 250));

        var result = picker.Release(new Point(300.2, 250.4));

        Assert.Equal(new Rect(100, 101, 200, 149), result!.Region);
        Assert.Null(result.Outline);
    }

    [Fact]
    public void AClickTakesTheTopmostWindowUnderItClippedToTheDesktop()
    {
        var front = new Rect(1800, 50, 400, 300);
        var behind = new Rect(0, 0, 1920, 1040);
        var picker = NewPicker(null, front, behind);

        picker.Move(new Point(1850, 100));
        Assert.Equal(new Rect(1800, 50, 120, 300), picker.Target);

        picker.Press(new Point(1850, 100));
        Assert.Equal(new Rect(1800, 50, 120, 300), picker.Release(new Point(1850, 100))!.Region);
    }

    [Fact]
    public void ADragInAnyDirectionSelectsTheSameRegion()
    {
        var picker = NewPicker();
        picker.Press(new Point(100, 100));
        Assert.Equal(new Rect(10, 20, 90, 80), picker.Release(new Point(10, 20))!.Region);
    }

    [Fact]
    public void AClickOnBareDesktopCancels()
    {
        var picker = NewPicker();
        picker.Press(new Point(10, 10));
        Assert.Null(picker.Release(new Point(11, 10)));
    }

    [Fact]
    public void ShiftSquaresTheDragOnItsLongerSide()
    {
        var picker = NewPicker();
        picker.Press(new Point(500, 500));

        var result = picker.Release(new Point(400, 560), square: true);

        Assert.Equal(new Rect(400, 500, 100, 100), result!.Region);
    }

    [Fact]
    public void SpaceMovesTheRectangleWithoutResizingIt()
    {
        var picker = NewPicker();
        picker.Press(new Point(100, 100));
        picker.Move(new Point(200, 180));
        picker.Move(new Point(250, 200), moveWhole: true);

        var result = picker.Release(new Point(250, 200));

        Assert.Equal(new Rect(150, 120, 100, 80), result!.Region);
    }

    [Fact]
    public void ALassoCarriesItsOutlineRelativeToItsBounds()
    {
        var picker = NewPicker();
        picker.SetMode(PickerMode.Freeform);
        picker.Press(new Point(100, 100));
        picker.Move(new Point(200, 100));
        picker.Move(new Point(200, 200));

        var result = picker.Release(new Point(100, 200));

        Assert.Equal(new Rect(100, 100, 100, 100), result!.Region);
        Assert.Equal([new Point(0, 0), new Point(100, 0), new Point(100, 100), new Point(0, 100)], result.Outline);
        Assert.Null(picker.Target);
    }

    [Fact]
    public void TheModeCannotChangeMidDrag()
    {
        var picker = NewPicker();
        picker.Press(new Point(100, 100));
        picker.NextMode();

        Assert.Equal(PickerMode.Region, picker.Mode);
        Assert.Null(picker.Release(new Point(300, 200))!.Outline);
    }

    [Fact]
    public void WindowModeTakesOnlyWhatIsClicked()
    {
        var window = new Rect(200, 200, 400, 300);
        var picker = new RegionPicker(Desktop, [window], Screens, null, PickerMode.Window);

        picker.Press(new Point(250, 250));
        picker.Move(new Point(500, 450));

        Assert.Null(picker.Selection);
        Assert.Equal(window, picker.Release(new Point(500, 450))!.Region);
    }

    [Fact]
    public void ScreenModeTakesTheMonitorUnderThePointer()
    {
        Rect[] screens = [new(0, 0, 960, 1080), new(960, 0, 960, 1080)];
        var picker = new RegionPicker(Desktop, [new Rect(0, 0, 1920, 1080)], screens, null, PickerMode.Screen);

        picker.Press(new Point(1500, 500));

        Assert.Equal(screens[1], picker.Release(new Point(1500, 500))!.Region);
    }

    [Fact]
    public void TextModeReadsADraggedAreaOrAClickedWindow()
    {
        var window = new Rect(200, 200, 400, 300);
        var picker = new RegionPicker(Desktop, [window], Screens, null, PickerMode.Text);

        picker.Press(new Point(10, 10));
        var area = picker.Release(new Point(110, 60))!;
        Assert.True(area.Text);
        Assert.Equal(new Rect(10, 10, 100, 50), area.Region);

        picker.SetTextScope(TextScope.Window);
        picker.Press(new Point(300, 300));
        Assert.Equal(window, picker.Release(new Point(380, 390))!.Region);
    }

    [Fact]
    public void RepeatingTheLastAreaInTextModeReadsIt()
    {
        var picker = new RegionPicker(Desktop, [], Screens, new Rect(10, 20, 30, 40), PickerMode.Text);

        var result = picker.RepeatLast()!;

        Assert.True(result.Text);
        Assert.Equal(new Rect(10, 20, 30, 40), result.Region);
    }

    [Fact]
    public void TabCyclesThroughEveryModeAndBack()
    {
        var picker = NewPicker();
        var seen = new List<PickerMode>();
        for (var i = 0; i < 5; i++)
        {
            seen.Add(picker.Mode);
            picker.NextMode();
        }

        Assert.Equal(Enum.GetValues<PickerMode>(), seen);
        Assert.Equal(PickerMode.Region, picker.Mode);
    }

    [Fact]
    public void TheLastRegionRepeatsOnlyWhereItStillFits()
    {
        Assert.Equal(new Rect(10, 20, 30, 40), NewPicker(new Rect(10, 20, 30, 40)).RepeatLast()!.Region);
        Assert.Equal(new Rect(1900, 0, 20, 40), NewPicker(new Rect(1900, 0, 300, 40)).RepeatLast()!.Region);
        Assert.Null(NewPicker(new Rect(3000, 0, 10, 10)).RepeatLast());
        Assert.Null(NewPicker().RepeatLast());
    }

    [Fact]
    public void ALassoFillsTheTriangleItEncloses()
    {
        Point[] triangle = [new(0, 0), new(10, 0), new(0, 10)];

        var runs = Polygon.InsideRuns(triangle, 10, 10).ToList();

        // A pixel counts when its centre is inside: the diagonal passes through the last centre of each row.
        Assert.Equal((0, 0, 9), runs[0]);
        Assert.Equal((8, 0, 1), runs[^1]);
        Assert.Equal(45, runs.Sum(run => run.End - run.Start));
    }

    [Fact]
    public void ACrossedLassoLeavesItsOverlapOut()
    {
        // Two squares traced as one path: the middle strip is crossed twice.
        Point[] figure = [new(0, 0), new(6, 0), new(6, 4), new(4, 4), new(4, 0), new(10, 0), new(10, 4), new(0, 4)];

        var run = Polygon.InsideRuns(figure, 10, 4).Where(run => run.Y == 2).ToList();

        Assert.Equal([(2, 0, 4), (2, 6, 10)], run);
    }
}
