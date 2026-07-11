using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NexusShot.App.Capture;
using NexusShot.App.Enums;
using NexusShot.App.Native;
using NexusShot.App.Services;
using NexusShot.App.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NexusShot.App.Views;

/// <summary>
/// The dashboard: a sidebar that browses captures and a detail pane that previews the selected one.
/// Annotating opens <see cref="EditorWindow"/> rather than docking it here, so the editor never
/// surrenders image width to a list the user has stopped looking at.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly IntPtr _handle;
    private readonly AppWindow _appWindow;
    private Tray.TrayIconService? _tray;
    private bool _isCaptureInProgress;
    private bool _isShuttingDown;

    /// <summary>True while the settings pane is being populated, so change handlers stay quiet.</summary>
    private bool _isLoadingSettings;

    public MainViewModel ViewModel { get; }

    /// <summary>
    /// The version stamped onto the assembly at publish time (<c>-p:Version</c>), shown as a badge
    /// beside the sidebar brand — as the full major.minor.patch, e.g. "v1.0.0".
    /// </summary>
    public string AppVersionLabel { get; } = FormatVersion();

    private static string FormatVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? string.Empty : $"v{v.Major}.{v.Minor}.{v.Build}";
    }

    public MainWindow(MainViewModel viewModel, AppServices services)
    {
        InitializeComponent();
        ViewModel = viewModel;
        _services = services;

        _handle = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_handle));
        Helpers.WindowSizing.ResizeDips(_appWindow, _handle, 1040, 700);

        // The sidebar already carries the brand, so the titlebar shows neither icon nor name —
        // content extends into it, leaving only the caption buttons. The top strip stays
        // draggable via the framework's fallback drag region. The icon is still set on the
        // window itself: Alt-Tab and the taskbar read it from there, not from the titlebar.
        ExtendsContentIntoTitleBar = true;
        Helpers.AppIcon.Apply(_appWindow, _handle);

        // Register before applying the stored preference, so this window is themed in the same pass.
        _services.Theme.Register(this);
        _services.Theme.SetPreference(services.Settings.Theme);
        _services.Theme.ThemeChanged += (_, _) => UpdateThemeButton();
        UpdateThemeButton();

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        _services.ScreenshotUpdated += (_, item) => ViewModel.RefreshScreenshot(item);
        WireShortcutRecorders();

        // Controls mark these handled before they reach Root, so defocus opts in with handledEventsToo.
        Root.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(Root_PointerPressed), handledEventsToo: true);
        Root.AddHandler(UIElement.KeyDownEvent,
            new KeyEventHandler(Root_KeyDown), handledEventsToo: true);

        // The dashboard hosts the tray icon's message loop, so closing it would tear down the
        // tray and the global hotkeys. Hide it instead; Quit owns real shutdown.
        _appWindow.Closing += (_, args) =>
        {
            if (_isShuttingDown) return;
            args.Cancel = true;
            HideDashboard();
        };

        _ = InitializeAsync();
    }

    /// <summary>Allows the tray's Quit command to close this window for real.</summary>
    public void PrepareForShutdown()
    {
        _isShuttingDown = true;
        ViewModel.StopWatching();
    }

    /// <summary>Gives the settings pane a way to re-register shortcuts after the user edits them.
    /// The tray is created after this window, so it attaches itself once ready.</summary>
    public void AttachTray(Tray.TrayIconService tray) => _tray = tray;

    private async Task InitializeAsync()
    {
        try { await ViewModel.InitializeAsync(); }
        catch (Exception exception) { _services.Logger.Error("main.history_load_failed", exception); }
        SyncSelectionToList();
        UpdateDetailPane();

        // Keep the sidebar synchronized with what File Explorer does to the save folder.
        ViewModel.StartWatching(DispatcherQueue);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Selected):
                SyncSelectionToList();
                UpdateDetailPane();
                break;
            case nameof(MainViewModel.IsEmpty):
                UpdateDetailPane();
                break;
        }
    }

    /// <summary>
    /// Pushes the view model's selection into the list. Guarded against the echo: assigning
    /// SelectedItem raises SelectionChanged, which would write straight back into the view model.
    /// </summary>
    private void SyncSelectionToList()
    {
        if (ReferenceEquals(HistoryList.SelectedItem, ViewModel.Selected)) return;
        HistoryList.SelectedItem = ViewModel.Selected;
    }

    private void History_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.Selected = HistoryList.SelectedItem as ScreenshotTile;

    /// <summary>The tile whose async preview load the detail pane is currently listening to.</summary>
    private ScreenshotTile? _detailTile;

    private void UpdateDetailPane()
    {
        // The settings pane owns the detail column while open.
        if (SettingsPane.Visibility == Visibility.Visible) return;

        var tile = ViewModel.Selected;
        var hasSelection = tile is not null;

        EmptyState.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
        PreviewWell.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        DetailBar.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;

        // The preview decodes asynchronously; listen so the image appears the moment it lands
        // instead of waiting for the next reselection.
        if (!ReferenceEquals(_detailTile, tile))
        {
            if (_detailTile is not null)
            {
                _detailTile.PropertyChanged -= DetailTile_PropertyChanged;
                _detailTile.ReleasePreview();
            }
            _detailTile = tile;
            if (tile is not null) tile.PropertyChanged += DetailTile_PropertyChanged;
        }

        if (tile is null)
        {
            // Drop the bitmap reference so a large preview is not pinned while nothing is shown.
            PreviewImage.Source = null;
            return;
        }

        // The already-decoded sidebar thumbnail stands in until the full preview is ready.
        PreviewImage.Source = tile.Preview ?? tile.Thumbnail;
        TitleText.Text = tile.FileName;
        MetaText.Text = $"{tile.Dimensions}  ·  {tile.FileSize}  ·  {tile.CapturedAt}";
    }

    private void DetailTile_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _detailTile) || _detailTile is null) return;
        if (SettingsPane.Visibility == Visibility.Visible) return;
        if (e.PropertyName is not (nameof(ScreenshotTile.Preview) or nameof(ScreenshotTile.Thumbnail))) return;
        PreviewImage.Source = _detailTile.Preview ?? _detailTile.Thumbnail;
    }

    /// <summary>Reflects the theme actually in effect, and names the theme a click would switch to.</summary>
    private void UpdateThemeButton()
    {
        var isDark = _services.Theme.EffectiveTheme == ElementTheme.Dark;

        // Escapes rather than literal characters: these are Segoe Fluent Icons private-use
        // codepoints, which render as empty boxes in most editors and diff tools and turn into
        // replacement characters the moment anything re-saves this file in another encoding.
        const string Sun = "\uE706";
        const string Moon = "\uE708";

        // Show the theme a click switches to, not the one already in effect.
        ThemeIcon.Glyph = isDark ? Sun : Moon;
        ToolTipService.SetToolTip(ThemeButton, isDark ? "Switch to light theme" : "Switch to dark theme");
    }

    /// <summary>
    /// Cycles the explicit preference. Deliberately toggles Light/Dark rather than cycling through
    /// System: a user clicking a theme button wants a specific theme, and landing on System would
    /// look like the click did nothing whenever System already resolved to the current theme.
    /// </summary>
    private async void Theme_Click(object sender, RoutedEventArgs e)
    {
        var next = _services.Theme.EffectiveTheme == ElementTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        _services.Settings.Theme = next;
        _services.Theme.SetPreference(next);

        // Keep the settings pane's theme selector honest if it is open.
        if (SettingsPane.Visibility == Visibility.Visible)
        {
            _isLoadingSettings = true;
            ThemeBox.SelectedIndex = (int)next;
            _isLoadingSettings = false;
        }

        try { await _services.Storage.SaveSettingsAsync(_services.Settings, CancellationToken.None); }
        catch (Exception exception) { _services.Logger.Error("settings.theme_save_failed", exception); }
    }

    public void HideDashboard() => NativeMethods.ShowWindow(_handle, NativeMethods.SwHide);

    public void ShowDashboard()
    {
        NativeMethods.ShowWindow(_handle, NativeMethods.SwRestore);
        Activate();
        NativeMethods.SetForegroundWindow(_handle);
    }

    public void ShowSettings()
    {
        ShowDashboard();
        OpenSettingsPane();
    }

    public void BeginRegionCapture() => _ = BeginCaptureAsync(CaptureMode.Region);

    /// <summary>
    /// Captures without hiding the dashboard: the user may want to capture the NexusShot window
    /// itself. Region mode shows a transparent layered overlay over the live desktop and grabs
    /// the pixels only once the selection is committed.
    /// </summary>
    public async Task BeginCaptureAsync(CaptureMode mode)
    {
        if (_isCaptureInProgress) return;
        _isCaptureInProgress = true;

        try
        {
            if (mode == CaptureMode.Region)
            {
                await CaptureRegionAsync();
                return;
            }

            await ViewModel.CaptureAsync(mode);
        }
        catch (Exception exception)
        {
            _services.Logger.Error("capture.failed", exception, new { mode = mode.ToString() });
        }
        finally
        {
            _isCaptureInProgress = false;
        }
    }

    private async Task CaptureRegionAsync()
    {
        var region = await RegionSelectionOverlay.SelectAsync();
        if (region is not { } selected) return; // Cancelled.

        // The overlay window is gone by now, but give the compositor a frame to repaint the
        // desktop underneath before reading pixels back off the screen.
        await Task.Delay(90);
        await ViewModel.CaptureAsync(CaptureMode.Region, selected);
    }

    private void Region_Click(object sender, RoutedEventArgs e) => _ = BeginCaptureAsync(CaptureMode.Region);
    private void FullScreen_Click(object sender, RoutedEventArgs e) => _ = BeginCaptureAsync(CaptureMode.FullScreen);
    private void ActiveWindow_Click(object sender, RoutedEventArgs e) => _ = BeginCaptureAsync(CaptureMode.ActiveWindow);
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettingsPane();

    private void Annotate_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } tile) return;
        var editor = new EditorWindow(tile.Item, _services);
        _services.Theme.Register(editor);
        editor.ActivateToFront();
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } tile) return;
        try { await _services.Clipboard.CopyImageAsync(tile.Item.FilePath, CancellationToken.None); }
        catch (Exception exception) { _services.Logger.Error("main.copy_failed", exception); }
    }

    /// <summary>Opens Explorer with the capture already selected.</summary>
    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } tile) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{tile.Item.FilePath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            _services.Logger.Error("main.reveal_failed", exception);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } tile) return;
        try { await ViewModel.RemoveAsync(tile, CancellationToken.None); }
        catch (Exception exception) { _services.Logger.Error("main.delete_failed", exception); }
    }

    // ============================  SETTINGS PANE  ============================
    // Every control applies its change immediately and persists it; there is no save step.
    // _isLoadingSettings keeps the population pass from re-triggering the handlers.

    private void OpenSettingsPane()
    {
        LoadSettingsIntoPane();
        ReserveCaptionArea();

        EmptyState.Visibility = Visibility.Collapsed;
        PreviewWell.Visibility = Visibility.Collapsed;
        DetailBar.Visibility = Visibility.Collapsed;
        SettingsPane.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Pushes the settings close button left of the native minimize/maximize/close cluster, which
    /// floats over the pane's top-right corner because content extends into the titlebar. The
    /// inset comes from the titlebar itself (physical pixels), so it tracks DPI and OS metrics.
    /// </summary>
    private void ReserveCaptionArea()
    {
        var scale = NativeMethods.GetDpiForWindow(_handle) / 96.0;
        var insetDips = _appWindow.TitleBar.RightInset > 0
            ? _appWindow.TitleBar.RightInset / scale
            : 138; // Standard three-button caption width, if the inset is not yet available.

        // The header already pads 28 from the pane edge; add whatever more the caption needs,
        // plus breathing room so the two close affordances never read as one cluster.
        SettingsCloseButton.Margin = new Thickness(0, 0, Math.Max(0, insetDips + 12 - 28), 0);
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsPane.Visibility = Visibility.Collapsed;
        UpdateDetailPane();
    }

    private void LoadSettingsIntoPane()
    {
        var settings = _services.Settings;
        var defaults = new Models.AppSettings();
        _isLoadingSettings = true;

        FolderText.Text = settings.ScreenshotFolder;
        CaptureModeBox.SelectedIndex = (int)settings.DefaultCaptureMode;
        DismissBox.Value = settings.PreviewDismissSeconds;
        ClipboardToggle.IsOn = settings.CopyToClipboardAutomatically;
        SaveToggle.IsOn = settings.SaveAutomatically;
        StartupToggle.IsOn = settings.StartWithWindows;
        ThemeBox.SelectedIndex = (int)settings.Theme;

        RegionRecorder.Binding = settings.CaptureRegionHotkey;
        RegionRecorder.DefaultBinding = defaults.CaptureRegionHotkey;
        FullScreenRecorder.Binding = settings.CaptureFullScreenHotkey;
        FullScreenRecorder.DefaultBinding = defaults.CaptureFullScreenHotkey;
        ActiveWindowRecorder.Binding = settings.CaptureActiveWindowHotkey;
        ActiveWindowRecorder.DefaultBinding = defaults.CaptureActiveWindowHotkey;
        OpenWindowRecorder.Binding = settings.OpenMainWindowHotkey;
        OpenWindowRecorder.DefaultBinding = defaults.OpenMainWindowHotkey;

        ShortcutWarning.Visibility = Visibility.Collapsed;
        _isLoadingSettings = false;
    }

    private void WireShortcutRecorders()
    {
        RegionRecorder.GestureChanged += (_, binding) =>
            ApplyShortcut(RegionRecorder, binding, s => s.CaptureRegionHotkey, (s, b) => s.CaptureRegionHotkey = b);
        FullScreenRecorder.GestureChanged += (_, binding) =>
            ApplyShortcut(FullScreenRecorder, binding, s => s.CaptureFullScreenHotkey, (s, b) => s.CaptureFullScreenHotkey = b);
        ActiveWindowRecorder.GestureChanged += (_, binding) =>
            ApplyShortcut(ActiveWindowRecorder, binding, s => s.CaptureActiveWindowHotkey, (s, b) => s.CaptureActiveWindowHotkey = b);
        OpenWindowRecorder.GestureChanged += (_, binding) =>
            ApplyShortcut(OpenWindowRecorder, binding, s => s.OpenMainWindowHotkey, (s, b) => s.OpenMainWindowHotkey = b);
    }

    /// <summary>Mutates the settings and persists them. Fire-and-forget: the change is already
    /// applied in memory; persistence failure only costs durability and is logged.</summary>
    private async void ApplySetting(Action<Models.AppSettings> change)
    {
        if (_isLoadingSettings) return;
        change(_services.Settings);
        try { await _services.Storage.SaveSettingsAsync(_services.Settings, CancellationToken.None); }
        catch (Exception exception) { _services.Logger.Error("settings.save_failed", exception); }
    }

    private void CaptureMode_Changed(object sender, SelectionChangedEventArgs e) =>
        ApplySetting(s => s.DefaultCaptureMode = (CaptureMode)Math.Max(0, CaptureModeBox.SelectedIndex));

    private void Clipboard_Toggled(object sender, RoutedEventArgs e) =>
        ApplySetting(s => s.CopyToClipboardAutomatically = ClipboardToggle.IsOn);

    private void Save_Toggled(object sender, RoutedEventArgs e) =>
        ApplySetting(s => s.SaveAutomatically = SaveToggle.IsOn);

    private void Dismiss_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) =>
        ApplySetting(s => s.PreviewDismissSeconds = double.IsNaN(sender.Value) ? 0 : (int)sender.Value);

    /// <summary>Click-away defocus: a press on an interactive control is its to keep; anything else
    /// commits the current edit by moving focus to the invisible sink.</summary>
    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is NumberBox or ComboBox or ComboBoxItem or ToggleSwitch or Controls.HotkeyRecorder
                or TextBox or Slider)
                return;
        FocusSink.Focus(FocusState.Programmatic);
    }

    /// <summary>Snaps rubber-band overscroll back to the edge; the ScrollViewer has no overpan toggle.</summary>
    private void SettingsScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        var clamped = Math.Clamp(SettingsScroller.VerticalOffset, 0, SettingsScroller.ScrollableHeight);
        if (clamped != SettingsScroller.VerticalOffset)
            SettingsScroller.ChangeView(null, clamped, null, disableAnimation: true);
    }

    /// <summary>Escape drops focus from whatever field has it; recorders handle their own Escape first.</summary>
    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape) return;
        var focused = FocusManager.GetFocusedElement(Content.XamlRoot) as DependencyObject;
        for (var node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is Controls.HotkeyRecorder) return;
        FocusSink.Focus(FocusState.Programmatic);
        e.Handled = true;
    }

    private void Startup_Toggled(object sender, RoutedEventArgs e) => ApplySetting(s =>
    {
        s.StartWithWindows = StartupToggle.IsOn;
        try { new StartupService().SetEnabled(s.StartWithWindows); }
        catch (Exception exception) { _services.Logger.Error("settings.startup_failed", exception); }
    });

    private void ThemeSetting_Changed(object sender, SelectionChangedEventArgs e) => ApplySetting(s =>
    {
        s.Theme = (AppTheme)Math.Max(0, ThemeBox.SelectedIndex);
        _services.Theme.SetPreference(s.Theme);
    });

    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _handle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        ApplySetting(s =>
        {
            s.ScreenshotFolder = folder.Path;
            Directory.CreateDirectory(s.ScreenshotFolder);
        });
        FolderText.Text = folder.Path;
        ViewModel.RestartWatcher(); // The sync watcher follows the save folder.
    }

    /// <summary>
    /// Applies an edited shortcut: rejects a gesture already bound to another action, otherwise
    /// stores it and re-registers everything, surfacing keys the OS refuses (owned by another app).
    /// </summary>
    private void ApplyShortcut(
        Controls.HotkeyRecorder recorder,
        Models.HotkeyBinding binding,
        Func<Models.AppSettings, Models.HotkeyBinding> read,
        Action<Models.AppSettings, Models.HotkeyBinding> write)
    {
        if (_isLoadingSettings) return;
        var settings = _services.Settings;

        var allBindings = new[]
        {
            settings.CaptureRegionHotkey, settings.CaptureFullScreenHotkey,
            settings.CaptureActiveWindowHotkey, settings.OpenMainWindowHotkey,
        };
        if (allBindings.Any(other => !ReferenceEquals(other, read(settings)) && other.IsSameGesture(binding)))
        {
            recorder.Binding = read(settings); // Revert the display.
            ShowShortcutWarning($"{Controls.HotkeyRecorder.Format(binding)} is already used by another action.");
            return;
        }

        ApplySetting(s => write(s, binding));

        var failed = _tray?.ApplyHotkeys() ?? [];
        ShortcutWarning.Visibility = Visibility.Collapsed;
        if (failed.Count > 0)
            ShowShortcutWarning($"{Controls.HotkeyRecorder.Format(binding)} could not be registered — another application may already use it.");
    }

    private void ShowShortcutWarning(string message)
    {
        ShortcutWarning.Text = message;
        ShortcutWarning.Visibility = Visibility.Visible;
    }
}
