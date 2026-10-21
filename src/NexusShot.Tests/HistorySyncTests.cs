using System.Security.AccessControl;
using NexusShot.Core;
using NexusShot.Platform;

namespace NexusShot.Tests;

/// <summary>How a folder scan lands in the history: one scan at a time, and findings checked against
/// the disk as it is when they arrive.</summary>
public class HistorySyncTests
{
    private static readonly FileVersion V1 = new(10, new DateTime(2026, 1, 1));
    private static readonly FileVersion V2 = new(20, new DateTime(2026, 1, 2));

    private static ScreenshotHistoryItem Item(string path, int day) =>
        new() { FilePath = path, CapturedAt = new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero) };

    private static HistoryScan Scan(
        (ScreenshotHistoryItem, FileVersion)[]? changed = null, string[]? missing = null) =>
        new(changed ?? [], missing ?? [], []);

    [Fact]
    public void ARequestDuringAScanRunsOnceItEnds()
    {
        var sync = new HistorySync();

        Assert.True(sync.TryBegin());
        Assert.False(sync.TryBegin());
        Assert.False(sync.TryBegin());

        var result = sync.Complete([], Scan(), stale: false, _ => false, _ => V1);
        Assert.True(result.ScanAgain);
        Assert.True(sync.TryBegin());
        Assert.False(sync.Complete([], Scan(), stale: false, _ => false, _ => V1).ScanAgain);
    }

    [Fact]
    public void AScanOfAFolderNoLongerInUseIsDiscardedForAFreshOne()
    {
        var sync = new HistorySync();
        List<ScreenshotHistoryItem> history = [Item(@"C:\old\a.png", 1)];
        sync.TryBegin();

        var result = sync.Complete(history, Scan(missing: [@"C:\old\a.png"]), stale: true, _ => false, _ => V1);

        Assert.Single(history);
        Assert.True(result.ScanAgain);
    }

    [Fact]
    public void AMissingFileIsDroppedUnlessItWasRecreatedSince()
    {
        var sync = new HistorySync();
        List<ScreenshotHistoryItem> history = [Item("gone.png", 1), Item("back.png", 2)];
        sync.TryBegin();

        var result = sync.Complete(history, Scan(missing: ["gone.png", "back.png"]), stale: false,
            path => path == "back.png", _ => V1);

        Assert.Equal(["back.png"], history.Select(item => item.FilePath));
        Assert.Equal(["gone.png"], result.Removed);
        Assert.True(result.Changed);
    }

    [Fact]
    public void NewFilesAreAdoptedNewestFirstAndKnownOnesRefreshed()
    {
        var sync = new HistorySync();
        var known = Item("known.png", 2);
        List<ScreenshotHistoryItem> history = [known];
        sync.TryBegin();

        var resized = Item("known.png", 2);
        resized.Width = 640;
        var result = sync.Complete(history, Scan(changed: [(Item("new.png", 3), V1), (resized, V1)]), stale: false,
            _ => true, _ => V1);

        Assert.Equal(["new.png", "known.png"], history.Select(item => item.FilePath));
        var (refreshed, version) = Assert.Single(result.Refreshed);
        Assert.Same(known, refreshed);
        Assert.Equal(V1, version);
        Assert.Equal(640, known.Width);
        Assert.Equal(V1, sync.Versions()["new.png"]);
    }

    [Fact]
    public void AFileRewrittenAfterTheScanIsLeftForAnotherScan()
    {
        var sync = new HistorySync();
        List<ScreenshotHistoryItem> history = [];
        sync.TryBegin();

        var result = sync.Complete(history, Scan(changed: [(Item("busy.png", 1), V1)]), stale: false,
            _ => true, _ => V2);

        Assert.Empty(history);
        Assert.False(result.Changed);
        Assert.True(result.ScanAgain);
        Assert.False(sync.Versions().ContainsKey("busy.png"));
    }

    [Fact]
    public void ACaptureIsDeletedOnlyWhenWindowsCanSayItIsGone()
    {
        var folder = Directory.CreateTempSubdirectory("nexusshot-denied-");
        var hidden = Path.Combine(folder.FullName, "a.png");
        File.WriteAllBytes(hidden, [0]);
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny);
        var security = folder.GetAccessControl();
        security.AddAccessRule(deny);
        folder.SetAccessControl(security);
        try
        {
            Assert.False(HistoryScanner.IsDeleted(hidden));
            Assert.True(HistoryScanner.IsDeleted(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png")));
            Assert.False(HistoryScanner.IsDeleted(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "a.png")));
        }
        finally
        {
            security.RemoveAccessRule(deny);
            folder.SetAccessControl(security);
            folder.Delete(recursive: true);
        }
    }
}
