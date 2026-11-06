using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Views;

namespace NexusShot;

/// <summary>
/// Screen recording: the capture bar in Video picks the area, a countdown gives the user time to get
/// ready, then the area streams to an encoder until stopped. The outline and the recording bar are
/// excluded from capture, so they never appear in the file.
/// </summary>
public sealed partial class App
{
    private readonly RecordingState _recording = new();
    private CountdownBadge? _recordingCountdown;
    private CancellationTokenSource? _recordingStop;
    private Task<PendingRecording>? _recordingWork;
    private RecordingBar? _recordingBar;
    private RecordingFrame? _recordingFrame;
    private bool _restoreLibraryAfterRecording;

    /// <summary>The Record shortcut and the Library button: start one, or end the one under way.</summary>
    private void ToggleRecording()
    {
        if (_recording.IsCapturing) StopRecording();
        else if (_recording.Phase == RecordingPhase.Countdown) EndRecording();
        else if (!_recording.IsBusy) Capture(CaptureMode.Region, PickerMode.Region, CaptureKind.Video);
    }

    /// <summary>The tray's entries choose the format up front; the capture bar can still switch it.</summary>
    private void Record(RecordingFormat format)
    {
        RememberFormat(format);
        Capture(CaptureMode.Region, PickerMode.Region, CaptureKind.Video);
    }

    private void RememberFormat(RecordingFormat format)
    {
        if (_settings.RecordingFormat == format) return;
        _settings.RecordingFormat = format;
        _storage.SaveSettings(_settings);
    }

    /// <summary>Counts down with the Library out of the way, then starts. A policy that disables
    /// capture is reported here, before anything moves.</summary>
    private void BeginRecording(PickedRecording picked)
    {
        try
        {
            if (!GraphicsCapture.IsSupported)
            {
                UserFeedback.Error(_main.Handle, "Screen recording is turned off on this PC by your organization.");
                return;
            }
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            Log.Error("recording.unsupported", exception);
            UserFeedback.Error(_main.Handle, "Screen recording is not available on this PC.");
            return;
        }
        if (!_recording.TryBegin()) return;

        RememberFormat(picked.Format);
        _restoreLibraryAfterRecording = WindowInterop.IsWindowVisible(_main.Handle);
        _main.Conceal();
        _recordingCountdown = new CountdownBadge(RecordingState.CountdownSeconds);
        _recordingCountdown.Dismissed += () => _recordingCountdown = null;
        // Posted: Elapsed fires inside WM_DESTROY.
        _recordingCountdown.Elapsed += () => _main.Post(() => StartRecording(picked.Region));
        _recordingCountdown.Start();
        _main.Invalidate();
    }

    private void StartRecording(RectInt area)
    {
        if (_disposed || _recording.Phase != RecordingPhase.Countdown) return;
        try
        {
            var plan = new RecordingPlan(_settings, area.Width, area.Height);
            _recording.Start();
            _recordingStop = new CancellationTokenSource();
            var stop = _recordingStop.Token;
            var folder = _settings.RecordingFolder;
            var cursor = _settings.IncludeCursor;
            var clock = _recording.Clock;
            _recordingFrame = new RecordingFrame(area, Theme);
            _recordingBar = new RecordingBar(_recording, plan.Limit, Theme);
            _recordingBar.PauseRequested += () => _main.Post(TogglePause);
            _recordingBar.StopRequested += discard => _main.Post(() => { if (discard) DiscardRecording(); else StopRecording(); });
            _recordingBar.Open(area);
            _recordingWork = Task.Run(() => ScreenRecording.Record(area, plan, folder, cursor, clock, stop));
            _ = FinishRecording(_recordingWork);
            _main.Invalidate();
        }
        catch (Exception exception)
        {
            EndRecording();
            Log.Error("recording.start", exception);
            UserFeedback.Error(_main.Handle, "Could not start recording. Check the recordings folder and try a smaller area.");
        }
    }

    private void TogglePause()
    {
        if (!_recording.TogglePause()) return;
        _recordingBar?.Invalidate();
        _main.Invalidate();
    }

    private void StopRecording() => Stop(discard: false);

    /// <summary>Drops the recording under way, or the countdown before it.</summary>
    private void DiscardRecording()
    {
        if (_recording.Phase == RecordingPhase.Countdown) EndRecording();
        else Stop(discard: true);
    }

    private void Stop(bool discard)
    {
        if (!_recording.Stop(discard)) return;
        _recordingStop?.Cancel();
        _recordingBar?.Invalidate();
        _main.Invalidate();
    }

    /// <summary>Also runs when the encoder ends by itself, at a GIF's length limit.</summary>
    private async Task FinishRecording(Task<PendingRecording> work)
    {
        PendingRecording? pending = null;
        Exception? failure = null;
        try { pending = await work.ConfigureAwait(false); }
        catch (Exception exception) { failure = exception; }
        if (_disposed) { pending?.Dispose(); return; }
        _main.Post(() =>
        {
            using (pending)
            {
                if (_disposed) return;
                try
                {
                    if (failure is not null) throw new InvalidOperationException("Recording failed.", failure);
                    if (!_recording.Discard)
                    {
                        var item = pending!.Commit();
                        _main.AddCapture(item);
                        UserFeedback.Info(_main.Handle, $"Saved {item.FileName}");
                    }
                }
                catch (Exception exception)
                {
                    Log.Error("recording.save", exception);
                    UserFeedback.Error(_main.Handle, "Could not save the recording. Check available disk space and the recordings folder, then retry.");
                }
                finally { EndRecording(); }
            }
        });
    }

    private void EndRecording()
    {
        _recording.Finish();
        _recordingCountdown?.Cancel();
        _recordingBar?.Dispose();
        _recordingBar = null;
        _recordingFrame?.Dispose();
        _recordingFrame = null;
        _recordingStop?.Dispose();
        _recordingStop = null;
        _recordingWork = null;
        if (_restoreLibraryAfterRecording) _main.Reveal();
        _restoreLibraryAfterRecording = false;
        _main.Invalidate();
    }

    private void DisposeRecording()
    {
        _recordingCountdown?.Cancel();
        _recordingStop?.Cancel();
        try { _recordingWork?.GetAwaiter().GetResult().Dispose(); }
        catch (Exception exception) { Log.Error("recording.shutdown", exception); }
        _recordingStop?.Dispose();
        _recordingBar?.Dispose();
        _recordingFrame?.Dispose();
    }
}
