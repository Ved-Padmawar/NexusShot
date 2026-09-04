using NexusShot.Core;

namespace NexusShot.Tests;

/// <summary>
/// The capture name is the only record of when a shot was taken that survives the file moving, so
/// what <see cref="CaptureName.For"/> writes has to read back through <see cref="CaptureName.TryParseTime"/>.
/// </summary>
public class CaptureNameTests
{
    [Fact]
    public void AWrittenNameReadsBackAsTheSameTime()
    {
        var when = new DateTime(2026, 8, 31, 1, 59, 25);

        Assert.True(CaptureName.TryParseTime(CaptureName.For(when) + ".png", out var parsed));
        Assert.Equal(when, parsed);
    }

    [Fact]
    public void ACollisionCounterDoesNotHideTheTime()
    {
        // Two captures inside the same second: the second gets `_001` appended.
        var when = new DateTime(2026, 8, 31, 1, 59, 25);
        var name = CaptureName.For(when) + "_001.png";

        Assert.True(CaptureName.TryParseTime(name, out var parsed));
        Assert.Equal(when, parsed);
    }

    [Fact]
    public void AFileTheAppDidNotNameIsRefused()
    {
        // Left to the caller's file-system fallback rather than guessed at.
        Assert.False(CaptureName.TryParseTime("holiday.png", out _));
        Assert.False(CaptureName.TryParseTime("Screenshot 2026-08-31 01.59.25.png", out _));
        Assert.False(CaptureName.TryParseTime("NexusShot not-a-date.png", out _));
        Assert.False(CaptureName.TryParseTime("NexusShot 2026-08-31 01.59.25_draft.png", out _));
    }

    [Fact]
    public void OrderingSurvivesFilesWhoseCreationTimeWasRewritten()
    {
        // The reinstall case: every file arrives with the same creation time, so only the name
        // still says which capture came first.
        var names = new[]
        {
            CaptureName.For(new DateTime(2026, 8, 31, 4, 21, 6)) + ".png",
            CaptureName.For(new DateTime(2026, 8, 31, 1, 59, 25)) + ".png",
            CaptureName.For(new DateTime(2026, 8, 31, 2, 32, 49)) + ".png",
        };

        var times = names.Select(name =>
        {
            Assert.True(CaptureName.TryParseTime(name, out var parsed));
            return parsed;
        }).OrderByDescending(time => time).ToArray();

        Assert.Equal(new DateTime(2026, 8, 31, 4, 21, 6), times[0]);
        Assert.Equal(new DateTime(2026, 8, 31, 2, 32, 49), times[1]);
        Assert.Equal(new DateTime(2026, 8, 31, 1, 59, 25), times[2]);
    }
}
