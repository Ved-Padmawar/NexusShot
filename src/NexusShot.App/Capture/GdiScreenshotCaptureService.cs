using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using NexusShot.App.Enums;
using NexusShot.App.Models;
using NexusShot.App.Services;
using Windows.Graphics;

namespace NexusShot.App.Capture;

/// <summary>
/// Per-monitor-DPI-aware GDI capture service. Coordinates are physical pixels because
/// the app manifest opts into PerMonitorV2 awareness.
/// </summary>
public sealed class GdiScreenshotCaptureService : IScreenshotCaptureService
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const long MaximumCapturePixels = 268_435_456; // 1 GiB at 32bpp.

    public Task<CaptureResult> CaptureFullScreenAsync(CancellationToken cancellationToken)
    {
        var desktop = GetVirtualDesktopBounds();
        return CaptureAsync(desktop, CaptureMode.FullScreen, cancellationToken);
    }

    public Task<CaptureResult> CaptureRegionAsync(RectInt32 region, CancellationToken cancellationToken)
    {
        var captureBounds = Intersect(region, GetVirtualDesktopBounds());
        return CaptureAsync(captureBounds, CaptureMode.Region, cancellationToken);
    }

    public Task<CaptureResult> CaptureActiveWindowAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || !GetWindowRect(window, out var rect))
            throw new InvalidOperationException("Could not determine the active window.");

        var captureBounds = Intersect(
            new RectInt32(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top),
            GetVirtualDesktopBounds());
        return CaptureAsync(captureBounds, CaptureMode.ActiveWindow, cancellationToken);
    }

    private static Task<CaptureResult> CaptureAsync(RectInt32 bounds, CaptureMode mode, CancellationToken cancellationToken)
    {
        ValidateBounds(bounds.Width, bounds.Height);
        return Task.Run(() => CaptureCore(bounds, mode, cancellationToken), cancellationToken);
    }

    private static CaptureResult CaptureCore(RectInt32 bounds, CaptureMode mode, CancellationToken cancellationToken)
    {
        string? path = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);

            cancellationToken.ThrowIfCancellationRequested();
            path = Path.Combine(Path.GetTempPath(), $"NexusShot_{Guid.NewGuid():N}.png");
            bitmap.Save(path, ImageFormat.Png);
            cancellationToken.ThrowIfCancellationRequested();
            return new CaptureResult { TemporaryFilePath = path, Width = bounds.Width, Height = bounds.Height, Mode = mode, SourceBounds = bounds };
        }
        catch
        {
            if (path is not null)
            {
                try { File.Delete(path); }
                catch (IOException) { /* A failed cleanup must not hide capture failure. */ }
                catch (UnauthorizedAccessException) { /* Same principle for locked temp files. */ }
            }
            throw;
        }
    }

    private static RectInt32 GetVirtualDesktopBounds() => new(
        GetSystemMetrics(SmXVirtualScreen),
        GetSystemMetrics(SmYVirtualScreen),
        GetSystemMetrics(SmCxVirtualScreen),
        GetSystemMetrics(SmCyVirtualScreen));

    private static RectInt32 Intersect(RectInt32 requested, RectInt32 available)
    {
        var left = Math.Max(requested.X, available.X);
        var top = Math.Max(requested.Y, available.Y);
        var right = Math.Min((long)requested.X + requested.Width, (long)available.X + available.Width);
        var bottom = Math.Min((long)requested.Y + requested.Height, (long)available.Y + available.Height);
        if (right <= left || bottom <= top) throw new ArgumentOutOfRangeException(nameof(requested), "The capture area is outside the virtual desktop.");
        return new RectInt32(left, top, checked((int)(right - left)), checked((int)(bottom - top)));
    }

    private static void ValidateBounds(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "The capture area must have positive dimensions.");
        if ((long)width * height > MaximumCapturePixels)
            throw new ArgumentOutOfRangeException(nameof(width), "The requested capture area is too large.");
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
}
