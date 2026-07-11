<div align="center">

<img src="assets/icons/nexus-shot-256.png" alt="NexusShot" width="128" height="128" />

# NexusShot

**A native Windows screenshot utility, modelled on CleanShot X.**

<p>
  <img src="https://img.shields.io/github/v/release/Ved-Padmawar/NexusShot?label=version&color=success" alt="Version" />
  <img src="https://img.shields.io/badge/C%23-239120?logo=csharp&logoColor=white" alt="C#" />
  <img src="https://img.shields.io/badge/-.NET%2010-512BD4?logo=dotnet&logoColor=white" alt=".NET 10" />
  <img src="https://img.shields.io/badge/WinUI%203-0067B8?logo=windows&logoColor=white" alt="WinUI 3" />
  <img src="https://img.shields.io/badge/Windows%20App%20SDK-0078D6?logo=windows&logoColor=white" alt="Windows App SDK" />
  <img src="https://img.shields.io/badge/Windows%2010%2F11-0078D6?logo=windows11&logoColor=white" alt="Windows 10/11" />
</p>

[**⬇ Download**](../../releases/latest) · [Features](#-features) · [Build](#-build-the-installer) · [Architecture](#-architecture)

</div>

---

## ✨ Features

**📸 Capture** — full virtual desktop, active window, and drag-selection region, with correct
multi-monitor and per-monitor-DPI coordinates. Region capture dims the screen over the **live**
desktop — video keeps playing underneath while you drag — with a crosshair and a live
pixel-dimension readout. Pixels are read only after the selection is committed.

**🃏 Quick Access Overlay** — after each capture a borderless thumbnail card appears at the
**bottom-left** of the work area and stacks upward as more captures arrive. It never steals focus
(`WS_EX_NOACTIVATE`) and stays out of Alt-Tab and the taskbar. The card sizes itself to the
capture's aspect ratio, so only the image shows — no frame or backdrop edge. Hovering reveals
Copy, Save as, Edit, and Pin; the card can be dragged straight into another application.
Auto-dismiss is configurable and pauses while the pointer is over the card or when pinned.

**🎨 Editor** — displays the screenshot at fit-to-window scale while keeping all annotation geometry
in image-pixel space. Click-and-drag sets an annotation's position and size in one gesture, and
each shape family gets its own selection model: boxes show eight resize grips, lines and arrows
show endpoint grips, brush strokes (pen/blur/pixelate) leave only their effect until explicitly
selected. Text annotations are editable objects — double-click (or click with the text tool) to
edit in place, with the box itself as the wrapping text area. Crop is an interactive session: a
handle-draggable frame with the outside dimmed, applied only on confirm (`Enter`) and discardable
(`Esc`). Colour palette, stroke thickness, undo/redo, and delete round it out.

**⌨️ Global hotkeys** — `Ctrl+Shift+S` region, `Ctrl+Shift+F` full screen, `Ctrl+Shift+W` active
window, `Ctrl+Shift+N` open dashboard. Registration is all-or-nothing with rollback; a conflict
surfaces in the tray tooltip instead of crashing.

### Keyboard shortcuts

| Context | Shortcuts |
| --- | --- |
| **Overlay** | `Ctrl+C` copy · `Ctrl+S` save · `Ctrl+E` edit · `Ctrl+P` pin · `Esc` dismiss |
| **Editor** | `V` select · `R` rectangle · `E` ellipse · `A` arrow · `L` line · `D` pen · `T` text · `H` highlight · `B` blur · `P` pixelate · `N` counter · `S` spotlight · `C` crop |

---

## 🚀 Getting started

### Requirements

- **Windows 10 2004+** (Windows 11 recommended)
- **.NET 10 SDK**
- **Visual Studio 2022** with the **Windows application development** workload, or the Windows App SDK build tools

### Run from source

```powershell
dotnet restore NexusShot.sln
dotnet build NexusShot.sln -c Debug -p:Platform=x64
dotnet run --project src/NexusShot.App/NexusShot.App.csproj
```

The app starts in the notification area. The dashboard's close button hides it; use **Quit** on the
tray menu to exit.

> Screenshots are saved to `Pictures\NexusShot`. Settings and metadata-only history live in
> `%LOCALAPPDATA%\NexusShot`; corrupt JSON is backed up and regenerated rather than crashing.

---

## 📦 Build the installer

To produce a self-contained, distributable installer `.exe`, run the build script from the
repository root:

```powershell
pwsh -NoProfile -File installer\build-installer.ps1                 # version 1.1.0
pwsh -NoProfile -File installer\build-installer.ps1 -Version 1.2.0  # override the version
```

The script publishes NexusShot self-contained (bundling the .NET and Windows App SDK runtimes, so
the target machine needs no runtime installed) and compiles it with Inno Setup into
`dist\NexusShot-Setup-<version>.exe`.

> **Prerequisite:** [Inno Setup 6](https://jrsoftware.org/isdl.php) — `winget install JRSoftware.InnoSetup`

---

## 🏛 Architecture

```text
assets/icons/  icon-source.svg + export-icons.ps1 -> nexus-shot.ico
src/NexusShot.App/
  Capture/    GDI capture + the layered Win32 region overlay
  Controls/   ToolTile, ColorSwatch, BrandMark (custom templated controls)
  Editor/     EditorDocument (state, undo/redo, selection)
              BoxGeometry (shared crop/shape/text interaction geometry)
              AnnotationFlattener (export)
              LiveAnnotationRenderer (on-screen preview)
  Helpers/    ImageLoader, MonitorHelper
  Hotkeys/    RegisterHotKey lifecycle
  Native/     P/Invoke surface and window subclassing
  Services/   Composition root, storage, clipboard, previews, theme, logging
  Storage/    Atomic JSON persistence
  Themes/     Tokens.xaml (theme dictionaries), Controls.xaml, Generic.xaml
  Tray/       Notification-area icon and command menu
  ViewModels/ MainViewModel, ScreenshotTile (lazy thumbnails)
  Views/      MainWindow, FloatingPreviewWindow, EditorWindow
```

The sections below document the non-obvious design decisions. Expand any that interest you.

<details>
<summary><b>The shell browses; the editor edits</b></summary>

<br />

`MainWindow` is a sidebar of captures plus a detail pane. Annotating opens `EditorWindow` as its
own window rather than docking it into that pane. A docked editor would surrender the sidebar's
width from the image on every edit, permanently, to a list the user has stopped looking at.

</details>

<details>
<summary><b>Theming</b></summary>

<br />

Every brush lives in a `ThemeDictionary` in `Themes/Tokens.xaml` and is consumed via
`{ThemeResource}` — including inside `VisualState.Setters`. `{StaticResource}` snapshots a brush at
load time, so a control realised under one theme would keep that brush forever and the window would
half-switch. The two palettes are not inverses: light uses black-alpha hairlines rather than
lightened white ones, and a darker accent, because `#0A84FF` on white fails contrast for small text.

`ThemeService` walks the open windows and sets `RequestedTheme` on each content root, because WinUI 3
has no application-wide switch that reaches already-open windows (`Application.RequestedTheme` throws
after launch) and each window owns an independent `XamlRoot`. It also drives
`DWMWA_USE_IMMERSIVE_DARK_MODE`, since XAML does not own the non-client area. In *System* mode the
theme follows the OS: the only signal an unpackaged app gets is `WM_SETTINGCHANGE` with
`lParam == "ImmersiveColorSet"`, which arrives on the tray's existing window subclass.

The editor toolbar's tiles and colour swatches are custom templated controls, not restyled
`ToggleButton`/`RadioButton`. A `RadioButton` always lays out a 20px indicator column ahead of its
content, which clipped the colour dots out of their swatches.

</details>

<details>
<summary><b>Icons</b></summary>

<br />

`assets/icons/icon-source.svg` is the design source of truth, sharing the Nexus family's tile
geometry and 135° cyan/steel split. The sibling apps rasterise theirs with `cargo tauri icon`;
NexusShot has no Tauri toolchain, so `export-icons.ps1` redraws the same geometry with
`System.Drawing` and writes a PNG-framed `.ico`. Keep the two in sync when the mark changes.

The glyph's two brackets each straddle the diagonal. An earlier version put one bracket wholly
inside each half, which left the bottom-right bracket as `#18222b` ink on `#3a4652` steel — nearly
no contrast, so it vanished at 16px and the mark read as a single stray corner.

`ApplicationIcon` only brands the **exe file**, which is what Explorer shows. The **taskbar** reads
its icon from the *window*, so each `AppWindow` calls `AppIcon.Apply`. That uses `AppWindow.SetIcon`'s
`IconId` overload over an `HICON` loaded from the module's own resource table, rather than
`SetIcon(string)` — the string overload resolves a filesystem path, which in an unpackaged app
depends on the working directory. **Alt-Tab** reads the HWND's `ICON_BIG`, which `SetIcon` does not
reliably populate, so `Apply` also sends `WM_SETICON` for both sizes. The tray loads the same
resource at `SM_CXSMICON`.

</details>

<details>
<summary><b>The region overlay is not a XAML window</b></summary>

<br />

WinUI 3 has [no supported transparent window](https://github.com/microsoft/microsoft-ui-xaml/issues/7276).
A screenshot tool needs one: the selection area must be genuinely see-through so the live desktop
shows underneath. The usual workaround is to screenshot the desktop first and display that frozen
image full-screen — which is what ShareX does, and why its users
[ask for the opposite](https://github.com/ShareX/ShareX/issues/68). Windows' own Snipping Tool does
not freeze; it dims the live screen.

So `RegionSelectionOverlay` is a plain Win32 `WS_EX_LAYERED` window composited with
`UpdateLayeredWindow`, drawn with GDI+, running its own message loop on a dedicated STA thread.
Nothing else in the app depends on it, and no XAML is involved. The surface is a top-down
32bpp **premultiplied** BGRA DIB: alpha 0 inside the selection, alpha 110 black outside.

Because the app manifest declares `PerMonitorV2`, `GetSystemMetrics(SM_*VIRTUALSCREEN)` returns
physical pixels, so overlay coordinates need no DPI scaling — a virtual desktop spanning monitors
with different scale factors works without correction.

</details>

<details>
<summary><b>Two renderers, on purpose</b></summary>

<br />

`LiveAnnotationRenderer` draws XAML shapes for the on-screen preview. `AnnotationFlattener`
composites the same annotations onto the source bitmap with GDI+ at true pixel resolution.

Export never screen-scrapes the canvas. A `RenderTargetBitmap` of the editor canvas can only
capture what is currently composited on screen, which makes exports depend on window size, scroll
position, and display DPI — and produces a blank image for anything off-screen. Flattening against
the source bitmap keeps saved pixels independent of how the editor happens to be displayed.

Annotations are structured objects and are only rasterised on copy or export.

</details>

---

## 🛠 Developer notes

- Images are loaded with `ImageLoader`, which streams bytes into `BitmapImage.SetSourceAsync`.
  Constructing a `BitmapImage` from a `file://` URI does not decode in an unpackaged WinUI 3 app.
- `MainWindow` hosts the tray icon's message loop, so its close button hides rather than closes.
- Capturing does **not** hide the dashboard, so NexusShot's own window can be captured.
- A `FileSystemWatcher` on the save folder keeps the sidebar synchronized with File Explorer:
  external deletes remove tiles, renames update them, and new PNGs appear automatically. Missing
  files are pruned from history at startup, off the UI thread.
- History thumbnails decode lazily at thumbnail resolution. The selected detail image decodes at
  source resolution for crisp high-DPI display and is released as soon as selection changes, so
  full-size bitmaps are never accumulated across the grid.
- Blur and pixelate use `LockBits` rather than `GetPixel`/`SetPixel`, which is orders of magnitude faster.
- Logs are JSON-lines under `%LOCALAPPDATA%\NexusShot\logs`, rotating at 1 MB. Image contents are never logged.
