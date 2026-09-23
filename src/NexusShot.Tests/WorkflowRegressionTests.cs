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
        document.ResetAfterSave();
        Assert.False(document.HasUnsavedChanges);
    }

    [Fact]
    public void ExportPreparationDoesNotCommitCropAndSnapshotIsIndependent()
    {
        var document = NewDocument();
        var shape = Draw(document, EditorTool.Rectangle, new(10, 10), new(80, 80));
        document.BeginCropSession();
        var files = new EditorFiles(document);
        files.OpenedAt(Path.Combine(_directory, "source.png"));
        var request = files.PrepareSave();
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
        var files = new EditorFiles(NewDocument());
        files.OpenedAt(Path.Combine(_directory, "source.png"));
        var committed = false;
        files.Committing += () => committed = true;
        Assert.Null(files.PrepareSaveAs((_, _) => null));
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
        var files = new EditorFiles(document);
        files.OpenedAt(source);
        var request = files.PrepareSave();
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
    public async Task SuccessfulSaveAdoptsPixelsOnlyOnCompletion()
    {
        var source = await MakeImage(100, 100);
        var document = NewDocument();
        document.SetImageSize(100, 100);
        Draw(document, EditorTool.Rectangle, new(10, 10), new(80, 80));
        var files = new EditorFiles(document);
        files.OpenedAt(source);
        var destination = Path.Combine(_directory, "edited.png");
        var request = files.PrepareSaveAs((_, _) => destination)!;
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
    public async Task AutoSaveDisabledDoesNotTouchTheConfiguredFolder()
    {
        var invalidFolder = Path.Combine(_directory, "must-not-exist");
        var item = await MediaWorker.Run(() =>
        {
            using var image = DecodedImage.Allocate(4, 4);
            image.Span.Fill(255);
            return CaptureStore.Save(image, invalidFolder, autoSave: false);
        });
        try
        {
            Assert.False(Directory.Exists(invalidFolder));
            Assert.True(File.Exists(item.FilePath));
            Assert.Equal(4, item.Width);
        }
        finally { File.Delete(item.FilePath); }
    }

    [Fact]
    public async Task CaptureWritesDistinctFilesAndNoPartials()
    {
        await MediaWorker.Run(() =>
        {
            using var image = DecodedImage.Allocate(4, 4);
            image.Span.Fill(255);
            var first = CaptureStore.Save(image, _directory, autoSave: true);
            var second = CaptureStore.Save(image, _directory, autoSave: true);
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
