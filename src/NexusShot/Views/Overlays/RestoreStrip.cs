using System.Runtime.InteropServices;
using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;
using static NexusShot.Views.Win32Window;

namespace NexusShot.Views;

/// <summary>
/// Recently closed captures across the top of the screen; clicking one restores its card. Unlike a
/// card it takes focus, because Esc and clicking elsewhere are how it goes away.
/// </summary>
public sealed partial class RestoreStrip : PopupWindow
{
    private const int VkEscape = 0x1B;

    private const double EdgeMargin = 12;

    private readonly QuickAccess _stack;
    private readonly UiThreadDispatch _dispatch;

    private Ui? _ui;
    private double _scale = 1;
    private double S(double units) => units * _scale;

    private StripArrangement _layout = new([], Rect.Empty, Rect.Empty);
    private bool _acrylic;

    /// <summary>Uploaded thumbnails, and decoded pixels waiting for the render thread.</summary>
    private readonly Dictionary<string, ImageSurface> _thumbnails = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DecodedImage> _decoded = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _requested = new(StringComparer.OrdinalIgnoreCase);

    private bool _tracking;

    public event Action<ScreenshotHistoryItem>? RestoreRequested;
    public event Action? HistoryRequested;
    public event Action? Dismissed;

    public RestoreStrip(QuickAccess stack)
        : base("NexusShot recently closed", translucent: true)
    {
        _stack = stack;
        // Not in OnCreated: the message loop raises that after the thumbnail worker may post.
        _dispatch = new UiThreadDispatch(Handle);
    }

    public void Open()
    {
        ApplyBackdrop();
        Relayout();
        Show();
        SetForeground();
    }

    /// <summary>Windows 11's acrylic behind the band. Older builds refuse the attribute, and the band
    /// is drawn solid instead.</summary>
    private void ApplyBackdrop()
    {
        WindowInterop.SetDwmAttribute(Handle, WindowInterop.DWMWA_WINDOW_CORNER_PREFERENCE, WindowInterop.DWMWCP_ROUND);
        WindowInterop.SetDwmAttribute(Handle, WindowInterop.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
        _acrylic = WindowInterop.ExtendFrameIntoClientArea(Handle)
            && WindowInterop.SetDwmAttribute(Handle, WindowInterop.DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_TRANSIENTWINDOW);
    }

    /// <summary>A band across the screen, refitted as the restore list shrinks.</summary>
    public void Relayout()
    {
        var work = Monitors.WorkAreaUnderCursor();
        _scale = Monitors.DpiScaleUnderCursor(Handle);

        var width = work.Width / _scale - EdgeMargin * 2;
        _layout = RestoreStripLayout.Arrange(_stack.RecentlyClosed.Count, width);

        var w = (int)Math.Round(S(width));
        var h = (int)Math.Round(S(RestoreStripLayout.Height));
        WindowInterop.SetWindowPos(Handle, HWND_TOPMOST,
            work.X + (work.Width - w) / 2, work.Y + (int)Math.Round(S(EdgeMargin)), w, h, SWP_SHOWWINDOW);

        RequestThumbnails();
        Invalidate();
    }

    /// <summary>Decodes on the media worker, so the strip opens at once and tiles fill in.</summary>
    private void RequestThumbnails()
    {
        var paths = _stack.RecentlyClosed.Take(_layout.Tiles.Count)
            .Select(item => item.FilePath)
            .Where(_requested.Add)
            .ToArray();
        if (paths.Length == 0) return;

        // Twice the tile, so a capture scaled to cover it is never upscaled.
        var width = (int)Math.Ceiling(S(RestoreStripLayout.TileWidth * 2));
        var height = (int)Math.Ceiling(S(RestoreStripLayout.TileHeight * 2));
        _ = MediaWorker.Run(() =>
        {
            foreach (var path in paths)
            {
                DecodedImage pixels;
                try { pixels = ImageSurface.DecodeScaled(path, maxWidth: width, maxHeight: height); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                    or InvalidOperationException or ExternalException)
                {
                    Log.Error("restore.thumbnail", exception, path);
                    continue;
                }

                // Discarded, not leaked, if the strip closed while this was decoding.
                _dispatch.Post(() => { _decoded[path] = pixels; Invalidate(); }, pixels.Dispose);
            }
            return true;
        }).ContinueWith(task => Log.Error("restore.thumbnails", task.Exception!.InnerException!),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    protected override void Render(IComObject<ID2D1HwndRenderTarget> renderTarget)
    {
        using var target = renderTarget.AsRenderTarget();
        target.Object.SetDpi(96, 96);

        _ui ??= new Ui(Resources(target));
        _ui.Theme = SystemTheme.Resolve(AppTheme.Dark, _stack.Settings.Accent);
        _ui.Scale = _scale;
        var ui = _ui;
        var theme = ui.Theme;

        var client = ClientRect;
        renderTarget.Clear(new D3DCOLORVALUE(0, 0, 0, 0));
        ui.BeginFrame(target, PointerInClient());
        ui.FillRect(new Rect(0, 0, client.Width, client.Height), _acrylic ? AcrylicTint : theme.SurfaceWindow);

        var closed = _stack.RecentlyClosed;
        for (var i = 0; i < _layout.Tiles.Count && i < closed.Count; i++)
            DrawTile(ui, target, i, _layout.Tiles[i], closed[i]);

        if (closed.Count == 0)
            ui.Text("Nothing closed recently", Scaled(_layout.EmptyMessage),
                theme.TextSecondary, S(Metrics.FontSm), align: TextAlign.Center);

        DrawHistoryButton(ui, Scaled(_layout.History));

        ui.EndFrame();
        if (ui.ClickedThisFrame) Invalidate();
    }

    private Rect Scaled(Rect design) => new(S(design.X), S(design.Y), S(design.Width), S(design.Height));

    private static readonly int TileId = Ui.Id("restore.tile");
    private static readonly Rgba AcrylicTint = new(0x14, 0x14, 0x17, 0x80);
    private static readonly Rgba PillPressed = new(0x00, 0x00, 0x00, 0x33);

    /// <summary>The capture filling a rounded tile. Hovering rings it and shows Restore beneath.</summary>
    private void DrawTile(Ui ui, IComObject<ID2D1RenderTarget> target, int index, Rect tile, ScreenshotHistoryItem item)
    {
        var bounds = Scaled(tile);
        var radius = (float)S(RestoreStripLayout.TileRadius);

        ui.FillRounded(bounds, radius, ui.Theme.SurfaceRaised);
        if (Thumbnail(target, item.FilePath) is { } thumbnail)
            ui.DrawBitmapRounded(thumbnail.Bitmap, bounds, radius,
                bounds.Cover(new Size(thumbnail.Width, thumbnail.Height)));

        var pill = Scaled(RestoreStripLayout.RestorePill(tile));
        if (!bounds.Contains(ui.Pointer) && !pill.Contains(ui.Pointer))
        {
            ui.StrokeRounded(bounds, radius, ui.Theme.StrokeSubtle);
            return;
        }

        // Just outside the tile, following its corners, so it frames the image rather than covering it.
        var ring = S(1.5);
        ui.StrokeRounded(bounds.Deflate(-ring / 2), radius + (float)(ring / 2), ui.Theme.Accent, (float)ring);

        var id = Ui.Id(TileId, index);
        var clicked = ui.Interact(id, pill);
        ui.FillRounded(pill, (float)(pill.Height / 2), ui.Theme.Accent);
        if (ui.IsActive(id)) ui.FillRounded(pill, (float)(pill.Height / 2), PillPressed);
        ui.Icon(Icons.Undo, new Rect(pill.X + S(10), pill.Y, S(14), pill.Height), ui.Theme.TextOnAccent, S(13));
        ui.Text("Restore", new Rect(pill.X + S(26), pill.Y, pill.Width - S(34), pill.Height),
            ui.Theme.TextOnAccent, S(Metrics.FontXs), Weight.Bold, TextAlign.Center);

        // Posted: a restore reflows this window, which must not happen inside the frame drawing it.
        if (clicked) _dispatch.Post(() => Restore(item));
    }

    private void DrawHistoryButton(Ui ui, Rect bounds)
    {
        if (ui.Button(Ui.Id("restore.history"), bounds, "History", ButtonStyle.Secondary, Icons.History, small: true))
            _dispatch.Post(() =>
            {
                HistoryRequested?.Invoke();
                Close();
            });
    }

    private void Restore(ScreenshotHistoryItem item)
    {
        RestoreRequested?.Invoke(item);
        if (_stack.RecentlyClosed.Count == 0) Close();
        else Relayout();
    }

    private ImageSurface? Thumbnail(IComObject<ID2D1RenderTarget> target, string path)
    {
        if (_thumbnails.TryGetValue(path, out var cached)) return cached;
        if (!_decoded.Remove(path, out var pixels)) return null;

        using (pixels)
        {
            using var context = target.AsDeviceContext();
            if (context is null) return null;
            var surface = ImageSurface.Upload(pixels, context);
            _thumbnails[path] = surface;
            return surface;
        }
    }

    private Point PointerInClient()
    {
        WindowInterop.GetCursorPos(out var screen);
        WindowInterop.ScreenToClient(Handle, ref screen);
        return new Point(screen.X, screen.Y);
    }

    protected override LRESULT? WindowProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case UiThreadDispatch.Message:
                _dispatch.Drain();
                return new LRESULT { Value = 0 };

            // Deactivated by a click elsewhere: dismiss.
            case WmActivate when (wParam.Value & 0xFFFF) == 0:
                _dispatch.Post(() => Close());
                break;

            case WmKeyDown when wParam.Value == VkEscape:
                Close();
                return new LRESULT { Value = 0 };

            case WmMouseMove:
                if (!_tracking) _tracking = WindowInterop.TrackMouseLeave(Handle);
                Invalidate();
                return new LRESULT { Value = 0 };

            case WmMouseLeave:
                _tracking = false;
                Invalidate();
                return new LRESULT { Value = 0 };

            case WmLButtonDown:
                _ui?.NotePress(ClientPoint(lParam));
                WindowInterop.SetCapture(Handle);
                Invalidate();
                return new LRESULT { Value = 0 };

            case WmLButtonUp:
                _ui?.NoteRelease(ClientPoint(lParam));
                if (WindowInterop.GetCapture() == Handle) WindowInterop.ReleaseCapture();
                Invalidate();
                return new LRESULT { Value = 0 };

            // Capture taken away mid-press: the release will never arrive, so nothing stays pressed.
            case WmCaptureChanged when _ui is { PointerDown: true } pressed:
                pressed.CancelPress();
                Invalidate();
                return new LRESULT { Value = 0 };
        }
        return base.WindowProc(hwnd, msg, wParam, lParam);
    }

    protected override void OnDestroyed(object? sender, EventArgs e)
    {
        _dispatch.Clear();
        foreach (var surface in _thumbnails.Values) surface.Dispose();
        foreach (var pixels in _decoded.Values) pixels.Dispose();
        _thumbnails.Clear();
        _decoded.Clear();

        base.OnDestroyed(sender, e);
        Dismissed?.Invoke();
    }

    private const int DWMSBT_TRANSIENTWINDOW = 3;
}
