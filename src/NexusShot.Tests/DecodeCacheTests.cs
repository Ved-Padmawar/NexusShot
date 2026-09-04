using NexusShot.Core;

namespace NexusShot.Tests;

/// <summary>
/// Admission rules for decodes that finish on a worker. These were previously written out at each
/// call site inside MainWindow, where they could only be exercised by running the app.
/// </summary>
public class DecodeCacheTests
{
    [Fact]
    public void CompletedDecodeIsAcceptedWhileItsGenerationStands()
    {
        var cache = new DecodeCache();
        Assert.True(cache.TryStart("a.png"));

        Assert.Equal(DecodeOutcome.Accept, cache.Finish("a.png", cache.Generation, succeeded: true));
    }

    [Fact]
    public void DecodeCompletingAfterInvalidateAllIsDroppedWithoutBeingBlamed()
    {
        var cache = new DecodeCache();
        cache.TryStart("a.png");
        var generation = cache.Generation;

        // The window was hidden and its visuals released while the worker was still decoding.
        cache.InvalidateAll();

        Assert.Equal(DecodeOutcome.Stale, cache.Finish("a.png", generation, succeeded: true));

        // Stale is not failure: the file was never shown to be unreadable, so it must stay eligible.
        Assert.False(cache.HasFailed("a.png"));
        Assert.True(cache.TryStart("a.png"));
    }

    [Fact]
    public void SecondDecodeOfTheSameFileIsRefusedWhileTheFirstIsRunning()
    {
        var cache = new DecodeCache();

        Assert.True(cache.TryStart("a.png"));
        Assert.False(cache.TryStart("a.png"));
        Assert.True(cache.IsRunning("a.png"));

        // A different file is unaffected: the guard is per file, not a global lock.
        Assert.True(cache.TryStart("b.png"));
    }

    [Fact]
    public void FinishingReleasesTheInFlightMarkSoTheFileCanBeDecodedAgain()
    {
        var cache = new DecodeCache();
        cache.TryStart("a.png");
        cache.Finish("a.png", cache.Generation, succeeded: true);

        Assert.False(cache.IsRunning("a.png"));
        Assert.True(cache.TryStart("a.png"));
    }

    [Fact]
    public void AStaleFinishStillClearsTheInFlightMark()
    {
        // Regression: an early return on the stale path would leave the file marked as decoding
        // forever, and no later repaint could ever start another decode of it.
        var cache = new DecodeCache();
        cache.TryStart("a.png");
        var generation = cache.Generation;
        cache.InvalidateAll();

        Assert.Equal(DecodeOutcome.Stale, cache.Finish("a.png", generation, succeeded: true));
        Assert.False(cache.IsRunning("a.png"));
    }

    [Fact]
    public void FailedDecodeIsNotRetried()
    {
        var cache = new DecodeCache();
        cache.TryStart("bad.png");

        Assert.Equal(DecodeOutcome.Failed, cache.Finish("bad.png", cache.Generation, succeeded: false));
        Assert.True(cache.HasFailed("bad.png"));

        // A corrupt image must not start a fresh decode on every frame that tries to draw it.
        Assert.False(cache.TryStart("bad.png"));
    }

    [Fact]
    public void InvalidatingOneFileLetsItBeDecodedAgainAfterFailing()
    {
        var cache = new DecodeCache();
        cache.TryStart("bad.png");
        cache.Finish("bad.png", cache.Generation, succeeded: false);

        // The file was rewritten on disk, so the earlier failure says nothing about it now.
        cache.Invalidate("bad.png");

        Assert.False(cache.HasFailed("bad.png"));
        Assert.True(cache.TryStart("bad.png"));
    }

    [Fact]
    public void InvalidatingOneFileAlsoRetiresDecodesOfOtherFiles()
    {
        // A save rewrites one file, but a decode of another may already be carrying pixels read
        // before that write. There is no per-file stamp to tell them apart, so the generation moves
        // for everyone.
        var cache = new DecodeCache();
        cache.TryStart("other.png");
        var generation = cache.Generation;

        cache.Invalidate("saved.png");

        Assert.Equal(DecodeOutcome.Stale, cache.Finish("other.png", generation, succeeded: true));
    }

    [Fact]
    public void InvalidateAllForgetsFailuresSoReplacedFilesAreTriedAgain()
    {
        var cache = new DecodeCache();
        cache.TryStart("bad.png");
        cache.Finish("bad.png", cache.Generation, succeeded: false);

        cache.InvalidateAll();

        Assert.False(cache.HasFailed("bad.png"));
        Assert.Equal(0, cache.FailureCount);
    }

    [Fact]
    public void DecodeForACaptureTheUserHasMovedOffIsStaleRatherThanFailed()
    {
        // The detail preview adds the selection to the same question: pixels that arrive after the
        // user selected something else are unwanted, but the file is still perfectly good.
        var cache = new DecodeCache();
        cache.TryStart("a.png");

        var outcome = cache.Finish("a.png", cache.Generation, succeeded: true, stillWanted: false);

        Assert.Equal(DecodeOutcome.Stale, outcome);
        Assert.False(cache.HasFailed("a.png"));
    }

    [Fact]
    public void ADecodeStartedBeforeTheFileWasSavedOverIsNotAdmitted()
    {
        // The editor saved over the capture while its thumbnail was being decoded. Those pixels are
        // the pre-save image, so admitting them would cache exactly the stale bitmap the save was
        // meant to replace.
        var cache = new DecodeCache();
        cache.TryStart("shot.png");
        var generation = cache.Generation;

        cache.Invalidate("shot.png");

        Assert.Equal(DecodeOutcome.Stale, cache.Finish("shot.png", generation, succeeded: true));

        // And the file is immediately eligible again, so the next frame decodes the new pixels.
        Assert.True(cache.TryStart("shot.png"));
    }

    [Fact]
    public void PathComparisonIgnoresCaseAsWindowsFilenamesDo()
    {
        var cache = new DecodeCache();
        cache.TryStart(@"C:\shots\A.png");

        Assert.True(cache.IsRunning(@"c:\shots\a.png"));
        Assert.False(cache.TryStart(@"c:\shots\a.png"));
    }

    [Fact]
    public void DecodesBeyondTheConcurrencyBoundAreRefused()
    {
        // Scrolling a long history reaches many files at once, each a distinct path that per-path
        // deduplication happily admits. Without a bound that is one WIC decoder per visible row.
        var cache = new DecodeCache();
        for (var i = 0; i < DecodeCache.MaxConcurrentDecodes; i++)
            Assert.True(cache.TryStart($"{i}.png"));

        Assert.False(cache.TryStart("overflow.png"));

        // A refusal is not a failure: the slot frees up and the file stays eligible.
        Assert.False(cache.HasFailed("overflow.png"));
        cache.Finish("0.png", cache.Generation, succeeded: true);
        Assert.True(cache.TryStart("overflow.png"));
    }
}
