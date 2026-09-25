using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using NexusShot.Core;

namespace NexusShot.Platform;

/// <summary>
/// Touchpad panning through DirectManipulation, as browsers read it. Without it Windows synthesises
/// WM_MOUSEWHEEL with jumps of 2000 units and bursts in both directions (+409, -447, +566 in 50 ms).
/// The viewport scrolls nothing: its transform is reported as deltas and reset when a gesture settles.
/// </summary>
public sealed partial class TouchpadPan : IDisposable
{
    /// <summary>The one moment a viewport can claim a touchpad contact.</summary>
    public const uint DM_POINTERHITTEST = 0x0250;

    private const uint CLSCTX_INPROC_SERVER = 1;
    private const int PT_TOUCHPAD = 5;
    private const float Size = 1000;

    private const uint CONFIGURATION_INTERACTION = 0x1;
    private const uint CONFIGURATION_TRANSLATION_X = 0x2;
    private const uint CONFIGURATION_TRANSLATION_Y = 0x4;
    private const uint CONFIGURATION_TRANSLATION_INERTIA = 0x20;
    private const uint CONFIGURATION_RAILS_X = 0x100;
    private const uint CONFIGURATION_RAILS_Y = 0x200;
    private const uint VIEWPORT_OPTIONS_MANUALUPDATE = 0x2;

    private const int STATUS_RUNNING = 3;
    private const int STATUS_INERTIA = 4;
    private const int STATUS_READY = 5;

    private static readonly Guid CLSID_Manager = new("54E211B6-3650-4F75-8334-FA359598E1C5");
    private static readonly Guid IID_Manager = new("FBF5D3B4-70C7-4163-9322-5A6F660D6FBC");
    private static readonly Guid IID_UpdateManager = new("B0AE62FD-BE34-46E7-9CAA-D361FACBB9CC");
    private static readonly Guid IID_Viewport = new("28b85a3d-60a0-48bd-9ba1-5ce8d9ea3a6d");

    private readonly IntPtr _window;
    private readonly Action<double> _panned;
    private readonly Action<bool> _moving;
    private readonly IDirectManipulationManager _manager;
    private readonly IDirectManipulationUpdateManager _updates;
    private readonly IDirectManipulationViewport _viewport;
    private readonly uint _cookie;
    private float _lastY;

    /// <param name="panned">Vertical pixels; positive scrolls toward the top.</param>
    /// <param name="moving">True while a gesture or its inertia runs; <see cref="Update"/> is called every frame until false.</param>
    private TouchpadPan(IntPtr window, Action<double> panned, Action<bool> moving,
        IDirectManipulationManager manager, IDirectManipulationUpdateManager updates, IDirectManipulationViewport viewport)
    {
        _window = window;
        _panned = panned;
        _moving = moving;
        _manager = manager;
        _updates = updates;
        _viewport = viewport;

        _viewport.ActivateConfiguration(CONFIGURATION_INTERACTION | CONFIGURATION_TRANSLATION_X
            | CONFIGURATION_TRANSLATION_Y | CONFIGURATION_TRANSLATION_INERTIA
            | CONFIGURATION_RAILS_X | CONFIGURATION_RAILS_Y);
        _viewport.SetViewportOptions(VIEWPORT_OPTIONS_MANUALUPDATE);

        unsafe
        {
            var handler = (IntPtr)ComInterfaceMarshaller<IDirectManipulationViewportEventHandler>
                .ConvertToUnmanaged(new ViewportEvents(this));
            try { _viewport.AddEventHandler(window, handler, out _cookie); }
            finally { Marshal.Release(handler); }
        }

        var rect = new RECT { right = (int)Size, bottom = (int)Size };
        _viewport.SetViewportRect(rect);
        _manager.Activate(window);
        _viewport.Enable();
        _updates.Update(IntPtr.Zero);
    }

    /// <summary>Null where DirectManipulation is unavailable; the wheel emulation then still scrolls.</summary>
    public static unsafe TouchpadPan? Create(IntPtr window, Action<double> panned, Action<bool> moving)
    {
        try
        {
            if (CoCreateInstance(CLSID_Manager, IntPtr.Zero, CLSCTX_INPROC_SERVER, IID_Manager, out var raw) != 0
                || raw == IntPtr.Zero)
                return null;
            var manager = ComInterfaceMarshaller<IDirectManipulationManager>.ConvertToManaged((void*)raw)!;
            Marshal.Release(raw);

            manager.GetUpdateManager(IID_UpdateManager, out var updatesRaw);
            var updates = ComInterfaceMarshaller<IDirectManipulationUpdateManager>.ConvertToManaged((void*)updatesRaw)!;
            Marshal.Release(updatesRaw);

            manager.CreateViewport(IntPtr.Zero, window, IID_Viewport, out var viewportRaw);
            var viewport = ComInterfaceMarshaller<IDirectManipulationViewport>.ConvertToManaged((void*)viewportRaw)!;
            Marshal.Release(viewportRaw);

            return new TouchpadPan(window, panned, moving, manager, updates, viewport);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException)
        {
            Log.Error("touchpad.init", exception);
            return null;
        }
    }

    /// <summary>Claims touchpad contacts only: a claimed touch-screen contact would stop becoming a click.</summary>
    public void HitTest(nuint wParam)
    {
        var pointer = (uint)(wParam & 0xFFFF);
        if (GetPointerType(pointer, out var type) && type == PT_TOUCHPAD)
            _viewport.SetContact(pointer);
    }

    /// <summary>Advances the gesture one frame.</summary>
    public void Update() => _updates.Update(IntPtr.Zero);

    /// <summary>Recentres the viewport once a gesture settles. The offset is zeroed first: the
    /// recentring reports itself as a content update, which must read as no movement.</summary>
    private void OnStatusChanged(int current)
    {
        _moving(current is STATUS_RUNNING or STATUS_INERTIA);
        if (current != STATUS_READY) return;

        _lastY = 0;
        _viewport.ZoomToRect(0, 0, Size, Size, 0);
    }

    private unsafe void OnContentUpdated(IntPtr content)
    {
        var transform = stackalloc float[6];
        if (ComInterfaceMarshaller<IDirectManipulationContent>.ConvertToManaged((void*)content)
            is not { } target || target.GetContentTransform(transform, 6) != 0)
            return;

        var y = transform[5];
        var delta = y - _lastY;
        _lastY = y;
        if (delta != 0) _panned(delta);
    }

    public void Dispose()
    {
        _viewport.Stop();
        _viewport.RemoveEventHandler(_cookie);
        _viewport.Abandon();
        _manager.Deactivate(_window);
    }

    [GeneratedComClass]
    private sealed partial class ViewportEvents(TouchpadPan owner) : IDirectManipulationViewportEventHandler
    {
        public int OnViewportStatusChanged(IntPtr viewport, int current, int previous)
        {
            owner.OnStatusChanged(current);
            return 0;
        }

        public int OnViewportUpdated(IntPtr viewport) => 0;

        public int OnContentUpdated(IntPtr viewport, IntPtr content)
        {
            owner.OnContentUpdated(content);
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int left, top, right, bottom; }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr instance);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPointerType(uint pointerId, out int type);

    // Declared in full, in header order: a missing member shifts every later call onto the wrong slot.

    [GeneratedComInterface]
    [Guid("FBF5D3B4-70C7-4163-9322-5A6F660D6FBC")]
    internal partial interface IDirectManipulationManager
    {
        void Activate(IntPtr window);
        void Deactivate(IntPtr window);
        void RegisterHitTestTarget(IntPtr window, IntPtr hitTestWindow, int type);
        void ProcessInput(IntPtr message, out int handled);
        void GetUpdateManager(in Guid riid, out IntPtr updateManager);
        void CreateViewport(IntPtr frameInfo, IntPtr window, in Guid riid, out IntPtr viewport);
        void CreateContent(IntPtr frameInfo, in Guid clsid, in Guid riid, out IntPtr content);
    }

    [GeneratedComInterface]
    [Guid("B0AE62FD-BE34-46E7-9CAA-D361FACBB9CC")]
    internal partial interface IDirectManipulationUpdateManager
    {
        void RegisterWaitHandleCallback(IntPtr handle, IntPtr handler, out uint cookie);
        void UnregisterWaitHandleCallback(uint cookie);
        void Update(IntPtr frameInfo);
    }

    [GeneratedComInterface]
    [Guid("28b85a3d-60a0-48bd-9ba1-5ce8d9ea3a6d")]
    internal partial interface IDirectManipulationViewport
    {
        void Enable();
        void Disable();
        void SetContact(uint pointerId);
        void ReleaseContact(uint pointerId);
        void ReleaseAllContacts();
        void GetStatus(out int status);
        void GetTag(in Guid riid, out IntPtr obj, out uint id);
        void SetTag(IntPtr obj, uint id);
        void GetViewportRect(out RECT rect);
        void SetViewportRect(in RECT rect);
        void ZoomToRect(float left, float top, float right, float bottom, int animate);
        void SetViewportTransform(IntPtr matrix, uint count);
        void SyncDisplayTransform(IntPtr matrix, uint count);
        void GetPrimaryContent(in Guid riid, out IntPtr content);
        void AddContent(IntPtr content);
        void RemoveContent(IntPtr content);
        void SetViewportOptions(uint options);
        void AddConfiguration(uint configuration);
        void RemoveConfiguration(uint configuration);
        void ActivateConfiguration(uint configuration);
        void SetManualGesture(uint configuration);
        void SetChaining(uint motion);
        void AddEventHandler(IntPtr window, IntPtr handler, out uint cookie);
        void RemoveEventHandler(uint cookie);
        void SetInputMode(uint mode);
        void SetUpdateMode(uint mode);
        void Stop();
        void Abandon();
    }

    [GeneratedComInterface]
    [Guid("952121DA-D69F-45F9-B0F9-F23944321A6D")]
    internal partial interface IDirectManipulationViewportEventHandler
    {
        [PreserveSig] int OnViewportStatusChanged(IntPtr viewport, int current, int previous);
        [PreserveSig] int OnViewportUpdated(IntPtr viewport);
        [PreserveSig] int OnContentUpdated(IntPtr viewport, IntPtr content);
    }

    [GeneratedComInterface]
    [Guid("b89962cb-3d89-442b-bb58-5098fa0f9f16")]
    internal unsafe partial interface IDirectManipulationContent
    {
        [PreserveSig] int GetContentRect(out RECT rect);
        [PreserveSig] int SetContentRect(in RECT rect);
        [PreserveSig] int GetViewport(in Guid riid, out IntPtr viewport);
        [PreserveSig] int GetTag(in Guid riid, out IntPtr obj, out uint id);
        [PreserveSig] int SetTag(IntPtr obj, uint id);
        [PreserveSig] int GetOutputTransform(float* matrix, uint count);
        [PreserveSig] int GetContentTransform(float* matrix, uint count);
        [PreserveSig] int SyncContentTransform(float* matrix, uint count);
    }
}
