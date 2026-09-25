<div align="center">

<img src="assets/icons/nexus-shot-256.png" alt="NexusShot" width="128" height="128" />

# NexusShot

**A native Windows screenshot and annotation utility.**

<p>
  <img src="https://img.shields.io/github/v/release/Ved-Padmawar/NexusShot?label=version&color=success" alt="Version" />
  <img src="https://img.shields.io/badge/C%23-239120?logo=csharp&logoColor=white" alt="C#" />
  <img src="https://img.shields.io/badge/-.NET%2010-512BD4?logo=dotnet&logoColor=white" alt=".NET 10" />
  <img src="https://img.shields.io/badge/Win32%20%2B%20Direct2D-0078D6?logo=windows&logoColor=white" alt="Win32 + Direct2D" />
  <img src="https://img.shields.io/badge/Native%20AOT-5C2D91?logo=dotnet&logoColor=white" alt="Native AOT" />
  <img src="https://img.shields.io/badge/Windows%2010%2F11-0078D6?logo=windows11&logoColor=white" alt="Windows 10/11" />
</p>

[**⬇ Download**](../../releases/latest) · [Features](#-features) · [Build](#-build-the-installer) · [Architecture](#-architecture)

**One single executable. No runtime, no framework payload, nothing else to install.**

</div>

---

## ✨ Features

**📸 Capture** — full virtual desktop, active window, and drag-selection region, with correct
multi-monitor and per-monitor-DPI coordinates. The region picker draws a **frozen snapshot** of the
screen, dimmed, with a crosshair and a live pixel-dimension readout. The saved image is cropped from
that same snapshot rather than re-grabbed afterwards, so an open menu or dropdown survives into the
shot and what you select is exactly what you get. Captures are copied to the clipboard and saved
automatically; both can be turned off in Settings. A **timed capture** counts down first, so a menu
or tooltip can be opened for the shot.

**🔤 Capture text** — select a region and its text goes straight to the clipboard, using the OCR built
into Windows (offline). Cards and the editor can copy an image's text too.

**🃏 Quick Access cards** — after each capture a thumbnail card appears in the corner you choose
(bottom-left by default) and stacks from there. It never steals focus. Hovering shows Pin, Close, Edit and Save as in the corners and
Copy / Copy text in the centre; drag the card anywhere else into another app — as a file, or as its
path into a text field. Auto-dismiss is configurable and pauses while hovered, pinned or dragged.

**↩️ Restore closed cards** — `Ctrl+Shift+H` shows recently closed captures in a strip at the top of
the screen; click Restore to bring one back.

**🎨 Editor** — rectangle, ellipse, line, arrow, pen, brush, eraser, text, numbered counter,
highlight, blur, pixelate, spotlight, and crop. Annotations stay selectable and editable after they
are drawn: boxes have eight resize grips, lines and arrows have endpoint grips, and text is edited
in place, wrapping inside its box, with bold, italic and underline on any part of it. Crop is a handle-draggable frame applied on `Enter` and discarded
with `Esc`. Blur and pixelate run on the GPU. Shapes can be outlined, tinted or filled. The colour picker
works in HEX, RGB, HSL or HSB, with opacity, an eyedropper, and recent and saved colours. Zoom with
`Ctrl`+wheel. Closing with unsaved edits asks first.

**🗂 Library** — every capture in the save folder, in a grid grouped by day and kept in sync with
File Explorer, with search. Hover a capture to delete, show in folder, copy, share (Windows share
sheet) or open it in the editor; double-click to edit. **Select** (or `Ctrl`+click) picks several to
delete at once. Scrolling is GPU-composited and smooth on touchpads. Drop an image on it, or use
**Open with** in Explorer, to edit any PNG, JPEG or BMP. Settings live here too: capture format,
after-capture action, card corner, OCR language, theme and accent, hotkeys, and more. Errors and
results arrive as Windows notifications.

**🔄 Updates** — when GitHub has a new version, a button appears in the Library. One click downloads
it with the progress on the button and verifies its signature; NexusShot then asks once, saving or
discarding open edits, before it installs and restarts. Nothing restarts on its own.

**⌨️ Global hotkeys** — `Ctrl+Shift+S` region, `Ctrl+Shift+F` full screen, `Ctrl+Shift+W` active
window, `Ctrl+Shift+O` capture text, `Ctrl+Shift+H` restore closed cards, `Ctrl+Shift+N` open the
Library; timed capture is unbound by default. All can be rebound (including to a single key such as
`F9`) or unbound in Settings. A binding another app already owns fails on its own, the rest still
register, and the Library says which one clashed.

### Keyboard shortcuts

| Context | Shortcuts |
| --- | --- |
| **Library** | `Ctrl+,` settings · `Esc` closes the open prompt or list, then Settings, then selection mode, then the window |
| **Editor tools** | `V` select · `R` rectangle · `E` ellipse · `L` line · `A` arrow · `D` pen · `M` brush · `X` eraser · `T` text · `N` counter · `H` highlight · `B` blur · `P` pixelate · `S` spotlight · `C` crop |
| **Editor** | `Ctrl+S` save · `Ctrl+C` copy · `Ctrl+Z` / `Ctrl+Y` undo / redo · `Ctrl+B` / `I` / `U` text style · `Ctrl+0` 100% · `Ctrl+9` fit · `Del` delete selection · `Enter` apply crop · `Esc` cancel |
| **Hotkey recorder** | `Backspace` unbind · `Delete` restore default · `Esc` cancel |

---

## 🚀 Getting started

### Requirements

- **Windows 10 1809+** (Windows 11 recommended)
- **.NET 10 SDK**
- **Visual Studio C++ build tools** — Native AOT compiles to machine code and links with MSVC

### Run from source

```powershell
.\build.ps1                      # debug build, then run
.\build.ps1 test                 # unit tests, then a headless render + drag-timing check
.\build.ps1 release              # build the native executable
```

The app starts in the notification area. The Library's close button hides it; use **Exit** on the
tray menu to quit. A second launch raises the running instance instead of starting another one,
because only one process can own the global hotkeys; a file opened from Explorer is handed to it.

> Screenshots are saved to `Pictures\NexusShot`. Settings and history live in `%APPDATA%\NexusShot`,
> logs in `%LOCALAPPDATA%\NexusShot\logs`. A corrupt settings file falls back to defaults rather than
> stopping the app from starting.

---

## 📦 Build the installer

```powershell
.\build.ps1 release              # Native AOT single exe -> dist\NexusShot.exe
.\build.ps1 installer            # release + Inno Setup -> dist\NexusShot-<version>.exe
```

`release` publishes a single Native AOT executable (~15 MB) — no .NET runtime, no framework payload,
so the target machine needs nothing installed. `installer` wraps that in Inno Setup.

> **Prerequisite:** [Inno Setup 6](https://jrsoftware.org/isdl.php) — `winget install JRSoftware.InnoSetup`

---

## 🏛 Architecture

```text
src/NexusShot/
  Core/       Framework-free state and logic, unit-tested without a GPU
              EditorDocument   annotations, gestures, selection, undo/redo, crop
              TextRuns         bold / italic / underline over part of a text box
              QuickAccess      the card stack: open, pinned, countdown, recently closed
              CardLayout       one card size, button and pill positions
              BoxGeometry      shared crop/shape/text handles, hit testing, resize
              AdornerGeometry  the exact geometry of selection and crop adorners
              AppSettings      settings + history persistence
              Theme, Palette   design tokens and colours as values
              Updates          release reading and signature checks
  Platform/   Win32, COM and WinRT interop: capture, tray notifications, hotkeys, clipboard,
              drag-out, OCR, share sheet, file dialogs, folder watcher, single instance,
              touchpad (DirectManipulation), updater, background media queue
  Render/     Direct2D / DirectWrite
              AnnotationRenderer  draws a document onto any D2D target
              Exporter            the same renderer, pointed at an offscreen target
              Ui, Dropdown        immediate-mode widgets
              ColorPicker         HEX / RGB / HSL / HSB picker with eyedropper
              CompositionLayers   the Library's DirectComposition layers
              PixelEffectSource   blur and pixelate as GPU effects
  Views/      Windows and their message handling
              MainWindow       the Library: day-grouped grid, settings
              EditorWindow     canvas + EditorChrome (toolbar, footer)
              FloatingPreview  the quick-access card
              RestoreStrip     recently closed captures
              RegionOverlay    the frozen-snapshot region picker
              TextEditor       inline text drawn in Direct2D
  App.cs              tray + hotkeys + lifetime
  CapturePipeline.cs  everything after the pixels exist: saving, cards, editors, history
src/NexusShot.Tests/  unit tests for Core, plus export pixels, widgets, the editor toolbar and the
                      Library drawn offscreen and clicked where they draw
```

The UI is **immediate mode**: there is no retained visual tree. Input mutates the document and asks
for a repaint; a frame is one allocation-free pass over the annotation list, and `WM_PAINT`
coalesces a burst of pointer messages into a single repaint. The Library's grid is the exception:
it is drawn once into a DirectComposition layer that the compositor scrolls, so scrolling redraws
nothing. File work — encoding, clipboard, thumbnail decoding — runs off the UI thread.

<details>
<summary><b>NexusShot used to be built on WinUI 3 — why it was rewritten in raw Win32 + Direct2D</b></summary>

<br />

Earlier versions of NexusShot were a WinUI 3 (XAML) app. Its lag was structural rather than
incidental: every pointer move mutated a retained visual tree — find an annotation's elements,
patch them, let layout re-run — work proportional to the scene, on the UI thread, per input event.
Frame batching and other workarounds went into two releases and the editor still lagged.

The rewrite drops the framework entirely: raw Win32 windows, Direct2D drawing, and immediate-mode
rendering. That removed the thing that was slow rather than working around it, and several other
problems turned out to be the same problem:

| | WinUI 3 | Win32 + Direct2D |
| --- | --- | --- |
| **Erasing** | A XAML `Polyline` cannot have holes, so each stroke was rasterised into a bitmap and erased pixel by pixel in software. | Stroke geometry *minus* the eraser's, filled by the GPU. |
| **Blur / pixelate** | Per-pixel C# loops on the UI thread, every frame. | `ID2D1Effect` on the GPU. |
| **Export** | A separate GDI+ flattener, kept in agreement with the screen by hand. | The same renderer, pointed at an offscreen target — they cannot drift. |
| **Preview sharpness** | A XAML `Image` got either a soft pre-scaled thumbnail or a heavy full-size bitmap. | One full-resolution GPU bitmap, rescaled each frame. |
| **Cursor** | Chased through `ProtectedCursor`, and lagged. | `WM_SETCURSOR`: Windows draws it. |
| **Payload** | 117 MB (Windows App SDK, self-contained). | **~15 MB**, single exe. |
| **RAM idle** | ~140 MB | **~10 MB** |

The editing model — `EditorDocument`, `BoxGeometry`, `Annotation` and the adorner geometry — carried
over essentially unchanged, because it never depended on the framework.

</details>

<details>
<summary><b>Implementation notes</b></summary>

<br />

- **Custom titlebar.** Window content extends into the titlebar (`CaptionWindow`): `WM_NCCALCSIZE`
  claims the caption, `WM_NCHITTEST` hands back the drag region and resize edges, and a maximised
  window is inset by `SM_CYSIZEFRAME + SM_CXPADDEDBORDER`, which Windows oversizes it by.
- **96 DPI render targets.** Input, window rects and images are all in physical pixels, so the
  Direct2D target is pinned to 96 DPI and the chrome scales itself. That is also what makes "100%"
  one image pixel per physical pixel.
- **One factory per target.** Direct2D refuses to mix resources from different factories, so
  `D2DResources` builds stroke styles and geometry from the factory that owns its target.
- **Text is drawn, not a Win32 `EDIT`.** A child HWND over a Direct2D surface has no defined paint
  order and flickered. The trade-off: full IME composition and UI Automation are not implemented.
- **Clipboard.** Images go on as `PNG`, `CF_DIBV5` and `CF_DIB`, all by value — delay-rendered
  data would vanish when the copying window closed and never reach Clipboard History (`Win`+`V`).
- **Drag-out.** Cards drag the shell's own data object (every format Explorer offers) plus
  `CF_UNICODETEXT`, so a drop onto a text field pastes the path.
- **Idle memory.** Small windows render in software, the export device is released after use, and
  large buffers never go through the managed heap, so the app idles at ~10 MB in the tray. Opening
  the Library or editor loads the GPU driver, which Windows keeps loaded until the app exits.
- **Signed updates.** Each release installer is signed in CI; the app refuses any download whose
  signature does not match its built-in key.
- **Rebinding a hotkey** unregisters all bindings while the recorder is armed; otherwise the key
  being rebound fires its action and never reaches the recorder.
- **Icons.** `assets/icons/icon-source.svg` is the source of truth for the app icon;
  `export-icons.ps1` writes the `.ico`. UI icons are SVG paths drawn by Direct2D.

</details>
