# Cause

`DrawCaptionButtons()` runs inside the Direct2D frame and calls `ShowWindow(SW_MAXIMIZE/SW_RESTORE)` synchronously. `ShowWindow` immediately re-enters `WM_SIZE`; `EditorWindow.OnResized()` then calls `ID2D1HwndRenderTarget.Resize()` before the active frame reaches `EndDraw()`. Direct2D returns `0x88990001` (`D2DERR_WRONG_STATE`). The captured native stack maps exactly to `Resize -> D2DRenderWindow.OnResized -> EditorWindow.OnResized`. This is not an installer, GPU, or clip-stack failure.

# Fix

`CaptionWindow` now queues minimize/maximize/restore with `PostMessageW(WM_SYSCOMMAND, SC_MINIMIZE/SC_MAXIMIZE/SC_RESTORE)` instead of calling `ShowWindow` from `Render()`. The queued command runs after `EndDraw()`, so the resulting `WM_SIZE` safely resizes the render target. The fix covers minimize as well as maximize/restore.

References: [Direct2D render-target drawing state](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nn-d2d1-id2d1hwndrendertarget), [WM_SYSCOMMAND](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-syscommand).
