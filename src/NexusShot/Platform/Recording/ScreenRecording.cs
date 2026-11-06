using NexusShot.Core;
using NexusShot.Render;

namespace NexusShot.Platform;

/// <summary>A recording stays private until the UI accepts it. Disposal also covers cancellation.</summary>
internal sealed class PendingRecording(string path, string folder, RecordingPlan plan) : IDisposable
{
    public ScreenshotHistoryItem Commit() => CaptureStore.Commit(path, folder, plan.Width, plan.Height, plan.Extension);

    public void Dispose()
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { Log.Error("recording.temp_cleanup", exception, path); }
    }
}

internal static class ScreenRecording
{
    /// <summary>One capture/encode loop, with at most two frames alive. Timestamps come from
    /// <paramref name="clock"/>, so a slow encoder drops frames rather than speeding up playback, and
    /// paused time, when nothing is captured, never reaches the file.</summary>
    public static PendingRecording Record(RectInt area, RecordingPlan plan, string folder, bool cursor,
        RecordingClock clock, CancellationToken stop)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $".nexusshot-{Guid.NewGuid():N}.tmp");
        var pending = new PendingRecording(path, folder, plan);
        DecodedImage? previous = null;
        try
        {
            using var stream = new GraphicsCapture.AreaStream(area, cursor);
            DecodedImage CaptureFrame() => Fit(stream.Next(), plan);
            using IRecordingEncoder encoder = plan.Format == RecordingFormat.Gif ? new GifEncoder(path) : new Mp4Encoder(path, plan);
            previous = CaptureFrame();
            long timestamp = 0;
            var interval = TimeSpan.TicksPerSecond / plan.FramesPerSecond;
            var limit = plan.Limit?.Ticks ?? long.MaxValue;
            while (!stop.IsCancellationRequested)
            {
                var wait = clock.IsRunning ? Math.Max(0, timestamp + interval - clock.Elapsed.Ticks) : interval;
                if (stop.WaitHandle.WaitOne(TimeSpan.FromTicks(wait))) break;
                if (!clock.IsRunning) continue;
                if (clock.Elapsed.Ticks >= limit) break;
                var next = CaptureFrame();
                var nextTimestamp = clock.Elapsed.Ticks;
                try { encoder.Write(previous, timestamp, Math.Max(1, nextTimestamp - timestamp)); }
                catch { next.Dispose(); throw; }
                previous.Dispose();
                previous = next;
                timestamp = nextTimestamp;
            }
            encoder.Write(previous, timestamp, Math.Max(interval, Math.Min(clock.Elapsed.Ticks, limit) - timestamp));
            encoder.Complete();
            return pending;
        }
        catch { pending.Dispose(); throw; }
        finally { previous?.Dispose(); }
    }

    private static DecodedImage Fit(DecodedImage source, RecordingPlan plan)
    {
        if (source.Width == plan.Width && source.Height == plan.Height)
            return source;
        using (source)
        {
            using var bitmap = WicImagingFactory.CreateBitmapFromMemory((uint)source.Width, (uint)source.Height,
                Constants.GUID_WICPixelFormat32bppPBGRA, (uint)source.Stride, (uint)source.ByteLength, source.Pointer);
            using var scaler = WicImagingFactory.CreateBitmapScaler();
            scaler.Object.Initialize(bitmap.Object, (uint)plan.Width, (uint)plan.Height,
                WICBitmapInterpolationMode.WICBitmapInterpolationModeFant).ThrowOnError();
            var pixels = DecodedImage.Allocate(plan.Width, plan.Height);
            try
            {
                scaler.Object.CopyPixels(IntPtr.Zero, (uint)pixels.Stride, (uint)pixels.ByteLength, pixels.Pointer).ThrowOnError();
                return pixels;
            }
            catch { pixels.Dispose(); throw; }
        }
    }
}
