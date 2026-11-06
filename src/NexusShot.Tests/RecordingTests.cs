using NexusShot.Core;

namespace NexusShot.Tests;

public class RecordingTests
{
    private static RecordingPlan Plan(RecordingFormat format, int width, int height) =>
        new(new AppSettings { RecordingFormat = format }, width, height);

    /// <summary>A clock that moves only when told, in whole seconds.</summary>
    private sealed class ManualTime
    {
        public long Ticks;
        public void Advance(int seconds) => Ticks += seconds * TimeSpan.TicksPerSecond;
    }

    [Fact]
    public void VideoAlignsOddDimensionsForH264()
    {
        var plan = Plan(RecordingFormat.Mp4, 801, 603);
        Assert.Equal((800, 602), (plan.Width, plan.Height));
        Assert.Equal(30, plan.FramesPerSecond);
        Assert.Equal(".mp4", plan.Extension);
    }

    [Fact]
    public void GifKeepsSmallOddDimensions()
    {
        var plan = Plan(RecordingFormat.Gif, 801, 603);
        Assert.Equal((801, 603), (plan.Width, plan.Height));
        Assert.Equal(10, plan.FramesPerSecond);
        Assert.Equal(".gif", plan.Extension);
    }

    [Fact]
    public void VideoScalesLargeDesktopsProportionally() =>
        Assert.Equal((3840, 2160), (Plan(RecordingFormat.Mp4, 7680, 4320).Width, Plan(RecordingFormat.Mp4, 7680, 4320).Height));

    [Fact]
    public void GifScalesPortraitAreasProportionally()
    {
        var plan = Plan(RecordingFormat.Gif, 1080, 1920);
        Assert.Equal((405, 720), (plan.Width, plan.Height));
    }

    [Fact]
    public void InvalidFormatIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan((RecordingFormat)42, 100, 100));

    [Fact]
    public void TinyAreasAreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan(RecordingFormat.Mp4, 1, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan(RecordingFormat.Gif, 100, 0));
    }

    [Fact]
    public void VideoFollowsTheFrameRateAndQualitySettings()
    {
        var standard = new RecordingPlan(new AppSettings { RecordingFrameRate = 60 }, 1920, 1080);
        var high = new RecordingPlan(new AppSettings { RecordingFrameRate = 60, RecordingQuality = RecordingQuality.High }, 1920, 1080);
        Assert.Equal(60, standard.FramesPerSecond);
        Assert.True(high.BitsPerSecond > standard.BitsPerSecond);
    }

    [Fact]
    public void OnlyGifIsCutAtItsLengthLimit()
    {
        Assert.Equal(TimeSpan.FromSeconds(15),
            new RecordingPlan(new AppSettings { RecordingFormat = RecordingFormat.Gif, GifLimitSeconds = 15 }, 100, 100).Limit);
        Assert.Null(new RecordingPlan(new AppSettings { RecordingFormat = RecordingFormat.Gif, GifLimitSeconds = 0 }, 100, 100).Limit);
        Assert.Null(new RecordingPlan(new AppSettings { GifLimitSeconds = 15 }, 100, 100).Limit);
    }

    [Fact]
    public void TheClockHoldsWhilePaused()
    {
        var time = new ManualTime();
        var clock = new RecordingClock(() => time.Ticks);
        clock.Run();
        time.Advance(3);
        clock.Hold();
        time.Advance(10);
        Assert.Equal(TimeSpan.FromSeconds(3), clock.Elapsed);
        clock.Run();
        time.Advance(2);
        Assert.Equal(TimeSpan.FromSeconds(5), clock.Elapsed);
    }

    [Fact]
    public void ASecondRecordingCannotClaimAnActiveSession()
    {
        var state = new RecordingState();
        Assert.True(state.TryBegin());
        Assert.False(state.TryBegin());
        state.Start();
        Assert.False(state.TryBegin());
    }

    [Fact]
    public void StartingRequiresACountdown() =>
        Assert.Throws<InvalidOperationException>(() => new RecordingState().Start());

    [Fact]
    public void PausingHoldsTheRecordedTime()
    {
        var time = new ManualTime();
        var state = new RecordingState(() => time.Ticks);
        state.TryBegin();
        time.Advance(3);
        state.Start();
        time.Advance(4);
        Assert.True(state.TogglePause());
        Assert.Equal(RecordingPhase.Paused, state.Phase);
        time.Advance(20);
        Assert.True(state.TogglePause());
        time.Advance(1);
        Assert.Equal(TimeSpan.FromSeconds(5), state.Clock.Elapsed);
    }

    [Fact]
    public void PauseOutsideARecordingDoesNothing()
    {
        var state = new RecordingState();
        Assert.False(state.TogglePause());
        state.TryBegin();
        Assert.False(state.TogglePause());
        Assert.Equal(RecordingPhase.Countdown, state.Phase);
    }

    [Fact]
    public void StopKeepsTheSessionBusyUntilEncodingFinishes()
    {
        var state = new RecordingState();
        state.TryBegin();
        state.Start();
        state.TogglePause();
        Assert.True(state.Stop(false));
        Assert.Equal(RecordingPhase.Finalizing, state.Phase);
        Assert.True(state.IsBusy);
        Assert.False(state.Clock.IsRunning);
        Assert.False(state.Stop(true));
    }

    [Fact]
    public void DiscardAppliesOnlyToTheCurrentRecording()
    {
        var state = new RecordingState();
        state.TryBegin();
        state.Start();
        Assert.True(state.Stop(true));
        Assert.True(state.Discard);
        state.Finish();
        Assert.False(state.IsBusy);
        Assert.True(state.TryBegin());
        Assert.False(state.Discard);
    }

    [Fact]
    public void EachRecordingStartsItsOwnClock()
    {
        var time = new ManualTime();
        var state = new RecordingState(() => time.Ticks);
        state.TryBegin();
        state.Start();
        time.Advance(9);
        state.Stop(false);
        var finishing = state.Clock;
        state.Finish();
        state.TryBegin();
        Assert.Equal(TimeSpan.Zero, state.Clock.Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(9), finishing.Elapsed);
    }

    [Fact]
    public void StopDuringTheCountdownDoesNothing()
    {
        var state = new RecordingState();
        state.TryBegin();
        Assert.False(state.Stop(false));
        state.Finish();
        Assert.True(state.TryBegin());
    }

    [Fact]
    public void VideoOffersOnlyRectangularModes()
    {
        var picker = new RegionPicker(new Size(100, 100), [], [], null, PickerMode.Region, CaptureKind.Video);
        picker.NextMode();
        Assert.Equal(PickerMode.Window, picker.Mode);
        picker.NextMode();
        Assert.Equal(PickerMode.Screen, picker.Mode);
        picker.NextMode();
        Assert.Equal(PickerMode.Region, picker.Mode);
        picker.SetMode(PickerMode.Freeform);
        picker.SetMode(PickerMode.Text);
        Assert.Equal(PickerMode.Region, picker.Mode);
    }

    [Fact]
    public void SwitchingToVideoLeavesAModeItLacks()
    {
        var picker = new RegionPicker(new Size(100, 100), [], [], null, PickerMode.Freeform);
        picker.SetKind(CaptureKind.Video);
        Assert.Equal(PickerMode.Region, picker.Mode);
        picker.SetKind(CaptureKind.Photo);
        picker.SetMode(PickerMode.Text);
        Assert.Equal(PickerMode.Text, picker.Mode);
    }

    [Fact]
    public void AVideoPickAsksForTheChosenFormat()
    {
        var picker = new RegionPicker(new Size(100, 100), [], [], null, PickerMode.Region, CaptureKind.Video);
        picker.SetFormat(RecordingFormat.Gif);
        picker.Press(new Point(10, 10));
        Assert.Equal(RecordingFormat.Gif, picker.Release(new Point(60, 50))!.Recording);

        picker.SetKind(CaptureKind.Photo);
        picker.Press(new Point(10, 10));
        Assert.Null(picker.Release(new Point(60, 50))!.Recording);
    }

    [Fact]
    public void MatchingFramesHaveNoChange()
    {
        uint[] frame = [1, 2, 3, 4, 5, 6];
        Assert.Null(FrameDiff.ChangedBounds(frame, frame, 3));
    }

    [Fact]
    public void TheChangeIsBoundedByEveryChangedPixel()
    {
        var before = new uint[4 * 5];
        var after = (uint[])before.Clone();
        after[1 * 4 + 2] = 9;
        after[3 * 4 + 1] = 9;
        Assert.Equal((1, 1, 2, 3), FrameDiff.ChangedBounds(before, after, 4));
    }

    [Fact]
    public void ScreenshotsAndRecordingsShareOneLibrary()
    {
        var settings = new AppSettings { ScreenshotFolder = @"C:\Shots", RecordingFolder = @"D:\Videos\NexusShot" };
        Assert.Equal([@"C:\Shots", @"D:\Videos\NexusShot"], settings.LibraryFolders());
        Assert.True(settings.InLibrary(@"D:\Videos\NexusShot\clip.mp4"));
        Assert.False(settings.InLibrary(@"C:\Shots-elsewhere\clip.mp4"));

        settings.RecordingFolder = @"C:\Shots\";
        Assert.Equal([@"C:\Shots"], settings.LibraryFolders());
    }

    [Fact]
    public void RecordingsBelongToTheLibraryWithoutEnteringTheStillImageEditor()
    {
        Assert.True(CaptureFiles.CanOpen("capture.MP4"));
        Assert.True(CaptureFiles.CanOpen("capture.GIF"));
        Assert.True(CaptureFiles.IsRecording("capture.GIF"));
        Assert.False(ImageFiles.CanOpen("capture.gif"));
        Assert.False(ImageFiles.CanOpen("capture.mp4"));
        Assert.True(CaptureFiles.CanOpen("capture.png"));
        Assert.False(CaptureFiles.IsRecording("capture.png"));
        Assert.False(CaptureFiles.CanOpen("capture.tmp"));
    }
}
