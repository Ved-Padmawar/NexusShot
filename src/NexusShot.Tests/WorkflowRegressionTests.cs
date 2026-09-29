using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;
using NexusShot.Views;
using static NexusShot.Tests.Editing;

namespace NexusShot.Tests;

public class WorkflowRegressionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nexusshot-regression-{Guid.NewGuid():N}");

    public WorkflowRegressionTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void FailedRegionCaptureCanBeRetried()
    {
        var calls = 0;
        DecodedImage Fail(RectInt bounds) { calls++; throw new InvalidOperationException("injected capture failure"); }
        Assert.Throws<InvalidOperationException>(() => RegionOverlay.Pick(Fail));
        Assert.Throws<InvalidOperationException>(() => RegionOverlay.Pick(Fail));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void DirtyStateFollowsContentAndUndoInsteadOfSelection()
    {
        var document = NewDocument();
        Assert.False(document.HasUnsavedChanges);
        document.BeginCropSession();
        Assert.False(document.HasUnsavedChanges);
        document.CancelCropSession();
        Draw(document, EditorTool.Rectangle, new(10, 10), new(80, 80));
        Assert.True(document.HasUnsavedChanges);
        document.SelectAnnotation(null);
        Assert.True(document.HasUnsavedChanges);
        document.Undo();
        Assert.False(document.HasUnsavedChanges);
        document.Redo();
        Assert.True(document.HasUnsavedChanges);
        document.MarkSaved(document.Revision);
        Assert.False(document.HasUnsavedChanges);
    }

    [Fact]
    public void ExportPreparationDoesNotCommitCropAndSnapshotIsIndependent()
    {
        var document = NewDocument();
        var shape = Draw(document, EditorTool.Rectangle, new(10, 10), new(80, 80));
        document.BeginCropSession();
        var files = new EditorFiles(document, () => DecodedImage.Allocate(100, 100));
        files.OpenedAt(Path.Combine(_directory, "source.png"));
        using var request = files.PrepareSave();
        shape.Text = "changed after snapshot";
        Assert.NotSame(shape, request.Document.Annotations[0]);
        Assert.NotEqual(shape.Text, request.Document.Annotations[0].Text);
        Assert.NotNull(document.PendingCrop);
        Assert.Null(document.CropBounds);
        Assert.True(document.CanUndo);
        Assert.NotNull(request.Document.CropBounds);
    }

    [Fact]
    public void CancellingSaveAsDoesNotEvenCommitInlineText()
    {
        var files = new EditorFiles(NewDocument(), () => DecodedImage.Allocate(100, 100));
        files.OpenedAt(Path.Combine(_directory, "source.png"));
        var committed = false;
        files.Committing += () => committed = true;
        Assert.Null(files.PrepareSaveAs((_, _, _) => null));
        Assert.False(committed);
    }

    [Fact]
    public async Task FailedExportPreservesOriginalAndPendingEdits()
    {
        var source = await MakeImage(100, 100);
        var original = File.ReadAllBytes(source);
        var document = NewDocument();
        document.SetImageSize(100, 100);
        Draw(document, EditorTool.Rectangle, new(10, 10), new(80, 80));
        document.BeginCropSession();
        var files = new EditorFiles(document, () => ImageSurface.Decode(source));
        files.OpenedAt(source);
        using var request = files.PrepareSave();
        using (var locked = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = await Record.ExceptionAsync(() => MediaWorker.Run(() => { request.Save(); return true; }));
            Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        }
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.NotNull(document.PendingCrop);
        Assert.True(document.HasUnsavedChanges);
        Assert.True(document.CanUndo);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task SuccessfulSaveIsAdoptedOnlyOnCompletion()
    {
        var source = await MakeImage(100, 100);
        var document = NewDocument();
        document.SetImageSize(100, 100);
        Draw(document, EditorTool.Rectangle, new(10, 10), new(80, 80));
        var files = new EditorFiles(document, () => ImageSurface.Decode(source));
        files.OpenedAt(source);
        var destination = Path.Combine(_directory, "edited.png");
        using var request = files.PrepareSaveAs((_, _, _) => destination)!;
        await MediaWorker.Run(() => { request.Save(); return true; });
        Assert.True(document.HasUnsavedChanges);
        Assert.Equal(source, files.Path);
        files.CompleteSave(request);
        Assert.False(document.HasUnsavedChanges);
        Assert.Equal(destination, files.Path);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(destination));
    }

    [Fact]
    public async Task SaveStillWorksAfterTheOpenFileIsDeleted()
    {
        // Regression: the export re-read the source file, so an image moved or deleted while open
        // could no longer be saved at all. It now flattens the pixels on screen.
        var source = await MakeImage(100, 100);
        using var onScreen = ImageSurface.Decode(source);
        var document = NewDocument();
        document.SetImageSize(100, 100);
        Draw(document, EditorTool.Rectangle, new(10, 10), new(80, 80));
        var files = new EditorFiles(document, () => DecodedImage.CopyFrom(onScreen.Span, 100, 100));
        files.OpenedAt(source);
        File.Delete(source);

        using var request = files.PrepareSave();
        await MediaWorker.Run(() => { request.Save(); return true; });

        using var written = ImageSurface.Decode(source);
        Assert.Equal((100, 100), (written.Width, written.Height));
    }

    [Fact]
    public void AnUnreadableImageIsReportedOnceAndRetriedOnlyWhenItChanges()
    {
        // Regression: every capture's rescan retried a broken file and logged it again.
        var broken = Path.Combine(_directory, "broken.png");
        File.WriteAllBytes(broken, [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);

        var (path, version) = Assert.Single(HistoryScanner.Scan(_directory, [], new Dictionary<string, FileVersion>()).Unreadable);
        var versions = new Dictionary<string, FileVersion>(StringComparer.OrdinalIgnoreCase) { [path] = version };
        Assert.Empty(HistoryScanner.Scan(_directory, [], versions).Unreadable);

        File.AppendAllText(broken, "rewritten");
        Assert.Single(HistoryScanner.Scan(_directory, [], versions).Unreadable);
    }

    [Fact]
    public void ARememberedUnreadableImageThatIsDeletedIsReportedMissing()
    {
        var broken = Path.Combine(_directory, "broken.png");
        File.WriteAllBytes(broken, [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        var versions = new Dictionary<string, FileVersion>(StringComparer.OrdinalIgnoreCase) { [broken] = FileVersion.Read(broken) };

        File.Delete(broken);

        Assert.Contains(broken, HistoryScanner.Scan(_directory, [], versions).Missing);
    }

    [Fact]
    public async Task HistoryDetectsImportOverwriteAndDeleteWithoutRedecodingUnchangedFiles()
    {
        var path = await MakeImage(40, 30);
        var versions = new Dictionary<string, FileVersion>(StringComparer.OrdinalIgnoreCase);
        var first = HistoryScanner.Scan(_directory, [], versions);
        var found = Assert.Single(first.Changed);
        versions[path] = found.Version;
        Assert.Empty(HistoryScanner.Scan(_directory, [path], versions).Changed);
        await MakeImage(80, 60, path);
        var changed = Assert.Single(HistoryScanner.Scan(_directory, [path], versions).Changed);
        Assert.Equal(80, changed.Item.Width);
        Assert.Equal(60, changed.Item.Height);
        File.Delete(path);
        Assert.Contains(path, HistoryScanner.Scan(_directory, [path], versions).Missing);
    }

    [Fact]
    public async Task HistoryAdoptsAndDropsEveryFormatTheAppWrites()
    {
        var jpeg = Path.Combine(_directory, "photo.jpg");
        var bitmap = Path.Combine(_directory, "scan.bmp");
        await MediaWorker.Run(() =>
        {
            using var image = DecodedImage.Allocate(8, 6);
            image.Span.Fill(255);
            ImageWriter.Write(jpeg, image, ImageFormat.Jpeg);
            ImageWriter.Write(bitmap, image, ImageFormat.Bmp);
            return true;
        });
        File.WriteAllText(Path.Combine(_directory, "notes.txt"), "not an image");

        var found = HistoryScanner.Scan(_directory, [], new Dictionary<string, FileVersion>()).Changed;
        Assert.Equal(["photo.jpg", "scan.bmp"], found.Select(entry => entry.Item.FileName).Order());

        File.Delete(bitmap);
        Assert.Equal([bitmap], HistoryScanner.Scan(_directory, [jpeg, bitmap], found.ToDictionary(
            entry => entry.Item.FilePath, entry => entry.Version, StringComparer.OrdinalIgnoreCase)).Missing);
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".bmp")]
    public void TheWatcherReportsADeletedCaptureInEveryFormat(string extension)
    {
        var path = Path.Combine(_directory, "capture" + extension);
        File.WriteAllBytes(path, [0]);
        using var fired = new ManualResetEventSlim();
        using var watcher = new FolderWatcher(_directory, fired.Set);

        File.Delete(path);

        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)), $"no change reported for a deleted {extension}");
    }

    [Fact]
    public void TheWatcherRecoversWhenItsFolderComesBack()
    {
        var folder = Path.Combine(_directory, "drive");
        Directory.CreateDirectory(folder);
        using var fired = new ManualResetEventSlim();
        using var watcher = new FolderWatcher(folder, fired.Set);

        // Long enough for the watcher to fail while the folder is gone, as a dropped drive does.
        Directory.Delete(folder);
        Thread.Sleep(500);
        fired.Reset();
        Directory.CreateDirectory(folder);

        // A recovered watcher rescans; a dead one never reports again.
        Assert.True(fired.Wait(TimeSpan.FromSeconds(10)), "the watcher never came back");
    }

    [Fact]
    public void TheWatcherIgnoresFilesThatAreNotImages()
    {
        using var fired = new ManualResetEventSlim();
        using var watcher = new FolderWatcher(_directory, fired.Set);

        File.WriteAllText(Path.Combine(_directory, "notes.txt"), "x");

        Assert.False(fired.Wait(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task CaptureWritesDistinctFilesAndNoPartials()
    {
        await MediaWorker.Run(() =>
        {
            using var image = DecodedImage.Allocate(4, 4);
            image.Span.Fill(255);
            var first = CaptureStore.Save(image, _directory, ImageFormat.Png);
            var second = CaptureStore.Save(image, _directory, ImageFormat.Png);
            Assert.NotEqual(first.FilePath, second.FilePath);
            return true;
        });
        Assert.Equal(2, Directory.GetFiles(_directory, "*.png").Length);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Theory]
    [InlineData(false, 40)]
    [InlineData(true, 124)]
    public void ClipboardHeadersDoNotContainUninitializedMemory(bool v5, int headerSize)
    {
        var bytes = Enumerable.Repeat((byte)0xCD, headerSize + 4).ToArray();
        ClipboardImage.WriteDib(bytes, new byte[] { 20, 30, 40, 255 }, 1, 1, headerSize, 4, v5);
        Assert.All(bytes[24..40], value => Assert.Equal(0, value));
        if (v5) Assert.All(bytes[60..124], value => Assert.Equal(0, value));
        Assert.Equal(new byte[] { 20, 30, 40, 255 }, bytes[headerSize..]);
    }

    [Fact]
    public void ClipboardRowsAreFlippedAndOnlyTranslucentPixelsMeetWhite()
    {
        // Top row: two opaque pixels. Bottom row: half-transparent premultiplied red, then clear.
        byte[] pixels =
        [
            10, 20, 30, 255,   40, 50, 60, 255,
            0, 0, 128, 128,    0, 0, 0, 0,
        ];
        var dib = new byte[40 + pixels.Length];

        ClipboardImage.WriteDib(dib, pixels, 2, 2, 40, 8, v5: false);

        Assert.Equal(new byte[] { 127, 127, 255, 255, 255, 255, 255, 255 }, dib[40..48]);
        Assert.Equal(new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 }, dib[48..]);
    }

    [Fact]
    public async Task MediaWorkIsSerializedAndFailureDoesNotPoisonTheQueue()
    {
        var caller = Environment.CurrentManagedThreadId;
        var first = await MediaWorker.Run(() => Environment.CurrentManagedThreadId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => MediaWorker.Run<int>(() => throw new InvalidOperationException()));
        var next = await MediaWorker.Run(() => Environment.CurrentManagedThreadId);
        Assert.NotEqual(caller, first);
        Assert.Equal(first, next);
    }

    [Fact]
    public void FailedCopyDoesNotTruncateDestinationAndCopyingToSelfSucceeds()
    {
        var destination = Path.Combine(_directory, "existing.png");
        File.WriteAllText(destination, "original");
        Assert.Throws<FileNotFoundException>(() => AtomicFile.Copy(Path.Combine(_directory, "missing.png"), destination));
        Assert.Equal("original", File.ReadAllText(destination));
        AtomicFile.Copy(destination, destination);
        Assert.Equal("original", File.ReadAllText(destination));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void ClosingAWindowDisposesQueuedAndLateWorkerResultsExactlyOnce()
    {
        var dispatch = new UiThreadDispatch(IntPtr.Zero);
        var disposed = 0;
        dispatch.Post(dispatch.Clear);
        dispatch.Post(() => Assert.Fail("Closed window ran a callback"), () => disposed++);
        dispatch.Drain();
        dispatch.Post(() => Assert.Fail("Closed window accepted work"), () => disposed++);
        dispatch.Clear();
        Assert.Equal(2, disposed);
    }

    [Fact]
    public async Task MediaQueueRejectsExcessWorkWithoutBlockingTheCaller()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = MediaWorker.Run(() => { entered.Set(); release.Wait(); return 0; });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var waiting = new List<Task<int>>();
        try
        {
            for (var i = 0; i < 8; i++) waiting.Add(MediaWorker.Run(() => 1));
            await Assert.ThrowsAsync<InvalidOperationException>(() => MediaWorker.Run(() => 2));
            Assert.False(first.IsCompleted);
        }
        finally { release.Set(); }
        await first;
        Assert.Equal(8, (await Task.WhenAll(waiting)).Sum());
    }

    [Fact]
    public void OversizedPixelBuffersFailBeforeAllocation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DecodedImage.Allocate(int.MaxValue, 2));
    }

    private Task<string> MakeImage(int width, int height, string? path = null) => MediaWorker.Run(() =>
    {
        path ??= Path.Combine(_directory, "source.png");
        using var image = DecodedImage.Allocate(width, height);
        image.Span.Fill(255);
        ImageWriter.Write(path, image);
        return path;
    });
}
