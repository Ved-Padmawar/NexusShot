using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using NexusShot.App.Helpers;
using NexusShot.App.Models;
using NexusShot.App.Native;
using NexusShot.App.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinRT.Interop;

namespace NexusShot.App.Views;

/// <summary>
/// The Quick Access Overlay: a borderless, non-activating thumbnail card anchored to the
/// bottom-left of the work area. Hovering reveals copy/save/annotate/pin; the card can be
/// dragged into other applications.
/// </summary>
public sealed partial class FloatingPreviewWindow : Window
{
    /// <summary>Card size in device-independent pixels; scaled to physical pixels per monitor DPI.
    /// The height follows the capture's aspect ratio so the window hugs the image exactly and no
    /// backdrop edge ever shows around the thumbnail.</summary>
    private const int CardWidthDips = 168;
    private const int MinCardHeightDips = 56;
    private const int MaxCardHeightDips = 240;
    private const int StackGapDips = 10;
    private const int EdgeMarginDips = 18;

    private int _cardHeightDips;

    private ScreenshotHistoryItem _item;
    private readonly AppServices _services;
    private readonly DispatcherTimer _dismissTimer = new();
    private readonly IntPtr _handle;
    private readonly AppWindow _appWindow;

    public bool IsClosed { get; private set; }
    public bool IsPinned { get; private set; }
    public Guid ItemId => _item.Id;

    /// <summary>True while the dismissal fade runs; repeat dismissals and reflows are ignored.</summary>
    private bool _isDismissing;

    /// <summary>Raised when the user pins or unpins, so the stack can re-flow.</summary>
    public event EventHandler? PinnedChanged;

    public FloatingPreviewWindow(ScreenshotHistoryItem item, AppServices services)
    {
        InitializeComponent();
        _item = item;
        _services = services;

        // The card keeps the capture's aspect ratio (clamped so extreme shapes stay usable);
        // UniformToFill then covers the window without letterboxing.
        var aspect = item.Width > 0 && item.Height > 0 ? (double)item.Height / item.Width : 110.0 / 168.0;
        _cardHeightDips = Math.Clamp((int)Math.Round(CardWidthDips * aspect), MinCardHeightDips, MaxCardHeightDips);

        _handle = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_handle));

        ConfigureChrome();
        _ = LoadThumbnailAsync();
        WireInteractions();
        StartAutoDismiss();
    }

    /// <summary>Scale factor of the monitor hosting this window. 1.0 at 100% scaling.</summary>
    private double Scale => NativeMethods.GetDpiForWindow(_handle) / 96.0;

    private int CardWidth => (int)Math.Round(CardWidthDips * Scale);
    private int CardHeight => (int)Math.Round(_cardHeightDips * Scale);

    /// <summary>Physical height plus stack gap, so the preview service can flow variable-height cards.</summary>
    public int StackExtent => CardHeight + (int)Math.Round(StackGapDips * Scale);

    /// <summary>Strips the titlebar and border, and keeps the card out of Alt-Tab.</summary>
    private void ConfigureChrome()
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }
        _appWindow.IsShownInSwitchers = false;

        // WS_EX_NOACTIVATE keeps the capture target focused; WS_EX_TOOLWINDOW hides it from the taskbar.
        var exStyle = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle).ToInt64();
        NativeMethods.SetWindowLongPtr(
            _handle,
            NativeMethods.GwlExStyle,
            new IntPtr(exStyle | NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow));

        // Strip the frame styles SetBorderAndTitleBar leaves behind, then force a frame
        // recalculation — otherwise the XAML island stays sized to the old, smaller client
        // area and a dark band shows along the right/bottom edge.
        var style = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlStyle).ToInt64();
        NativeMethods.SetWindowLongPtr(
            _handle,
            NativeMethods.GwlStyle,
            new IntPtr(style & ~(NativeMethods.WsCaption | NativeMethods.WsThickFrame)));
        NativeMethods.SetWindowPos(
            _handle, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder |
            NativeMethods.SwpNoActivate | NativeMethods.SwpFrameChanged);

        // Resize after the frame is gone, so the island lays out to the full window.
        _appWindow.Resize(new Windows.Graphics.SizeInt32(CardWidth, CardHeight));

        ApplyDwmChrome();
    }

    /// <summary>Rounds the frame via DWM and suppresses its border; re-applied on every show
    /// because the first show can reset attributes set while hidden.</summary>
    private void ApplyDwmChrome()
    {
        var corner = NativeMethods.DwmwcpRound;
        NativeMethods.DwmSetWindowAttribute(_handle, NativeMethods.DwmwaWindowCornerPreference, ref corner, sizeof(int));

        var borderColor = NativeMethods.DwmwaColorNone;
        NativeMethods.DwmSetWindowAttribute(_handle, NativeMethods.DwmwaBorderColor, ref borderColor, sizeof(int));
    }

    /// <summary>
    /// Re-asserts HWND_TOPMOST. <see cref="OverlappedPresenter.IsAlwaysOnTop"/> alone loses to other
    /// topmost windows (and to full-screen apps), so the card is re-raised whenever it is shown or moved.
    /// </summary>
    private void BringToTop() => NativeMethods.SetWindowPos(
        _handle,
        NativeMethods.HwndTopmost,
        0, 0, 0, 0,
        NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);

    private async Task LoadThumbnailAsync()
    {
        try
        {
            // Decode at 2x the card width so the thumbnail stays crisp on high-DPI displays.
            PreviewImage.Source = await ImageLoader.LoadAsync(_item.FilePath, CardWidth * 2);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            Close();
        }
    }

    public async Task RefreshAsync(ScreenshotHistoryItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _item = item;
        var aspect = item.Width > 0 && item.Height > 0 ? (double)item.Height / item.Width : 110.0 / 168.0;
        _cardHeightDips = Math.Clamp((int)Math.Round(CardWidthDips * aspect), MinCardHeightDips, MaxCardHeightDips);
        _appWindow.Resize(new Windows.Graphics.SizeInt32(CardWidth, CardHeight));
        PreviewImage.Source = await ImageLoader.LoadAsync(item.FilePath, CardWidth * 2, cancellationToken);
    }

    private void WireInteractions()
    {
        Root.PointerEntered += (_, _) => SetHoverState(true);
        Root.PointerExited += (_, _) => SetHoverState(false);
        Root.KeyDown += Root_KeyDown;

        // Drag the thumbnail straight into another application. A completed drop means the
        // capture reached its destination, so the card has done its job and dismisses itself.
        Card.CanDrag = true;
        Card.DragStarting += Card_DragStarting;
        Card.DropCompleted += (_, args) =>
        {
            if (args.DropResult != DataPackageOperation.None) Close();
        };

        Closed += (_, _) =>
        {
            IsClosed = true;
            _dismissTimer.Stop();
        };
    }

    private void StartAutoDismiss()
    {
        var seconds = _services.Settings.PreviewDismissSeconds;
        if (seconds <= 0) return;
        _dismissTimer.Interval = TimeSpan.FromSeconds(seconds);
        _dismissTimer.Tick += (_, _) => DismissWithAnimation();
        _dismissTimer.Start();
    }

    /// <summary>Shows the window without taking focus away from whatever the user was doing.</summary>
    public void ShowWithoutActivating()
    {
        NativeMethods.ShowWindow(_handle, NativeMethods.SwShowNoActivate);
        ApplyDwmChrome();
        BringToTop();
    }

    /// <summary>
    /// Positions the card at the bottom-left of <paramref name="workArea"/>. Offset 0 sits on the
    /// bottom edge and later cards stack upward, matching CleanShot X. The offset is in physical
    /// pixels because card heights vary with each capture's aspect ratio.
    /// </summary>
    public void MoveToStackPosition(Windows.Graphics.RectInt32 workArea, int stackOffset)
    {
        if (_isDismissing) return; // A reflow must not fight the dismissal slide.
        var margin = (int)Math.Round(EdgeMarginDips * Scale);
        var x = workArea.X + margin;
        var y = workArea.Y + workArea.Height - margin - CardHeight - stackOffset;
        _appWindow.Move(new Windows.Graphics.PointInt32(x, Math.Max(workArea.Y + margin, y)));
        BringToTop();
    }

    private void SetHoverState(bool isHovered)
    {
        HoverLayer.IsHitTestVisible = isHovered;
        Animate(HoverLayer, isHovered ? 1 : 0);
        Animate(CloseButton, isHovered ? 1 : 0);

        // Hovering means the user is dealing with the card; do not yank it away mid-interaction.
        if (isHovered) _dismissTimer.Stop();
        else if (!IsPinned && _services.Settings.PreviewDismissSeconds > 0) _dismissTimer.Start();
    }

    private static void Animate(UIElement element, double toOpacity)
    {
        var animation = new DoubleAnimation
        {
            To = toOpacity,
            Duration = new Duration(TimeSpan.FromMilliseconds(130)),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private async void Card_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(_item.FilePath);
            args.Data.RequestedOperation = DataPackageOperation.Copy;
            args.Data.SetStorageItems([file]);
            // Text fields cannot accept a file, so also offer the path as plain text.
            args.Data.SetText(_item.FilePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            args.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var control = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape: DismissWithAnimation(); break;
            case Windows.System.VirtualKey.C when control: Copy_Click(sender, new RoutedEventArgs()); break;
            case Windows.System.VirtualKey.S when control: Save_Click(sender, new RoutedEventArgs()); break;
            case Windows.System.VirtualKey.E when control: Edit_Click(sender, new RoutedEventArgs()); break;
            case Windows.System.VirtualKey.P when control: Pin_Click(sender, new RoutedEventArgs()); break;
            default: return;
        }
        e.Handled = true;
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { await _services.Clipboard.CopyImageAsync(_item.FilePath, CancellationToken.None); }
        catch (Exception exception) { _services.Logger.Error("preview.copy_failed", exception); return; }
        Close();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker();
        picker.FileTypeChoices.Add("PNG image", [".png"]);
        picker.SuggestedFileName = Path.GetFileNameWithoutExtension(_item.FilePath);
        InitializeWithWindow.Initialize(picker, _handle);

        var destination = await picker.PickSaveFileAsync();
        if (destination is null) return;
        try
        {
            File.Copy(_item.FilePath, destination.Path, true);
            Close();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _services.Logger.Error("preview.save_failed", exception);
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        var editor = new EditorWindow(_item, _services);
        _services.Theme.Register(editor);
        editor.ActivateToFront();
        Close();
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        IsPinned = !IsPinned;
        if (IsPinned) _dismissTimer.Stop();
        PinButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            IsPinned ? Windows.UI.Color.FromArgb(0xE6, 0x0A, 0x84, 0xFF) : Windows.UI.Color.FromArgb(0xE6, 0x20, 0x20, 0x24));
        PinnedChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e) => DismissWithAnimation();

    /// <summary>Fades the card out (~180 ms with a slight leftward drift) before closing. The
    /// whole HWND fades via WS_EX_LAYERED alpha: XAML opacity would only fade the content into
    /// the window's opaque backdrop.</summary>
    private void DismissWithAnimation()
    {
        if (_isDismissing || IsClosed) return;
        _isDismissing = true;
        _dismissTimer.Stop();

        var exStyle = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle).ToInt64();
        NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExStyle, new IntPtr(exStyle | NativeMethods.WsExLayered));
        // A freshly layered window has no alpha set and may stop painting; pin it opaque first.
        NativeMethods.SetLayeredWindowAttributes(_handle, 0, 255, NativeMethods.LwaAlpha);

        const double durationMs = 180;
        var slide = (int)Math.Round(8 * Scale);
        var origin = _appWindow.Position;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(10);
        timer.Tick += (_, _) =>
        {
            var progress = Math.Min(1, stopwatch.Elapsed.TotalMilliseconds / durationMs);
            var eased = progress * (2 - progress); // Ease-out.
            NativeMethods.SetLayeredWindowAttributes(_handle, 0, (byte)(255 * (1 - eased)), NativeMethods.LwaAlpha);
            _appWindow.Move(new Windows.Graphics.PointInt32(origin.X - (int)(eased * slide), origin.Y));

            if (progress < 1) return;
            timer.Stop();
            if (!IsClosed) Close(); // Removal from the stack happens only now, via Closed.
        };
        timer.Start();
    }
}
