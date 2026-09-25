using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>
/// Updates, surfaced in two places that share one state: a button in the tools row and the Updates
/// row in Settings. The background check (startup, every six hours, after waking) only ever shows
/// the button; an update runs only from the user's click: download on the button, verify, then one
/// prompt before restarting. Nothing restarts, or opens, on its own.
/// </summary>
public sealed partial class MainWindow
{
    private enum UpdateStage { Idle, Checking, UpToDate, Available, Downloading, Ready, Failed }

    private UpdateStage _updateStage;
    private UpdateRelease? _update;
    private double _updateProgress;
    private string? _updateError;
    private CancellationTokenSource? _updateCancel;

    /// <summary>The verified installer, held from the end of the download until the restart.</summary>
    private string? _installer;

    /// <summary>The restart prompt, with how many editors had unsaved changes when it opened. Null
    /// while closed.</summary>
    private int? _restartPrompt;

    /// <summary>Raised from the restart prompt with the verified installer, and whether open editors
    /// save before closing or drop their changes.</summary>
    public event Action<string, bool>? InstallRequested;

    /// <summary>How many open editors have unsaved changes; supplied by the app, which owns them.</summary>
    public Func<int>? UnsavedEditors { get; set; }

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
        if (_updateStage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Ready) return;
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

            if (release is not null) OfferUpdate(release);
            else if (!quiet) SetUpdateStage(UpdateStage.UpToDate);
        });
    }

    /// <summary>A newer release was found: the tools row offers it.</summary>
    internal void OfferUpdate(UpdateRelease release)
    {
        _update = release;
        SetUpdateStage(UpdateStage.Available);
    }

    /// <summary>Fetches and verifies the installer, reporting progress off the UI thread. The real
    /// download unless a test stands in for the network.</summary>
    internal Func<UpdateRelease, Action<double>, CancellationToken, Task<string>> DownloadInstaller { get; set; } =
        Updater.DownloadAsync;

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
        try { installer = await DownloadInstaller(release, Report, cancel.Token); }
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
            _installer = installer;
            _updateProgress = 1;
            SetUpdateStage(UpdateStage.Ready);
            OpenRestartPrompt();
        });
    }

    public void UpdateFailed(string reason) => SetUpdateStage(UpdateStage.Failed, reason);

    private void SetUpdateStage(UpdateStage stage, string? error = null)
    {
        _updateStage = stage;
        _updateError = error;
        Invalidate();
    }

    private void OpenRestartPrompt()
    {
        CloseSettings();
        _restartPrompt = UnsavedEditors?.Invoke() ?? 0;
        Invalidate();
    }

    private bool RestartPromptOpen => _restartPrompt is not null;

    /// <summary>The prompt's choice. The stage stays Ready: if a save fails and the restart stops,
    /// the button is still there to try again.</summary>
    private void Restart(bool saveEditors)
    {
        _restartPrompt = null;
        if (_installer is { } installer) InstallRequested?.Invoke(installer, saveEditors);
    }

    // ============================  TOOLS-ROW BUTTON  ============================

    private double UpdateButtonWidth => S(188);

    /// <summary>True while the tools row carries the button: from a found update until it is installed.</summary>
    private bool ShowsUpdateButton =>
        _update is not null && _updateStage is UpdateStage.Available or UpdateStage.Downloading
            or UpdateStage.Ready or UpdateStage.Failed;

    /// <summary>One fixed width across every stage, so its text never shifts. The download's progress
    /// is drawn here and nowhere else in the Library.</summary>
    private void DrawUpdateButton(Ui ui, Rect bounds)
    {
        var theme = ui.Theme;
        var id = Ui.Id("library.update");
        var clicked = ui.Interact(id, bounds);
        var hot = ui.IsHot(id) && _updateStage != UpdateStage.Downloading;
        var radius = (float)S(Metrics.RadiusSm);
        var version = _update!.Version.ToString(3);

        // The accent's wash is fainter on dark, as the theme's own soft accent is.
        byte Tint(bool hover) => theme.IsDark ? (byte)(hover ? 46 : 26) : (byte)(hover ? 66 : 41);

        var (fill, border, text, icon, label) = _updateStage switch
        {
            UpdateStage.Ready => (hot ? theme.Accent.Mix(Rgba.Black, 0.14) : theme.Accent, default,
                theme.TextOnAccent, Icons.Restart, "Restart to update"),
            UpdateStage.Failed => (theme.Danger.WithAlpha(Tint(hot)), theme.Danger.WithAlpha(115),
                theme.Danger, Icons.Warning, "Update failed"),
            UpdateStage.Downloading => (theme.Accent.WithAlpha(Tint(false)), theme.Accent.WithAlpha(128),
                theme.TextPrimary, Icons.Download, "Downloading"),
            _ => (theme.Accent.WithAlpha(Tint(hot)), theme.Accent.WithAlpha(128),
                theme.AccentText, Icons.Download, "Update available"),
        };

        ui.FillRounded(bounds, radius, fill);
        if (_updateStage == UpdateStage.Downloading && _updateProgress > 0)
        {
            ui.PushClip(bounds);
            var done = bounds with { Width = Math.Round(bounds.Width * _updateProgress) };
            ui.FillRounded(done, radius, theme.Accent.WithAlpha(72));
            ui.FillRect(new Rect(done.Right - 1, bounds.Y, 1, bounds.Height), theme.Accent.WithAlpha(204));
            ui.PopClip();
        }
        if (border.A > 0) ui.StrokeRounded(bounds, radius, border);

        var x = bounds.X + S(9);
        ui.Icon(icon, new Rect(x, bounds.Y, S(15), bounds.Height),
            _updateStage == UpdateStage.Downloading ? theme.AccentText : text, S(15));
        x += S(15) + S(8);
        var font = S(Metrics.FontSm);
        ui.Text(label, new Rect(x, bounds.Y, bounds.Right - x, bounds.Height), text, font, Weight.Semibold);

        if (_updateStage == UpdateStage.Downloading)
        {
            var percent = $"{(int)(_updateProgress * 100)}%";
            var width = ui.MeasureText(percent, font, Weight.Semibold, Face.Mono);
            ui.Text(percent, new Rect(bounds.Right - S(10) - width, bounds.Y, width + 1, bounds.Height),
                theme.TextSecondary, font, Weight.Semibold, face: Face.Mono);
        }
        else
        {
            var tag = _updateStage == UpdateStage.Failed ? "Retry" : version;
            var tagFont = S(11);
            var tagWidth = ui.MeasureText(tag, tagFont, Weight.Semibold) + S(12);
            var chip = new Rect(bounds.Right - S(6) - tagWidth, bounds.Center.Y - S(9), tagWidth, S(18));
            var (chipFill, chipText) = _updateStage switch
            {
                UpdateStage.Ready => (theme.TextOnAccent, theme.Accent),
                UpdateStage.Failed => (theme.Danger, theme.SurfaceWindow),
                _ => (theme.Accent, theme.TextOnAccent),
            };
            ui.FillRounded(chip, (float)S(Metrics.RadiusXs), chipFill);
            ui.Text(tag, chip, chipText, tagFont, Weight.Semibold, TextAlign.Center);
        }

        if (_updateStage == UpdateStage.Failed && _updateError is { } error) ui.Tip(id, bounds, $"Could not update: {error}");
        if (!clicked) return;

        // Posted: counting unsaved editors commits their open text, which must not happen mid-frame.
        if (_updateStage is UpdateStage.Available or UpdateStage.Failed) StartUpdate();
        else if (_updateStage == UpdateStage.Ready) Post(OpenRestartPrompt);
    }

    /// <summary>
    /// The one question before restarting, asked only once the installer is verified. With unsaved
    /// editors it offers to save them all or drop them; with none it is a plain confirm. Cancel keeps
    /// the installer, and the tools-row button brings the prompt back.
    /// </summary>
    private void DrawRestartPrompt(Ui ui, double width, double height)
    {
        var open = ui.Animate(Ui.Id("restart.open"), RestartPromptOpen ? 1 : 0, Metrics.MotionFast);
        if (open <= 0.01) return;

        var theme = ui.Theme;
        var unsaved = _restartPrompt ?? 0;
        ui.FillRect(new Rect(0, 0, width, height), theme.Scrim.WithAlpha((byte)(theme.Scrim.A * open)));

        var pad = S(24);
        var card = new Rect((width - S(480)) / 2, (height - S(156)) / 2 + S(10) * (1 - open), S(480), S(156));

        // A press on the scrim cancels, and must not also land on the library.
        if (RestartPromptOpen && ui.PointerPressed && !card.Contains(ui.Pointer) && ui.Pointer.Y > CaptionHeight)
            _restartPrompt = null;
        ui.Inert = !RestartPromptOpen;

        var radius = (float)S(Metrics.RadiusLg);
        ui.Shadow(card, radius, S(60), S(24), theme.Shadow);
        ui.FillRounded(card, radius, theme.SurfaceWindow);
        ui.StrokeRounded(card, radius, theme.StrokeDefault);

        var version = _update?.Version.ToString(3) ?? "";
        ui.Text($"Restart to install NexusShot {version}?", new Rect(card.X + pad, card.Y + pad - S(2), card.Width - pad * 2, S(24)),
            theme.TextPrimary, S(Metrics.FontLg), Weight.Semibold, face: Face.Display);
        var detail = unsaved switch
        {
            0 => "NexusShot closes, installs the update and opens again.",
            1 => "An open editor has unsaved changes.",
            _ => $"{unsaved} open editors have unsaved changes.",
        };
        ui.Text(detail, new Rect(card.X + pad, card.Y + pad + S(26), card.Width - pad * 2, S(20)),
            theme.TextSecondary, S(Metrics.FontMd));

        var y = card.Bottom - pad - S(32);
        var right = card.Right - pad;
        Rect Take(double buttonWidth)
        {
            right -= buttonWidth;
            var bounds = new Rect(right, y, buttonWidth, S(32));
            right -= S(8);
            return bounds;
        }

        var primary = unsaved > 0 ? "Save all and restart" : "Restart";
        var primaryIcon = unsaved > 0 ? Icons.Save : Icons.Restart;
        if (TintedButton(ui, Ui.Id("restart.save"), Take(ui.ButtonWidth(primary, primaryIcon)), primary, primaryIcon))
            Restart(saveEditors: true);

        if (unsaved > 0 && ui.Button(Ui.Id("restart.discard"), Take(ui.ButtonWidth("Discard and restart", Icons.Delete)),
            "Discard and restart", ButtonStyle.Outline, Icons.Delete))
            Restart(saveEditors: false);

        if (ui.Button(Ui.Id("restart.cancel"), Take(ui.ButtonWidth("Cancel", keycap: "Esc")), "Cancel", ButtonStyle.Outline,
            keycap: "Esc"))
            _restartPrompt = null;

        ui.Inert = false;
    }

    /// <summary>The update's own look for a button: the accent as a wash with a border, deepening on
    /// hover. A solid accent has no visible hover on the lighter presets.</summary>
    private bool TintedButton(Ui ui, int id, Rect bounds, string label, Icon icon)
    {
        var theme = ui.Theme;
        var clicked = ui.Interact(id, bounds);
        var hot = ui.IsHot(id) || ui.IsActive(id);
        var radius = (float)S(Metrics.RadiusSm);
        ui.FillRounded(bounds, radius, theme.Accent.WithAlpha(theme.IsDark ? (byte)(hot ? 46 : 26) : (byte)(hot ? 66 : 41)));
        ui.StrokeRounded(bounds, radius, theme.Accent.WithAlpha(128));

        var font = S(Metrics.FontMd);
        var textWidth = ui.MeasureText(label, font, Weight.Semibold);
        var x = bounds.Center.X - (S(15) + S(7) + textWidth) / 2;
        ui.Icon(icon, new Rect(x, bounds.Y, S(15), bounds.Height), theme.AccentText, S(15));
        ui.Text(label, new Rect(x + S(22), bounds.Y, textWidth + 1, bounds.Height), theme.AccentText, font, Weight.Semibold);
        return clicked;
    }

    // ============================  SETTINGS ROW  ============================

    private string UpdateCaption() => _updateStage switch
    {
        UpdateStage.Checking => "Checking for a newer version…",
        UpdateStage.UpToDate => $"NexusShot {AppVersion} is up to date",
        UpdateStage.Available => $"NexusShot {_update!.Version.ToString(3)} is available · you have {AppVersion}",
        UpdateStage.Downloading => _update!.InstallerSize > 0
            ? $"Downloading {_update.Version.ToString(3)} · {(int)(_updateProgress * 100)}% of {_update.InstallerSize / 1048576.0:0.0} MB"
            : $"Downloading {_update.Version.ToString(3)} · {(int)(_updateProgress * 100)}%",
        UpdateStage.Ready => $"NexusShot {_update!.Version.ToString(3)} is ready · restart to install",
        UpdateStage.Failed => $"Could not update: {_updateError}",
        _ => $"NexusShot {AppVersion}",
    };

    private double UpdateControlWidth => S(200);

    private void UpdateControl(Ui ui, Rect slot)
    {
        if (_updateStage == UpdateStage.Downloading)
        {
            var cancel = new Rect(slot.Right - S(28), slot.Center.Y - S(14), S(28), S(28));
            var bar = slot with { Width = slot.Width - S(36) };
            ui.Progress(new Rect(bar.X, slot.Center.Y - S(4), bar.Width, S(8)), _updateProgress);
            if (ui.IconButton(Ui.Id("settings.update.cancel"), cancel, Icons.Close, "Cancel the download", iconSize: 13,
                destructive: true))
                _updateCancel?.Cancel();
            return;
        }

        var (label, icon) = _updateStage switch
        {
            UpdateStage.Available => ($"Update to {_update!.Version.ToString(3)}", Icons.Download),
            UpdateStage.Ready => ("Restart to update", Icons.Restart),
            _ => ("Check for updates", Icons.Refresh),
        };
        var width = ui.ButtonWidth(label, icon, small: true);
        var bounds = new Rect(slot.Right - width, slot.Center.Y - S(15), width, S(30));
        var action = _updateStage is UpdateStage.Available or UpdateStage.Ready;
        if (ui.Button(Ui.Id("settings.update"), bounds, label, action ? ButtonStyle.Primary : ButtonStyle.Outline,
            icon, small: true, enabled: _updateStage != UpdateStage.Checking))
        {
            if (_updateStage == UpdateStage.Available) StartUpdate();
            else if (_updateStage == UpdateStage.Ready) Post(OpenRestartPrompt);
            else CheckForUpdates(quiet: false);
        }
    }
}
