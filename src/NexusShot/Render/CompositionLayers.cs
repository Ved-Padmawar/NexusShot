using System.Runtime.InteropServices;
using NexusShot.Core;

namespace NexusShot.Render;

/// <summary>
/// A window as DirectComposition layers: backdrop, a scrolling layer, and chrome over both. Scrolling
/// moves the layer's offset on the compositor's thread, so nothing is redrawn to scroll - the model
/// browsers use. All surfaces share one Direct2D device, so <see cref="Resources"/> serve them all.
/// </summary>
public sealed class CompositionLayers : IDisposable
{
    private static readonly Guid IID_DesktopDevice = typeof(IDCompositionDesktopDevice).GUID;
    private static readonly Guid IID_DeviceContext = typeof(ID2D1DeviceContext).GUID;

    private readonly IComObject<ID2D1Device> _device;
    private readonly IComObject<IDCompositionDesktopDevice> _composition;
    private readonly IComObject<IDCompositionTarget> _target;
    private readonly IComObject<IDCompositionVisual2> _root;
    private readonly IComObject<IDCompositionVisual2> _backdrop;
    private readonly IComObject<IDCompositionVisual2> _scrollClip;
    private readonly IComObject<IDCompositionVisual2> _scroll;
    private readonly IComObject<IDCompositionVisual2> _chrome;

    private IComObject<IDCompositionSurface>? _backdropSurface;
    private IComObject<IDCompositionSurface>? _chromeSurface;
    private IComObject<IDCompositionVirtualSurface>? _scrollSurface;
    private (int Width, int Height) _size;
    private (int Width, int Height) _scrollSize;
    private Rgba _backdropColor;

    public IComObject<ID2D1Factory1> Factory { get; }

    /// <summary>Never drawn on; brushes and bitmaps are made from it.</summary>
    public IComObject<ID2D1DeviceContext> Resources { get; }

    public CompositionLayers(IntPtr window)
    {
        (_device, Factory) = D2DDevice.Create();
        Resources = _device.CreateDeviceContext();
        Resources.Object.SetDpi(96, 96);

        var device = ComObject.GetOrCreateComInstance(_device.Object);
        try
        {
            Functions.DCompositionCreateDevice2(device, IID_DesktopDevice, out var raw).ThrowOnError();
            _composition = ComObject.FromPointer<IDCompositionDesktopDevice>(raw, CreateObjectFlags.None, true)!;
            Marshal.Release(raw);
        }
        finally
        {
            Marshal.Release(device);
        }

        _composition.Object.CreateTargetForHwnd(new HWND { Value = window }, true, out var target).ThrowOnError();
        _target = new ComObject<IDCompositionTarget>(target);

        _root = Visual();
        _backdrop = Visual();
        _scrollClip = Visual();
        _scroll = Visual();
        _chrome = Visual();

        _root.Object.AddVisual(_backdrop.Object, false, null!).ThrowOnError();
        _root.Object.AddVisual(_scrollClip.Object, true, _backdrop.Object).ThrowOnError();
        _root.Object.AddVisual(_chrome.Object, true, _scrollClip.Object).ThrowOnError();
        _scrollClip.Object.AddVisual(_scroll.Object, false, null!).ThrowOnError();
        _target.Object.SetRoot(_root.Object).ThrowOnError();
    }

    private IComObject<IDCompositionVisual2> Visual()
    {
        _composition.Object.CreateVisual(out var visual).ThrowOnError();
        return new ComObject<IDCompositionVisual2>(visual);
    }

    /// <summary>Sizes the backdrop and chrome to the client area; a no-op while nothing changed.</summary>
    public void Resize(int width, int height, Rgba background)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (_size == (width, height) && _backdropColor == background && _backdropSurface is not null) return;

        if (_size != (width, height) || _chromeSurface is null)
        {
            _chromeSurface?.Dispose();
            _chromeSurface = Surface(width, height);
            SetContent(_chrome, _chromeSurface.Object);
        }

        _backdropSurface?.Dispose();
        _backdropSurface = Surface(width, height);
        SetContent(_backdrop, _backdropSurface.Object);
        using (var context = BeginDraw(_backdropSurface.Object, null, out _))
            context.Object.Clear(D2DResources.ToD3D(background));
        _backdropSurface.Object.EndDraw().ThrowOnError();

        _size = (width, height);
        _backdropColor = background;
    }

    /// <summary>Places the scrolling layer: its viewport in client pixels, its content size, its scroll.</summary>
    public void PlaceScroll(Rect viewport, int contentWidth, int contentHeight, double scroll)
    {
        contentWidth = Math.Max(1, contentWidth);
        contentHeight = Math.Max(1, contentHeight);
        if (_scrollSurface is null)
        {
            _composition.Object.CreateVirtualSurface((uint)contentWidth, (uint)contentHeight,
                DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_PREMULTIPLIED,
                out var surface).ThrowOnError();
            _scrollSurface = new ComObject<IDCompositionVirtualSurface>(surface);
            SetContent(_scroll, _scrollSurface.Object);
            _scrollSize = (contentWidth, contentHeight);
        }
        else if (_scrollSize != (contentWidth, contentHeight))
        {
            _scrollSurface.Object.Resize((uint)contentWidth, (uint)contentHeight).ThrowOnError();
            _scrollSize = (contentWidth, contentHeight);
        }

        _scrollClip.Object.SetOffsetX((float)viewport.X).ThrowOnError();
        _scrollClip.Object.SetOffsetY((float)viewport.Y).ThrowOnError();
        var clip = new D2D_RECT_F(0, 0, (float)viewport.Width, (float)viewport.Height);
        _scrollClip.Object.SetClip(in clip).ThrowOnError();

        // Whole pixels: a fractional offset resamples the layer, and text goes soft while it moves.
        _scroll.Object.SetOffsetY(-(float)Math.Round(scroll)).ThrowOnError();
    }

    /// <summary>Opens <paramref name="area"/> (content pixels) for drawing in client coordinates, given
    /// where the content's origin sits now. Every pixel of the area must be painted: none are kept.</summary>
    public IComObject<ID2D1DeviceContext> BeginScroll(RECT area, Point origin)
    {
        var context = BeginDraw(_scrollSurface!.Object, area, out var offset);
        context.Object.SetTransform(D2D_MATRIX_3X2_F.Translation(
            (float)(offset.x - area.left - origin.X), (float)(offset.y - area.top - origin.Y)));
        return context;
    }

    public void EndScroll() => _scrollSurface!.Object.EndDraw().ThrowOnError();

    /// <summary>Empties the scrolling layer, for a grid with nothing in it.</summary>
    public void ClearScroll() => _scrollSurface?.Object.Trim(0, 0).ThrowOnError();

    /// <summary>Frees every pixel outside <paramref name="keep"/>.</summary>
    public unsafe void TrimScroll(RECT keep) => _scrollSurface?.Object.Trim((nint)(&keep), 1).ThrowOnError();

    /// <summary>Opens the chrome for a whole redraw, cleared to transparent.</summary>
    public IComObject<ID2D1DeviceContext> BeginChrome()
    {
        var context = BeginDraw(_chromeSurface!.Object, null, out var offset);
        context.Object.SetTransform(D2D_MATRIX_3X2_F.Translation(offset.x, offset.y));
        context.Object.Clear(new D3DCOLORVALUE(0, 0, 0, 0));
        return context;
    }

    public void EndChrome() => _chromeSurface!.Object.EndDraw().ThrowOnError();

    public void Commit() => _composition.Object.Commit().ThrowOnError();

    private IComObject<IDCompositionSurface> Surface(int width, int height)
    {
        _composition.Object.CreateSurface((uint)width, (uint)height, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_PREMULTIPLIED, out var surface).ThrowOnError();
        return new ComObject<IDCompositionSurface>(surface);
    }

    private static void SetContent(IComObject<IDCompositionVisual2> visual, object surface)
    {
        var pointer = ComObject.GetOrCreateComInstance(surface);
        try { visual.Object.SetContent(pointer).ThrowOnError(); }
        finally { Marshal.Release(pointer); }
    }

    /// <summary>The surface's context for <paramref name="area"/>, at 96 DPI so a unit is a pixel.</summary>
    private static unsafe IComObject<ID2D1DeviceContext> BeginDraw(IDCompositionSurface surface, RECT? area, out POINT offset)
    {
        var iid = IID_DeviceContext;
        IntPtr raw;
        if (area is { } rect) surface.BeginDraw((nint)(&rect), in iid, out raw, out offset).ThrowOnError();
        else surface.BeginDraw(0, in iid, out raw, out offset).ThrowOnError();

        var context = ComObject.FromPointer<ID2D1DeviceContext>(raw, CreateObjectFlags.None, true)!;
        Marshal.Release(raw);
        context.Object.SetDpi(96, 96);
        return context;
    }

    private static unsafe IComObject<ID2D1DeviceContext> BeginDraw(IDCompositionVirtualSurface surface, RECT area, out POINT offset)
    {
        var iid = IID_DeviceContext;
        surface.BeginDraw((nint)(&area), in iid, out var raw, out offset).ThrowOnError();
        var context = ComObject.FromPointer<ID2D1DeviceContext>(raw, CreateObjectFlags.None, true)!;
        Marshal.Release(raw);
        context.Object.SetDpi(96, 96);
        return context;
    }

    public void Dispose()
    {
        _chromeSurface?.Dispose();
        _backdropSurface?.Dispose();
        _scrollSurface?.Dispose();
        _chrome.Dispose();
        _scroll.Dispose();
        _scrollClip.Dispose();
        _backdrop.Dispose();
        _root.Dispose();
        _target.Dispose();
        _composition.Dispose();
        Resources.Dispose();
        _device.Dispose();
        Factory.Dispose();
    }
}
