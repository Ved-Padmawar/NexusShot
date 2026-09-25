using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>
/// The Settings row for updates. The background check (startup, every six hours, after waking) only
/// ever notifies; an update runs only from the user's click, and then to the end: download, verify, restart.
/// </summary>
public sealed partial class MainWindow
{
    private enum UpdateStage { Idle, Checking, UpToDate, Available, Downloading, Installing, Failed }

    private UpdateStage _updateStage;
    private UpdateRelease? _update;
    private double _updateProgress;
    private string? _updateError;
    private CancellationTokenSource? _updateCancel;

    /// <summary>Announced once per version.</summary>
    private Version? _announced;

    /// <summary>Raised with a verified installer.</summary>
    public event Action<string>? UpdateReady;

    private const nuint UpdateTimerId = 0x0DA7;
    private const nuint WakeCheckTimerId = 0x0DA8;
    private const uint IntervalMs = 6 * 60 * 60 * 1000;
    private const uint AfterWakeMs = 60 * 1000;

    public void ScheduleUpdateChecks()
    {
        WindowInterop.SetTimer(Handle, UpdateTimerId, IntervalMs, IntPtr.Zero);
        if (_settings.CheckForUpdates) CheckForUpdates(quiet: true);
    }

    /// <summary>A sleeping PC misses its ticks; check a minute after waking, once the network is back.</summary>
    private void CheckAfterWake() => WindowInterop.SetTimer(Handle, WakeCheckTimerId, AfterWakeMs, IntPtr.Zero);

    private bool OnUpdateTimer(nuint id)
    {
        if (id == WakeCheckTimerId) WindowInterop.KillTimer(Handle, WakeCheckTimerId);
        else if (id != UpdateTimerId) return false;
        if (_settings.CheckForUpdates) CheckForUpdates(quiet: true);
        return true;
    }

    private void CheckForUpdates(bool quiet) => _ = CheckForUpdatesAsync(quiet);

    private async Task CheckForUpdatesAsync(bool quiet)
    {
        if (_updateStage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Installing) return;
        if (!quiet) SetUpdateStage(UpdateStage.Checking);

        UpdateRelease? release = null;
        Exception? failure = null;
        // Anything at all: a failure here must end in a message, never a row stuck on "Checking".
        try { release = await Updater.CheckAsync(); }
        catch (Exception exception) { failure = exception; }

        Post(() =>
        {
            if (failure is not null)
            {
                Log.Error("update.check", failure);
                if (!quiet) SetUpdateStage(UpdateStage.Failed, "could not reach GitHub");
                return;
            }

            _update = release;
            if (release is not null)
            {
                SetUpdateStage(UpdateStage.Available);
                if (quiet && release.Version != _announced)
                {
                    _announced = release.Version;
                    UserFeedback.Info(Handle, $"NexusShot {release.Version.ToString(3)} is available. Open Settings to update.");
                }
            }
            else if (!quiet) SetUpdateStage(UpdateStage.UpToDate);
        });
    }

    private void StartUpdate() => _ = StartUpdateAsync();

    private async Task StartUpdateAsync()
    {
        if (_update is not { } release || _updateStage == UpdateStage.Downloading) return;
        _updateProgress = 0;
        SetUpdateStage(UpdateStage.Downloading);

        // Whole-percent steps only: the download reports far more often than a frame.
        var shown = -1;
        void Report(double fraction)
        {
            var percent = (int)(fraction * 100);
            if (percent == Interlocked.Exchange(ref shown, percent)) return;
            Post(() =>
            {
                _updateProgress = fraction;
                Invalidate();
            });
        }

        using var cancel = _updateCancel = new CancellationTokenSource();
        string? installer = null;
        Exception? failure = null;
        try { installer = await Updater.DownloadAsync(release, Report, cancel.Token); }
        catch (Exception exception) { failure = exception; }
        var cancelled = cancel.IsCancellationRequested;
        _updateCancel = null;

        Post(() =>
        {
            if (cancelled)
            {
                SetUpdateStage(UpdateStage.Available);
                return;
            }
            if (installer is null)
            {
                Log.Error("update.download", failure!);
                SetUpdateStage(UpdateStage.Failed, failure is InvalidDataException ? failure.Message : "the download failed");
                return;
            }
            _updateProgress = 1;
            SetUpdateStage(UpdateStage.Installing);
            UpdateReady?.Invoke(installer);
        });
    }

    public void UpdateFailed(string reason) => SetUpdateStage(UpdateStage.Failed, reason);

    private void SetUpdateStage(UpdateStage stage, string? error = null)
    {
        _updateStage = stage;
        _updateError = error;
        Invalidate();
    }

    private string UpdateCaption() => _updateStage switch
    {
        UpdateStage.Checking => "Checking for a newer version…",
        UpdateStage.UpToDate => $"NexusShot {AppVersion} is up to date",
        UpdateStage.Available => $"NexusShot {_update!.Version.ToString(3)} is available · you have {AppVersion}",
        UpdateStage.Downloading => _update!.InstallerSize > 0
            ? $"Downloading {_update.Version.ToString(3)} · {(int)(_updateProgress * 100)}% of {_update.InstallerSize / 1048576.0:0.0} MB"
            : $"Downloading {_update.Version.ToString(3)} · {(int)(_updateProgress * 100)}%",
        UpdateStage.Installing => "Installing - NexusShot will restart",
        UpdateStage.Failed => $"Could not update: {_updateError}",
        _ => $"NexusShot {AppVersion}",
    };

    private double UpdateControlWidth => S(200);

    private void UpdateControl(Ui ui, Rect slot)
    {
        if (_updateStage is UpdateStage.Downloading or UpdateStage.Installing)
        {
            // Only a download can be cancelled: a running installer stopped midway breaks the install.
            var cancel = new Rect(slot.Right - S(28), slot.Center.Y - S(14), S(28), S(28));
            var bar = _updateStage == UpdateStage.Downloading ? slot with { Width = slot.Width - S(36) } : slot;
            ui.Progress(new Rect(bar.X, slot.Center.Y - S(4), bar.Width, S(8)), _updateProgress);
            if (_updateStage == UpdateStage.Downloading
                && ui.IconButton(Ui.Id("settings.update.cancel"), cancel, Icons.Close, "Cancel the download", iconSize: 13,
                    destructive: true))
                _updateCancel?.Cancel();
            return;
        }

        var available = _updateStage == UpdateStage.Available;
        var label = available ? $"Update to {_update!.Version.ToString(3)}" : "Check for updates";
        var width = ui.ButtonWidth(label, small: true);
        var bounds = new Rect(slot.Right - width, slot.Center.Y - S(15), width, S(30));
        if (ui.Button(Ui.Id("settings.update"), bounds, label, available ? ButtonStyle.Primary : ButtonStyle.Outline,
            small: true, enabled: _updateStage != UpdateStage.Checking))
        {
            if (available) StartUpdate();
            else CheckForUpdates(quiet: false);
        }
    }
}
