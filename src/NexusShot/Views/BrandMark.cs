using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>The NexusShot logo, from the exe's own icon at the exact pixel size drawn, so it stays
/// crisp at any scale and no second copy of the artwork ships.</summary>
internal sealed class BrandMark : IDisposable
{
    private ImageSurface? _surface;
    private int _size;

    public void Draw(Ui ui, Rect bounds)
    {
        var size = (int)Math.Round(bounds.Width);
        if (_surface is null || _size != size)
        {
            using var context = ui.DeviceContext();
            if (context is null) return;
            var icon = AppIcon.LoadOwned(size);
            if (icon == IntPtr.Zero) return;
            try
            {
                _surface?.Dispose();
                _surface = ImageSurface.FromIcon(icon, context);
                _size = size;
            }
            finally
            {
                AppIcon.Destroy(icon);
            }
        }
        ui.DrawBitmapRounded(_surface.Bitmap, bounds, 0, bounds);
    }

    public void Dispose()
    {
        _surface?.Dispose();
        _surface = null;
    }
}
