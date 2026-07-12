<div align="center">

<img src="assets/icons/nexus-shot-256.png" alt="NexusShot" width="128" height="128" />

# NexusShot

**A native Windows screenshot utility, modelled on CleanShot X.**

<p>
  <img src="https://img.shields.io/github/v/release/Ved-Padmawar/NexusShot?label=version&color=success" alt="Version" />
  <img src="https://img.shields.io/badge/C%23-239120?logo=csharp&logoColor=white" alt="C#" />
  <img src="https://img.shields.io/badge/-.NET%2010-512BD4?logo=dotnet&logoColor=white" alt=".NET 10" />
  <img src="https://img.shields.io/badge/Win32%20%2B%20Direct2D-0078D6?logo=windows&logoColor=white" alt="Win32 + Direct2D" />
  <img src="https://img.shields.io/badge/Native%20AOT-5C2D91?logo=dotnet&logoColor=white" alt="Native AOT" />
  <img src="https://img.shields.io/badge/Windows%2010%2F11-0078D6?logo=windows11&logoColor=white" alt="Windows 10/11" />
</p>

[**⬇ Download**](../../releases/latest) · [Features](#-features) · [Build](#-build) · [Architecture](#-architecture)

**One 7.6 MB executable. No runtime, no framework payload, nothing else to install.**

</div>

---

## ✨ Features

**📸 Capture** — full virtual desktop, active window, and drag-selection region, with correct
multi-monitor and per-monitor-DPI coordinates.

**🎨 Editor** — rectangle, ellipse, line, arrow, pen, brush, eraser, text, highlight, blur,
pixelate, counter, spotlight, and crop. Annotations are objects, not pixels: they stay editable
until you save. Blur and pixelate run on the GPU. Text is edited in place.

**⌨️ Global hotkeys** — `Ctrl+Shift+S` region · `Ctrl+Shift+F` full screen · `Ctrl+Shift+W` active
window · `Ctrl+Shift+N` open the shell. A binding another app already owns fails on its own; the
rest still register.

### Editor shortcuts

`V` select · `R` rectangle · `O` ellipse · `L` line · `A` arrow · `P` pen · `B` brush · `E` eraser ·
`T` text · `H` highlight · `U` blur · `X` pixelate · `N` counter · `S` spotlight · `C` crop ·
`Ctrl+Z` / `Ctrl+Y` undo / redo · `1` toggle fit / 100%

---

## 🚀 Build

```powershell
.\build.ps1                      # debug build, then run
.\build.ps1 test                 # headless render + drag-timing check
.\build.ps1 release              # Native AOT single exe -> dist\NexusShot.exe
.\build.ps1 installer            # release + Inno Setup -> dist\NexusShot-<version>.exe
```

One project, one output directory, one command per thing you might want.

**Requirements:** .NET 10 SDK, and the Visual Studio C++ build tools (Native AOT compiles to machine
code and links with MSVC). For `installer`, also [Inno Setup 6](https://jrsoftware.org/isdl.php).

> Screenshots are saved to `Pictures\NexusShot`. Settings and history live in `%APPDATA%\NexusShot`;
> a corrupt file falls back to defaults rather than refusing to start.

---

## 🏛 Architecture

```text
src/NexusShot/
  Core/       Framework-free editing logic and types
              EditorDocument   state, gestures, undo/redo, selection, crop
              Annotation       one annotation, in image-pixel space
              BoxGeometry      shared crop/shape/text interaction geometry
              AdornerGeometry  the exact geometry of selection and crop adorners
              Theme, Palette   design tokens as values, not resource dictionaries
  Render/     Direct2D
              AnnotationRenderer  draws a document onto any D2D target
              Ui, ToolIcons       immediate-mode widgets, vector icons
              PixelEffectSource   blur and pixelate as GPU effects
              Exporter            the same renderer, pointed at an offscreen target
              ImageSurface        WIC decode + GPU upload
  Platform/   Win32: capture, tray, hotkeys, clipboard, the inline text EDIT
  Views/      MainWindow, EditorWindow, RegionOverlay
  App.cs      tray + hotkeys + capture pipeline
```

The app is **immediate mode**: there is no retained visual tree. Input mutates the document and
asks for a repaint; a frame is one allocation-free pass over the annotation list. `WM_PAINT` is
already coalesced to the display rate, so a burst of pointer messages collapses into one frame on
its own.

Measured on a 120-frame drag with 9 annotations live (including GPU blur and pixelate):
**median 1.2 ms per frame**, against a 16.7 ms budget.

<details>
<summary><b>Why this is not WinUI 3 any more</b></summary>

<br />

The previous build was WinUI 3, and its lag was structural rather than incidental. Every pointer
move mutated a retained visual tree: find an annotation's elements, patch them, let layout re-run —
work proportional to the scene, on the UI thread, per input event. The old renderer fought that with
hand-rolled frame batching (buffer the samples, hook `CompositionTarget.Rendering`, flush once a
frame) and still lagged when a drag started and stopped abruptly. Two releases went into the
symptoms.

Immediate mode removes the thing that was slow instead of working around it. Several other problems
turned out to be the same problem wearing different clothes:

| | WinUI 3 | now |
| --- | --- | --- |
| **Erasing** | A XAML `Polyline` cannot have holes, so each stroke was rasterised into a `WriteableBitmap` and the erased pixels cleared in a software loop. | Stroke geometry *minus* the widened eraser path. One geometry, filled by the GPU. |
| **Blur / pixelate** | C# per-pixel loops on the UI thread, producing a `WriteableBitmap` per stroke per frame. | `ID2D1Effect`. |
| **Export** | A separate GDI+ flattener, kept in agreement with the on-screen renderer by hand. | The same renderer, pointed at an offscreen target. They cannot drift. |
| **Blurry preview** | A XAML `Image` scales whatever bitmap it is given: either a pre-scaled thumbnail (soft) or the full image in the visual tree (heavy). | One bitmap per capture, uploaded at full resolution, rescaled by the GPU each frame. |
| **Cursor lag** | Chased through `ProtectedCursor`. | `WM_SETCURSOR` + `SetCursor`: Windows draws it. |
| **Payload** | 117 MB (Windows App SDK, self-contained). | **7.6 MB**, single exe. |
| **RAM idle** | ~140 MB | **~58 MB** |

`EditorDocument`, `BoxGeometry`, `Annotation` and the adorner geometry ported over essentially
unchanged. They never depended on the framework — only on `Windows.Foundation`'s `Point`/`Rect`,
which `Core/Geometry.cs` now supplies.

</details>

<details>
<summary><b>Two things that will bite you</b></summary>

<br />

**Direct2D refuses to use resources from one factory with a target from another** — *"Objects used
together must be created from the same factory instance."* Stroke styles and path geometries are
*factory* resources, so they must come from whichever factory owns the target being drawn into.
`D2DResources` takes its factory from its target, and everything that builds geometry goes through
it. A process-wide factory looks tidy and fails at runtime.

**The render target defaults to the system DPI.** On a scaled display D2D then scales every
coordinate — while `ClientRect`, `WM_MOUSEMOVE` and the image are all already in physical pixels.
The target is pinned to 96 DPI and the chrome scales itself. That is also what makes "100%" mean one
image pixel to one *physical* pixel, which is the rule that keeps a screenshot pin-sharp.

Also: the app manifest needs the ComCtl32 v6 dependency. Without it an unhandled exception dies
inside its own `TaskDialog` and reports nothing at all.

</details>

<details>
<summary><b>Immediate-mode UI, and the one place it does not apply</b></summary>

<br />

A widget is a function call, not an object: `if (ui.Button(id, bounds, "Save")) { ... }`. The widget
owns no state, so there is nothing to keep in sync with the model, and the flags the XAML build
needed to suppress re-entrancy (`_isLoadingThickness`, `_isLoadingTextFormat`) have nothing to
guard. Icons are vector paths — no icon font, no PNG assets, no `.pri` to forget to publish.

The exception is **text entry**, which is hosted in a real Win32 `EDIT` child window. Hand-rolling a
text box means hand-rolling the caret, selection, shift-arrow, word select, `Ctrl+A`, clipboard,
in-box undo, and IME composition for anyone typing a language that needs one. All of that already
exists and is already correct. The control is created over the annotation's box, styled through
`WM_CTLCOLOREDIT` to paint on the image's own colour, and destroyed when the edit ends.

</details>

<details>
<summary><b>The region overlay freezes the screen</b></summary>

<br />

It draws a snapshot taken *before* the window appears, rather than being transparent over the live
desktop. A live overlay has to fight the compositor and can catch its own dimming in the capture.
The snapshot means what you select is exactly what you get.

Because the manifest declares `PerMonitorV2`, `GetSystemMetrics(SM_*VIRTUALSCREEN)` returns physical
pixels, so a virtual desktop spanning monitors with different scale factors needs no correction.

</details>
