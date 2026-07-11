using Microsoft.UI.Xaml;
using NexusShot.App.Services;
using NexusShot.App.Storage;
using NexusShot.App.Tray;
using NexusShot.App.ViewModels;
using NexusShot.App.Views;

namespace NexusShot.App;

public partial class App : Application
{
    private MainWindow? _mainWindow;
    private TrayIconService? _tray;

    public static AppServices Services { get; } = new();

    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var logger = new FileLogger();
        try
        {
            var storage = new JsonStorageService(logger);
            var settings = await storage.LoadSettingsAsync(CancellationToken.None);
            Services.Configure(storage, settings, logger);
            logger.Info("app.launched");

            // Never activated at launch: Activate() would flash the window for a frame before
            // the hide. The window first appears when the tray, a hotkey, or Settings asks for it.
            _mainWindow = new MainWindow(new MainViewModel(Services), Services);

            _tray = new TrayIconService(_mainWindow, Services);
            _tray.RegionCaptureRequested += (_, _) => _mainWindow.BeginRegionCapture();
            _tray.SettingsRequested += (_, _) => _mainWindow.ShowSettings();
            _tray.QuitRequested += (_, _) =>
            {
                Services.Previews.CloseAll();
                _tray?.Dispose();
                _mainWindow?.PrepareForShutdown();
                _mainWindow?.Close();
                Exit();
            };

            _tray.Show();
            _mainWindow.AttachTray(_tray);
        }
        catch (Exception exception)
        {
            logger.Error("app.launch_failed", exception);
            Exit();
        }
    }
}
