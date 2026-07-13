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

[**⬇ Download**](../../releases/latest) · [Features](#-features) · [Build](#-build-the-installer) · [Architecture](#-architecture)

**One single executable. No runtime, no framework payload, nothing else to install.**

</div>

---

## ✨ Features

**📸 Capture** — full virtual desktop, active window, and drag-selection region, with correct
multi-monitor and per-monitor-DPI coordinates. The region picker draws a **frozen snapshot** of the
screen, dimmed, with a crosshair and a live pixel-dimension readout. A live overlay has to fight the
compositor and can catch its own dimming in the capture; the snapshot means what you select is
exactly what you get.

**🃏 Quick Access Overlay** — after each capture a borderless thumbnail card appears at the
**bottom-left** of the work area and stacks upward as more captures arrive. It never steals focus
(`WS_EX_NOACTIVATE`) and stays out of Alt-Tab and the taskbar. The card sizes itself to the
capture's aspect ratio, so only the image shows — no frame or backdrop edge. Hovering reveals
Copy, Save as, Edit, and Pin; the card can be dragged straight into another application, as a file
or as its path into a text field. Auto-dismiss is configurable, pauses while the pointer is over the
card or when pinned, and fades the card out to the left.

**🎨 Editor** — displays the screenshot at fit-to-window scale while keeping all annotation geometry
in image-pixel space. Click-and-drag sets an annotation's position and size in one gesture, and
each shape family gets its own selection model: boxes show eight resize grips, lines and arrows
show endpoint grips, brush strokes (pen/blur/pixelate) leave only their effect until explicitly
selected. Text annotations are editable objects — double-click (or click with the text tool) to
edit in place, with the box itself as the wrapping text area. Crop is an interactive session: a
handle-draggable frame with the outside dimmed, applied only on confirm (`Enter`) and discardable
(`Esc`). Blur and pixelate run on the GPU. The colour picker takes a hex value or R/G/B directly, so
a colour can be matched to a spec rather than dragged for by eye.

**⌨️ Global hotkeys** — `Ctrl+Shift+S` region, `Ctrl+Shift+F` full screen, `Ctrl+Shift+W` active
window, `Ctrl+Shift+N` open the shell. All four are rebindable in Settings. Registration is
best-effort: a binding another app already owns fails on its own, the rest still register, and the
shell says which one clashed.

### Keyboard shortcuts

| Context | Shortcuts |
| --- | --- |
| **Shell** | `Esc` closes the open list, then Settings, then the selected capture, then the window |
| **Editor tools** | `V` select · `R` rectangle · `E` ellipse · `L` line · `A` arrow · `D` pen · `M` brush · `X` eraser · `T` text · `N` counter · `H` highlight · `B` blur · `P` pixelate · `S` spotlight · `C` crop |
| **Editor** | `Ctrl+Z` / `Ctrl+Y` undo / redo · `Del` delete selection · `1` toggle fit / 100% · `Enter` apply crop · `Esc` cancel |

Save, Save as… and Copy live in the editor's footer.

---

## 🚀 Getting started

### Requirements

- **Windows 10 1809+** (Windows 11 recommended)
- **.NET 10 SDK**
- **Visual Studio C++ build tools** — Native AOT compiles to machine code and links with MSVC

### Run from source

```powershell
.\build.ps1                      # debug build, then run
.\build.ps1 test                 # headless render + drag-timing check
```

The app starts in the notification area. The shell's close button hides it; use **Quit** on the
tray menu to exit. A second launch raises the running instance rather than starting a rival — one
process owns the global hotkeys, and a second could register none of them.

> Screenshots are saved to `Pictures\NexusShot`. Settings and metadata-only history live in
> `%APPDATA%\NexusShot`; a corrupt file falls back to defaults rather than refusing to start.

---

## 📦 Build the installer

```powershell
.\build.ps1 release              # Native AOT single exe -> dist\NexusShot.exe
.\build.ps1 installer            # release + Inno Setup -> dist\NexusShot-<version>.exe
```

`release` publishes a single Native AOT executable — no .NET runtime, no framework payload, so the
target machine needs nothing installed. `installer` wraps that in Inno Setup.

> **Prerequisite:** [Inno Setup 6](https://jrsoftware.org/isdl.php) — `winget install JRSoftware.InnoSetup`

---

## 🏛 Architecture

```text
assets/icons/  icon-source.svg + export-icons.ps1 -> nexus-shot.ico
src/NexusShot/
  Core/       Framework-free editing logic and types
              EditorDocument   state, gestures, undo/redo, selection, crop
              Annotation       one annotation, in image-pixel space
              BoxGeometry      shared crop/shape/text interaction geometry
              AdornerGeometry  the exact geometry of selection and crop adorners
              Theme, Palette   design tokens as values, not resource dictionaries
  Render/     Direct2D
              AnnotationRenderer  draws a document onto any D2D target
              Ui, Icons           immediate-mode widgets, vector icons
              ColorPicker         hex / RGB picker with editable fields
              PixelEffectSource   blur and pixelate as GPU effects
              Exporter            the same renderer, pointed at an offscreen target
              ImageSurface        WIC decode + GPU upload
  Platform/   Win32: capture, tray, hotkeys, clipboard, drag-out, single instance,
              and the inline text EDIT
  Views/      CaptionWindow    content extended into the titlebar
              MainWindow       the shell: sidebar, detail pane, settings
              EditorWindow     canvas + EditorChrome (toolbar, footer)
              FloatingPreview  the quick-access card
              RegionOverlay    the frozen-snapshot region picker
  App.cs      tray + hotkeys + capture pipeline
```

The app is **immediate mode**: there is no retained visual tree. Input mutates the document and
asks for a repaint; a frame is one allocation-free pass over the annotation list. `WM_PAINT` is
already coalesced to the display rate, so a burst of pointer messages collapses into one frame on
its own.

Measured on a 120-frame drag with 9 annotations live (including GPU blur and pixelate):
**median 1.2 ms per frame**, against a 16.7 ms budget.

The sections below document the non-obvious design decisions. Expand any that interest you.

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
| **Payload** | 117 MB (Windows App SDK, self-contained). | **~8 MB**, single exe. |
| **RAM idle** | ~140 MB | **~58 MB** |

`EditorDocument`, `BoxGeometry`, `Annotation` and the adorner geometry ported over essentially
unchanged. They never depended on the framework — only on `Windows.Foundation`'s `Point`/`Rect`,
which `Core/Geometry.cs` now supplies.

</details>

<details>
<summary><b>The shell browses; the editor edits</b></summary>

<br />

`MainWindow` is a sidebar of captures plus a detail pane. Annotating opens `EditorWindow` as its
own window rather than docking it into that pane. A docked editor would surrender the sidebar's
width from the image on every edit, permanently, to a list the user has stopped looking at.

The shell opens with nothing selected. The detail pane is the only thing that decodes a capture at
full resolution, and doing that before the first frame is a window that visibly takes a beat to
appear.

</details>

<details>
<summary><b>The titlebar is the client area</b></summary>

<br />

`CaptionWindow` extends the app's content into the titlebar, which is what the XAML build got from
`ExtendsContentIntoTitleBar`. The caption is not a strip above the client area — it *is* the client
area, painted by the app, with only the system buttons floating on top. That is what lets the
shell's sidebar run unbroken from the top of the window to the bottom.

`WM_NCCALCSIZE` claims the caption's height while leaving the resize borders alone. `WM_NCHITTEST`
then hands back the parts the frame still owns: the drag region, and the eight resize edges. A
maximised window is inset by `SM_CYSIZEFRAME + SM_CXPADDEDBORDER` — Windows deliberately oversizes
it by exactly that, and ignoring it crops the top of the content.

`WM_ERASEBKGND` is claimed and not honoured. The window class brush is white, and letting Windows
paint it before the first Direct2D frame is what makes a window flash white as it opens.

</details>

<details>
<summary><b>Immediate mode, and the one place it does not apply</b></summary>

<br />

A widget is a function call, not an object: `if (ui.Button(id, bounds, "Save")) { ... }`. The widget
owns no state, so there is nothing to keep in sync with the model, and the flags the XAML build
needed to suppress re-entrancy (`_isLoadingThickness`, `_isLoadingTextFormat`) have nothing to
guard. Icons are vector paths — no icon font, no PNG assets, no `.pri` to forget to publish. The
sidebar's brand mark is drawn from the icon's own 960-unit grid rather than shipped as a bitmap.

The trap is **ordering**. `Interact()` is what sets a widget's hot/active state, so reading
`IsHot(id)` before calling it styles the widget from the *previous* frame — which, since
`BeginFrame` resets it, means no hover at all. Every widget calls `Interact` first and styles from
the result. The same trap applies to state a click changes: the frame that handled the click has
already drawn the old value, so it must invalidate to show the new one.

Clip state is the other sharp edge. Direct2D counts pushes and pops itself, and an unbalanced stack
— or a layer popped with the axis-aligned call — faults the device with `D2DERR_WRONG_STATE` and
takes the app down. `Ui` records what each clip was pushed as, pops with the matching call, and
unwinds anything a caller left open at the end of the frame.

The exception is **text entry**, which is hosted in a real Win32 `EDIT` child window. Hand-rolling a
text box means hand-rolling the caret, selection, shift-arrow, word select, `Ctrl+A`, clipboard,
in-box undo, and IME composition for anyone typing a language that needs one. All of that already
exists and is already correct. The control is created over the annotation's box, styled through
`WM_CTLCOLOREDIT` to paint on the image's own colour, and destroyed when the edit ends.

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
<summary><b>The clipboard, and why a copy can silently vanish</b></summary>

<br />

`ClipboardImage` publishes three formats, because no one of them is read by everything: the
registered `PNG` format carries real alpha and is what modern apps prefer, `CF_DIBV5` is the Win32
format that also carries alpha, and `CF_DIB` is what everything can still paste. The two DIBs are
flattened onto white, whose alpha is widely ignored.

Every format is placed **by value**. Delay-rendered clipboard data stays owned by the copying
process: the paste target has to come back and ask for it, which fails the moment the window that
copied goes away — and the shell's Clipboard History (`Win`+`V`) never records the entry at all.

`FileDrag` has the mirror-image problem. `CF_HDROP` is what Explorer, browsers and mail clients read
as "here is a file", but a text field cannot accept a file at all — so the path is also offered as
`CF_UNICODETEXT`. That is what makes dropping a card onto an address bar or a chat box paste the
path rather than do nothing.

</details>

<details>
<summary><b>Icons</b></summary>

<br />

`assets/icons/icon-source.svg` is the design source of truth, sharing the Nexus family's tile
geometry and 135° cyan/steel split. `export-icons.ps1` redraws the same geometry with
`System.Drawing` and writes a PNG-framed `.ico`. Keep the two in sync when the mark changes.

`ApplicationIcon` only brands the **exe file**, which is what Explorer shows. The **taskbar** reads
its icon from the *window*, and **Alt-Tab** reads the HWND's `ICON_BIG` — so `AppIcon.Apply` sends
`WM_SETICON` for both sizes, loading the `HICON` from the module's own resource table rather than a
filesystem path (which in an unpackaged app depends on the working directory). The shell suppresses
its own caption icon with `WS_EX_DLGMODALFRAME`, because the sidebar already carries the brand.

</details>

---

## 🛠 Developer notes

- **Nothing decodes on the first frame.** Inflating a PNG costs ~14 ms whether the result is a 4K
  bitmap or a 52×34 chip. `ImageSurface` splits into `DecodeScaled` (CPU, any thread) and `Upload`
  (GPU, must be the thread that owns the device); thumbnails decode on the thread pool and fill in as
  they arrive. First frame went from **104 ms to 27 ms**.
- History thumbnails decode at thumbnail resolution. The selected detail image decodes at source
  resolution for crisp high-DPI display and is released as soon as the selection changes, so
  full-size bitmaps never accumulate.
- A `FileSystemWatcher` on the save folder keeps the sidebar synchronized with File Explorer:
  external deletes remove rows, new PNGs appear automatically. The watcher fires on a thread-pool
  thread, so its work is posted to the UI thread rather than mutating the history under a frame that
  is drawing it.
- **A hotkey has to stand aside to be rebound.** A key registered with `RegisterHotKey` is delivered
  as `WM_HOTKEY` and never as a keystroke — so pressing the very key you are rebinding fires its
  action and the recorder never sees it. The bindings are unregistered while a row is armed.
- Capturing does **not** hide the shell, so NexusShot's own window can be captured.
- The editor's Save overwrites the capture and refreshes its quick-access card, creating one if it
  was already dismissed. Save as… writes a new file, points the editor at it, and gives it a card of
  its own.
- Logs are JSON-lines under `%LOCALAPPDATA%\NexusShot\logs`, rotating at 1 MB. Image contents are
  never logged.
