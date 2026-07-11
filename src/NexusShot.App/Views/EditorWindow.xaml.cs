using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NexusShot.App.Controls;
using NexusShot.App.Editor;
using NexusShot.App.Enums;
using NexusShot.App.Helpers;
using NexusShot.App.Models;
using NexusShot.App.Services;
using Windows.Foundation;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NexusShot.App.Views;

/// <summary>
/// Markup editor. The view maps pointer input into image-pixel space and defers all state to
/// <see cref="EditorDocument"/>; exported pixels come from the flattener, never from the canvas.
/// </summary>
public sealed partial class EditorWindow : Window
{
    private static readonly string[] Palette =
        ["#FF3B30", "#FFCC00", "#34C759", "#0A84FF", "#FFFFFF", "#1C1C1E"];

    private static readonly int[] FontSizes = [12, 14, 16, 20, 24, 28, 32, 40, 48, 64];

    private readonly EditorDocument _document = new();
    private readonly AppServices _services;
    private readonly IntPtr _handle;

    /// <summary>The capture being edited. Save As switches this to the newly written file.</summary>
    private ScreenshotHistoryItem _item;

    private int _imageWidth;
    private int _imageHeight;
    private bool _isLoaded;
    private bool _isAdjustingThickness;
    private bool _isLoadingTextFormat;
    private bool _isLoadingThickness;
    private bool _isLoadingColorPicker;
    private Point? _pendingGesturePoint;
    private bool _gestureFramePending;
    private Point? _brushCursorPoint;
    private readonly DispatcherTimer _saveToastTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    /// <summary>Decoded source pixels; feeds the real blur/pixelate previews once loaded.</summary>
    private EditorPixelSource? _pixelSource;

    private Annotation? _editingText;

    /// <summary>Overlay that hosts the inline TextBox and its own resize grips as one unit, so
    /// the grips always sit exactly on the box the user sees rather than on separately-rendered
    /// annotation visuals.</summary>
    private TextEditOverlay? _textOverlay;

    public EditorWindow(ScreenshotHistoryItem item, AppServices services)
    {
        InitializeComponent();
        _item = item;
        _services = services;
        _handle = WindowNative.GetWindowHandle(this);

        var appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_handle));
        WindowSizing.ResizeDips(appWindow, _handle, 1180, 820);
        AppIcon.Apply(appWindow, _handle);

        // Same chrome as the main window: content extends into the titlebar, leaving only the
        // caption buttons. The slim strip above the toolbar is the drag region and shows the
        // file name, so the toolbar's own buttons stay clear of the caption cluster.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        TitleBarText.Text = Path.GetFileName(item.FilePath);
        _saveToastTimer.Tick += (_, _) =>
        {
            _saveToastTimer.Stop();
            SaveToast.Visibility = Visibility.Collapsed;
        };
    }

    /// <summary>Activates and forces foreground: Activate() alone can leave a freshly created
    /// window behind the window that spawned it.</summary>
    public void ActivateToFront()
    {
        Activate();
        Native.NativeMethods.SetForegroundWindow(_handle);

        WireTools();
        BuildPalette();
        foreach (var size in FontSizes) FontSizeBox.Items.Add(size);
        SelectTool(EditorTool.Rectangle);

        _document.Changed += (_, _) => Redraw();
        _document.ActiveAnnotationChanged += (_, annotation) =>
            LiveAnnotationRenderer.UpdateActive(AnnotationCanvas, _document, annotation, _pixelSource, AdornerScale());
        _document.PendingCropChanged += (_, _) =>
            LiveAnnotationRenderer.UpdateCropAdorner(AnnotationCanvas, _document, AdornerScale());
        Root.KeyDown += Root_KeyDown;

        // Re-fit when the window resizes so the image always fills the available viewport, and
        // redraw so adorners keep their constant on-screen size at the new display scale.
        CanvasScroller.SizeChanged += (_, _) =>
        {
            SizeCanvasToFit();
            if (_isLoaded) Redraw();
        };

        _ = LoadImageAsync();
    }

    private async Task LoadImageAsync()
    {
        try
        {
            var bitmap = await ImageLoader.LoadAsync(_item.FilePath);
            _imageWidth = bitmap.PixelWidth;
            _imageHeight = bitmap.PixelHeight;
            _document.SetImageSize(_imageWidth, _imageHeight);

            BaseImage.Source = bitmap;
            SizeCanvasToFit();
            _isLoaded = true;
            UpdateStatus();

            // Decode the raw pixels off the UI thread; blur/pixelate previews show a frosted
            // placeholder until this lands, then a redraw swaps in the real effect.
            _pixelSource = await Task.Run(() => EditorPixelSource.Load(_item.FilePath));
            Redraw();
        }
        catch (Exception exception)
        {
            _services.Logger.Error("editor.load_failed", exception, new { file = Path.GetFileName(_item.FilePath) });
            StatusText.Text = "Could not open this screenshot.";
        }
    }

    /// <summary>
    /// Scales the image down to fit the viewport while preserving aspect ratio. The canvas is
    /// always sized in image pixels internally, so a RenderTransform does the display scaling.
    /// </summary>
    private void SizeCanvasToFit()
    {
        if (_imageWidth == 0 || _imageHeight == 0) return;

        ImageHost.Width = _imageWidth;
        ImageHost.Height = _imageHeight;
        BaseImage.Width = _imageWidth;
        BaseImage.Height = _imageHeight;
        AnnotationCanvas.Width = _imageWidth;
        AnnotationCanvas.Height = _imageHeight;

        var scale = DisplayScale();
        ImageHost.RenderTransform = new ScaleTransform { ScaleX = scale, ScaleY = scale };
        CanvasFrame.Width = _imageWidth * scale;
        CanvasFrame.Height = _imageHeight * scale;
    }

    /// <summary>Uniform fit scale, never magnifying beyond 1:1.</summary>
    private double DisplayScale()
    {
        var availableWidth = Math.Max(200, CanvasScroller.ViewportWidth - 64);
        var availableHeight = Math.Max(200, CanvasScroller.ViewportHeight - 64);
        if (_imageWidth == 0 || _imageHeight == 0) return 1;
        return Math.Min(1, Math.Min(availableWidth / _imageWidth, availableHeight / _imageHeight));
    }

    /// <summary>Maps a pointer position on the scaled host back into image-pixel coordinates.</summary>
    private Point ToImagePoint(PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(ImageHost).Position;
        return new Point(
            Math.Clamp(position.X, 0, _imageWidth),
            Math.Clamp(position.Y, 0, _imageHeight));
    }

    /// <summary>
    /// Builds the colour palette. <see cref="ColorSwatch"/> owns mutual exclusion here rather than
    /// through a RadioButton GroupName, because a RadioButton reserves a 20px indicator column
    /// ahead of its content and clipped the dot out of the swatch.
    /// </summary>
    private void BuildPalette()
    {
        foreach (var hex in Palette)
        {
            var swatch = new ColorSwatch
            {
                ColorHex = hex,
                SwatchBrush = new SolidColorBrush(ParseColor(hex)),
                IsSelected = hex == _document.ColorHex,
            };
            ToolTipService.SetToolTip(swatch, hex);
            swatch.Invoked += (sender, _) => SelectColor(((ColorSwatch)sender!).ColorHex);
            ColorPanel.Children.Add(swatch);
        }
    }

    private void SelectColor(string hex)
    {
        _document.SetColor(hex);
        var color = ParseColor(hex);
        CurrentColorPreview.Fill = new SolidColorBrush(color);
        _isLoadingColorPicker = true;
        CustomColorPicker.Color = color;
        _isLoadingColorPicker = false;
        foreach (var swatch in ColorPanel.Children.OfType<ColorSwatch>())
            swatch.IsSelected = swatch.ColorHex == hex;
        if (_brushCursorPoint is { } point) UpdateBrushCursor(point);

        // The palette is also the text colour control: recolour the live inline editor too.
        ApplyFormatToEditingBox();
    }

    /// <summary>Wires every tile's Invoked to tool selection, so the XAML carries no click handlers.</summary>
    private void WireTools()
    {
        foreach (var tile in ToolPanel.Children.OfType<ToolTile>())
            tile.Invoked += (sender, _) => SelectTool(Enum.Parse<EditorTool>(((ToolTile)sender!).Tool));
    }

    private void Redraw()
    {
        LiveAnnotationRenderer.Render(AnnotationCanvas, _document, _pixelSource, AdornerScale());

        // While the overlay is open it *is* the annotation: keep the rendered text and grips
        // suppressed so nothing shows doubled behind the box.
        if (_editingText is not null) SetAnnotationEditing(_editingText, true);

        UndoButton.IsEnabled = _document.CanUndo;
        RedoButton.IsEnabled = _document.CanRedo;
        UpdateTextFormatPanel();
        UpdateStatus();
    }

    /// <summary>Inverse display scale: adorners drawn in image pixels keep on-screen size.</summary>
    private double AdornerScale() => 1 / DisplayScale();

    private void UpdateStatus()
    {
        if (!_isLoaded) return;
        var crop = (_document.PendingCrop ?? _document.CropBounds) is { } c
            ? $"  •  Crop {(int)c.Width}×{(int)c.Height} — Save to apply, Esc to cancel"
            : string.Empty;
        StatusText.Text = $"{_imageWidth}×{_imageHeight}  •  {_document.Annotations.Count} annotation(s){crop}";
    }

    /// <summary>Applies the tool and keeps the tiles mutually exclusive. Entering the crop tool
    /// opens a crop session; leaving it without saving discards the pending frame.</summary>
    private void SelectTool(EditorTool tool)
    {
        if (tool != EditorTool.Crop) _document.CancelCropSession();
        _document.ActiveTool = tool;
        if (tool == EditorTool.Crop && _isLoaded) _document.BeginCropSession();

        _isLoadingThickness = true;
        ThicknessSlider.Maximum = tool is EditorTool.Brush or EditorTool.Eraser ? 300 : 20;
        ThicknessSlider.Value = Math.Min(ThicknessSlider.Maximum, _document.ActiveThickness);
        _isLoadingThickness = false;
        HideBrushCursor();

        var name = tool.ToString();
        foreach (var tile in ToolPanel.Children.OfType<ToolTile>())
            tile.IsSelected = tile.Tool == name;
        UpdateTextFormatPanel();
    }

    // ============================  TEXT FORMATTING  ============================

    /// <summary>The annotation the formatting controls act on; null means they set defaults.</summary>
    private Annotation? TextFormatTarget() =>
        _editingText ?? (_document.Selected is { Tool: EditorTool.Text } selected ? selected : null);

    /// <summary>Shows the formatting cluster when text is in play and mirrors the target's values.</summary>
    private void UpdateTextFormatPanel()
    {
        var target = TextFormatTarget();
        TextFormatPanel.Visibility = target is not null || _document.ActiveTool == EditorTool.Text
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (TextFormatPanel.Visibility == Visibility.Collapsed) return;

        _isLoadingTextFormat = true;
        var fontSize = (int)Math.Round(target?.FontSize ?? _document.TextFontSize);
        FontSizeBox.SelectedItem = FontSizes.Contains(fontSize)
            ? fontSize
            : FontSizes.OrderBy(size => Math.Abs(size - fontSize)).First();
        BoldToggle.IsChecked = target?.IsBold ?? _document.TextBold;
        ItalicToggle.IsChecked = target?.IsItalic ?? _document.TextItalic;
        UnderlineToggle.IsChecked = target?.IsUnderline ?? _document.TextUnderline;
        _isLoadingTextFormat = false;
    }

    private void FontSize_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingTextFormat || FontSizeBox.SelectedItem is not int size) return;
        _document.SetTextFormat(d => d.TextFontSize = size, a => a.FontSize = size);
        ApplyFormatToEditingBox();
    }

    private void TextFormat_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingTextFormat) return;
        var bold = BoldToggle.IsChecked == true;
        var italic = ItalicToggle.IsChecked == true;
        var underline = UnderlineToggle.IsChecked == true;
        _document.SetTextFormat(
            d => { d.TextBold = bold; d.TextItalic = italic; d.TextUnderline = underline; },
            a => { a.IsBold = bold; a.IsItalic = italic; a.IsUnderline = underline; });
        ApplyFormatToEditingBox();
    }

    /// <summary>Pushes formatting into the live inline editor. (TextBox cannot render underline;
    /// it appears once the edit commits.)</summary>
    private void ApplyFormatToEditingBox()
    {
        if (_textOverlay is not { } overlay || _editingText is not { } annotation) return;
        overlay.ApplyFormat(annotation.FontSize, annotation.IsBold, annotation.IsItalic, ParseColor(annotation.ColorHex));
    }

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_isLoaded) return;

        // Clicking the canvas while typing commits the text first, like every canvas editor.
        CommitTextEdit();

        var point = ToImagePoint(e);

        // With the text tool, clicking an existing text annotation re-opens it for editing
        // instead of stacking a new empty one on top — unless the click grabs a resize handle.
        if (_document.ActiveTool == EditorTool.Text
            && !(_document.Selected is { } selected && _document.GetResizeHandleAt(selected, point, HandleTolerance()) is not null)
            && _document.Annotations.LastOrDefault(a => a.Tool == EditorTool.Text && a.HitTest(point)) is { } existingText)
        {
            _document.SelectAnnotation(existingText);
            BeginTextEdit(existingText);
            return;
        }

        ImageHost.CapturePointer(e.Pointer);
        _document.BeginGesture(point, HandleTolerance());
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isLoaded) return;
        var point = ToImagePoint(e);
        UpdateBrushCursor(point);
        if (e.GetCurrentPoint(ImageHost).Properties.IsLeftButtonPressed)
        {
            _pendingGesturePoint = point;
            if (!_gestureFramePending)
            {
                _gestureFramePending = true;
                CompositionTarget.Rendering += ApplyPendingGesture;
            }
            return;
        }
        if (_document.ActiveTool is EditorTool.Pen or EditorTool.Brush or EditorTool.Eraser) return;
        UpdateHoverCursor(point);
    }

    private void Canvas_PointerExited(object sender, PointerRoutedEventArgs e) => HideBrushCursor();

    private void UpdateBrushCursor(Point point)
    {
        if (_document.ActiveTool == EditorTool.Pen)
        {
            _brushCursorPoint = point;
            ImageHost.SetPencilCursor();
            return;
        }

        if (_document.ActiveTool is not (EditorTool.Brush or EditorTool.Eraser))
        {
            HideBrushCursor();
            return;
        }

        _brushCursorPoint = point;
        var diameter = PaintStrokeGeometry.Diameter(_document.ActiveThickness);
        var left = point.X - diameter / 2;
        var top = point.Y - diameter / 2;
        var inverseScale = AdornerScale();

        foreach (var outline in new[] { BrushCursorOuter, BrushCursorInner })
        {
            outline.Width = diameter;
            outline.Height = diameter;
            Canvas.SetLeft(outline, left);
            Canvas.SetTop(outline, top);
            outline.Visibility = Visibility.Visible;
        }

        // Keep the outline legible over both light and dark pixels without changing its diameter.
        BrushCursorOuter.StrokeThickness = 3 * inverseScale;
        BrushCursorInner.StrokeThickness = _document.ActiveTool == EditorTool.Brush
            ? inverseScale
            : 0;
        var cursorColor = ParseColor(_document.ColorHex);
        BrushCursorInner.Fill = _document.ActiveTool == EditorTool.Brush
            ? new SolidColorBrush(cursorColor)
            : null;
        ImageHost.SetHiddenCursor();
    }

    private void HideBrushCursor()
    {
        _brushCursorPoint = null;
        BrushCursorOuter.Visibility = Visibility.Collapsed;
        BrushCursorInner.Visibility = Visibility.Collapsed;
        ImageHost.SetCursorShape(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
    }

    private void ApplyPendingGesture(object? sender, object e)
    {
        CompositionTarget.Rendering -= ApplyPendingGesture;
        _gestureFramePending = false;
        if (_pendingGesturePoint is not { } point) return;
        _pendingGesturePoint = null;
        _document.ContinueGesture(point);
    }

    /// <summary>Cursor feedback while hovering: resize arrows over handles (selection or crop
    /// frame), a move cursor over grabbable regions, an I-beam for the text tool, a crosshair
    /// for drawing tools.</summary>
    private void UpdateHoverCursor(Point point)
    {
        Microsoft.UI.Input.InputSystemCursorShape shape;

        if (_document.IsCropSessionActive)
        {
            if (_document.GetCropHandleAt(point, HandleTolerance()) is { } cropHandle)
                shape = CursorForHandle(cropHandle);
            else if (_document.PendingCrop is { } crop && crop.Contains(point))
                shape = Microsoft.UI.Input.InputSystemCursorShape.SizeAll;
            else
                shape = Microsoft.UI.Input.InputSystemCursorShape.Arrow;
        }
        else if (_document.Selected is { } selected
            && _document.GetResizeHandleAt(selected, point, HandleTolerance()) is { } handle)
        {
            shape = CursorForHandle(handle);
        }
        else if (_document.ActiveTool == EditorTool.Select)
        {
            shape = _document.Annotations.Any(a => a.HitTest(point))
                ? Microsoft.UI.Input.InputSystemCursorShape.SizeAll
                : Microsoft.UI.Input.InputSystemCursorShape.Arrow;
        }
        else if (_document.ActiveTool == EditorTool.Text)
        {
            shape = Microsoft.UI.Input.InputSystemCursorShape.IBeam;
        }
        else
        {
            shape = Microsoft.UI.Input.InputSystemCursorShape.Cross;
        }

        ImageHost.SetCursorShape(shape);
    }

    private static Microsoft.UI.Input.InputSystemCursorShape CursorForHandle(ResizeHandle handle) => handle switch
    {
        ResizeHandle.TopLeft or ResizeHandle.BottomRight => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast,
        ResizeHandle.TopRight or ResizeHandle.BottomLeft => Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest,
        ResizeHandle.Top or ResizeHandle.Bottom => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth,
        ResizeHandle.Left or ResizeHandle.Right => Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast,
        _ => Microsoft.UI.Input.InputSystemCursorShape.SizeAll, // Line endpoints.
    };

    /// <summary>Handle grab radius in image pixels, compensating for the display scale.</summary>
    private double HandleTolerance() => 10 / DisplayScale();

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isLoaded) return;
        ImageHost.ReleasePointerCapture(e.Pointer);

        if (_gestureFramePending)
        {
            CompositionTarget.Rendering -= ApplyPendingGesture;
            _gestureFramePending = false;
        }
        _pendingGesturePoint = null;
        var point = ToImagePoint(e);
        _document.ContinueGesture(point);

        // Typing starts only for a newly drawn text annotation; a resize/move of an existing
        // one ends like any other gesture.
        var beginTyping = _document.ActiveTool == EditorTool.Text && _document.IsDrawGestureActive;
        _document.EndGesture(point);

        if (beginTyping && _document.Selected is { Tool: EditorTool.Text } placed)
            BeginTextEdit(placed);
    }

    /// <summary>Double-clicking an existing text annotation re-opens it for editing in place.</summary>
    private void Canvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (!_isLoaded) return;
        var position = e.GetPosition(ImageHost);
        var point = new Point(
            Math.Clamp(position.X, 0, _imageWidth),
            Math.Clamp(position.Y, 0, _imageHeight));

        var hit = _document.Annotations.LastOrDefault(a => a.Tool == EditorTool.Text && a.HitTest(point));
        if (hit is null) return;
        _document.SelectAnnotation(hit);
        BeginTextEdit(hit);
    }

    /// <summary>
    /// Opens the inline editor (TextBox + its own grips) over the annotation's rectangle, in image
    /// coordinates so it scales with the canvas. The overlay owns the box and grips as one unit,
    /// so the grips stay exactly on the visible border and resizing never drifts. The annotation's
    /// separately-rendered visuals are suppressed while editing. Enter/Shift+Enter insert lines;
    /// clicking away commits; Escape cancels.
    /// </summary>
    private void BeginTextEdit(Annotation annotation)
    {
        CommitTextEdit();

        var overlay = new TextEditOverlay(AdornerScale());
        overlay.Box.Text = annotation.Text;
        overlay.ApplyFormat(annotation.FontSize, annotation.IsBold, annotation.IsItalic, ParseColor(annotation.ColorHex));
        overlay.SetBounds(annotation.Bounds, _imageWidth, _imageHeight);

        overlay.Box.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Escape) { CancelTextEdit(); args.Handled = true; }
        };
        overlay.Box.LostFocus += (_, args) =>
        {
            // Focus moving to a grip within the same overlay is a resize, not a commit.
            if (FocusManager.GetFocusedElement(Content.XamlRoot) is DependencyObject focused
                && IsWithin(focused, overlay)) return;
            CommitTextEdit();
        };
        overlay.Resizing += (_, _) => UpdateStatus();
        overlay.ResizeCompleted += (_, _) => overlay.Box.Focus(FocusState.Programmatic);

        _textOverlay = overlay;
        _editingText = annotation;
        SetAnnotationEditing(annotation, true); // The overlay is the annotation while typing.
        TextEditCanvas.Children.Add(overlay);
        overlay.Box.Focus(FocusState.Programmatic);
        overlay.Box.SelectionStart = overlay.Box.Text.Length; // Caret at the end; clicks reposition it.
        UpdateTextFormatPanel();
    }

    private static bool IsWithin(DependencyObject? node, DependencyObject ancestor)
    {
        for (; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, ancestor)) return true;
        return false;
    }

    /// <summary>While an annotation is edited inline, its rendered text and selection grips are
    /// removed from the annotation canvas so only the overlay is visible; a later redraw or the
    /// cleared flag restores them.</summary>
    private void SetAnnotationEditing(Annotation annotation, bool isEditing)
    {
        if (isEditing)
        {
            SetAnnotationVisualsVisible(annotation, false);
            LiveAnnotationRenderer.RemoveSelectionAdorner(AnnotationCanvas);
        }
        else
        {
            SetAnnotationVisualsVisible(annotation, true);
        }
    }

    /// <summary>Hides or restores an annotation's rendered visuals, so the inline editor never
    /// shows the text doubled. A commit/cancel redraw rebuilds the canvas and restores state.</summary>
    private void SetAnnotationVisualsVisible(Annotation annotation, bool isVisible)
    {
        foreach (var child in AnnotationCanvas.Children.OfType<FrameworkElement>())
        {
            if (child.Tag is Guid id && id == annotation.Id)
                child.Opacity = isVisible ? 1 : 0;
        }
    }

    /// <summary>Applies the typed text. Committing empty removes the annotation: as if never
    /// placed when it was new, or as a normal undoable delete when it had text before.</summary>
    private void CommitTextEdit()
    {
        if (TakeTextEditor() is not { } editor) return;
        var (annotation, overlay, bounds) = editor;
        var text = overlay.Box.Text.Trim();

        if (text.Length == 0)
        {
            if (annotation.Text.Length == 0) _document.CancelAnnotation(annotation);
            else { _document.SelectAnnotation(annotation); _document.DeleteSelected(); }
            return;
        }

        // The overlay is the single source of truth for size: write its final bounds and text
        // back as one undo step, so what was on screen is exactly what renders/exports.
        _document.SetTextContent(annotation, text, bounds);
    }

    private void CancelTextEdit()
    {
        if (TakeTextEditor() is not { } editor) return;
        if (editor.Annotation.Text.Length == 0) _document.CancelAnnotation(editor.Annotation);
    }

    /// <summary>Detaches the active inline editor, clearing state before removal so the TextBox's
    /// LostFocus (raised by the removal itself) cannot re-enter the commit.</summary>
    private (Annotation Annotation, TextEditOverlay Overlay, Rect Bounds)? TakeTextEditor()
    {
        if (_textOverlay is null || _editingText is null) return null;

        // Detaching the overlay can invalidate its layout dimensions.
        var result = (_editingText, _textOverlay, _textOverlay.Bounds);
        _textOverlay = null;
        _editingText = null;
        TextEditCanvas.Children.Remove(result.Item2);

        // Restore the visuals hidden for the edit; a no-op commit raises no Changed/redraw.
        SetAnnotationVisualsVisible(result.Item1, true);
        return result;
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Keys typed into the inline text editor are content, not tool shortcuts.
        if (e.OriginalSource is TextBox) return;

        // A crop session claims Escape before anything else: the crop is abandoned, nothing
        // is applied. Saving is what commits a crop.
        if (_document.IsCropSessionActive && e.Key == Windows.System.VirtualKey.Escape)
        {
            SelectTool(EditorTool.Select); // Cancels the session on the way out.
            e.Handled = true;
            return;
        }

        var control = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (control)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.Z: _document.Undo(); break;
                case Windows.System.VirtualKey.Y: _document.Redo(); break;
                case Windows.System.VirtualKey.C: Copy_Click(sender, new RoutedEventArgs()); break;
                case Windows.System.VirtualKey.S when shift: SaveAs_Click(sender, new RoutedEventArgs()); break;
                case Windows.System.VirtualKey.S: Save_Click(sender, new RoutedEventArgs()); break;
                default: return;
            }
            e.Handled = true;
            return;
        }

        EditorTool? tool = e.Key switch
        {
            Windows.System.VirtualKey.V => EditorTool.Select,
            Windows.System.VirtualKey.R => EditorTool.Rectangle,
            Windows.System.VirtualKey.E => EditorTool.Ellipse,
            Windows.System.VirtualKey.A => EditorTool.Arrow,
            Windows.System.VirtualKey.L => EditorTool.Line,
            Windows.System.VirtualKey.D => EditorTool.Pen,
            Windows.System.VirtualKey.M => EditorTool.Brush,
            Windows.System.VirtualKey.X => EditorTool.Eraser,
            Windows.System.VirtualKey.T => EditorTool.Text,
            Windows.System.VirtualKey.H => EditorTool.Highlight,
            Windows.System.VirtualKey.B => EditorTool.Blur,
            Windows.System.VirtualKey.P => EditorTool.Pixelate,
            Windows.System.VirtualKey.N => EditorTool.Counter,
            Windows.System.VirtualKey.S => EditorTool.Spotlight,
            Windows.System.VirtualKey.C => EditorTool.Crop,
            _ => null,
        };

        if (tool is { } selected)
        {
            SelectTool(selected);
            e.Handled = true;
            return;
        }

        if (e.Key is Windows.System.VirtualKey.Delete or Windows.System.VirtualKey.Back)
        {
            _document.DeleteSelected();
            e.Handled = true;
        }
    }

    /// <summary>
    /// The slider raises ValueChanged on every tick of a drag. Push a single undo entry on the
    /// first tick and treat the rest as adjustments, so one drag is one undo step.
    /// </summary>
    private void Thickness_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isLoaded || _isLoadingThickness) return;
        _document.SetStrokeThickness(e.NewValue, isAdjusting: _isAdjustingThickness);
        if (_brushCursorPoint is { } point) UpdateBrushCursor(point);
        _isAdjustingThickness = true;
    }

    /// <summary>Ends the drag, so the next slider interaction starts a fresh undo step.</summary>
    private void Thickness_DragEnded(object sender, PointerRoutedEventArgs e) => _isAdjustingThickness = false;

    private void ColorFlyout_Opening(object sender, object e)
    {
        _isLoadingColorPicker = true;
        CustomColorPicker.Color = ParseColor(_document.ColorHex);
        _isLoadingColorPicker = false;
    }

    private void CustomColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_isLoadingColorPicker) return;
        var color = args.NewColor;
        SelectColor($"#{color.R:X2}{color.G:X2}{color.B:X2}");
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => _document.Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => _document.Redo();
    private void Delete_Click(object sender, RoutedEventArgs e) => _document.DeleteSelected();

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        var temporary = Path.Combine(Path.GetTempPath(), $"NexusShot_edit_{Guid.NewGuid():N}.png");
        try
        {
            await FlattenAsync(temporary);
            await _services.Clipboard.CopyImageAsync(temporary, CancellationToken.None);
            StatusText.Text = "Copied to clipboard.";
        }
        catch (Exception exception)
        {
            _services.Logger.Error("editor.copy_failed", exception);
            StatusText.Text = "Copy failed.";
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    /// <summary>Commits annotations and any active crop over the current file, then reloads it
    /// as the new baseline.</summary>
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        CommitTextEdit();

        var temporary = Path.Combine(Path.GetTempPath(), $"NexusShot_save_{Guid.NewGuid():N}.png");
        try
        {
            // Flatten to a temp file first so a failure mid-write cannot corrupt the original.
            await FlattenAsync(temporary);
            File.Copy(temporary, _item.FilePath, true);
        }
        catch (Exception exception)
        {
            _services.Logger.Error("editor.save_failed", exception);
            StatusText.Text = "Save failed.";
            return;
        }
        finally
        {
            TryDelete(temporary);
        }

        await ReloadSavedAsync($"Saved {Path.GetFileName(_item.FilePath)}");
        await _services.Previews.RefreshPreviewAsync(_item, CancellationToken.None);
        ShowSaveToast($"Saved {Path.GetFileName(_item.FilePath)}");
    }

    /// <summary>Commits the edits into a new file, leaves the original untouched, and switches
    /// this editor to the newly written file.</summary>
    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        CommitTextEdit();

        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add("PNG image", [".png"]);
        picker.SuggestedFileName = $"{Path.GetFileNameWithoutExtension(_item.FilePath)}_edited";
        InitializeWithWindow.Initialize(picker, _handle);

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try
        {
            await FlattenAsync(file.Path);
        }
        catch (Exception exception)
        {
            _services.Logger.Error("editor.export_failed", exception);
            StatusText.Text = "Save failed.";
            return;
        }

        var original = _item;
        original.EditedFilePath = file.Path;
        _services.NotifyScreenshotUpdated(original);

        _item = new ScreenshotHistoryItem
        {
            FilePath = file.Path,
            CaptureMode = original.CaptureMode,
        };
        await ReloadSavedAsync($"Saved to {file.Path}");
        await _services.Previews.ShowPreviewAsync(_item, CancellationToken.None);
        ShowSaveToast($"Saved as {Path.GetFileName(file.Path)}");
    }

    private void ShowSaveToast(string message)
    {
        SaveToastText.Text = message;
        SaveToast.Visibility = Visibility.Visible;
        _saveToastTimer.Stop();
        _saveToastTimer.Start();
    }

    /// <summary>Resets the document over the saved file, reloads the bitmap, refreshes the window
    /// naming and tells the library to re-decode its thumbnails.</summary>
    private async Task ReloadSavedAsync(string status)
    {
        _document.ResetAfterSave();
        SelectTool(EditorTool.Select);

        var fileName = Path.GetFileName(_item.FilePath);
        TitleBarText.Text = fileName;
        Title = $"NexusShot — {fileName}";

        _isLoaded = false;
        _pixelSource = null;
        await LoadImageAsync();

        _item.Width = _imageWidth;
        _item.Height = _imageHeight;
        _services.NotifyScreenshotUpdated(_item);
        StatusText.Text = status;
    }

    /// <summary>Flattens with the live crop frame when one is engaged.</summary>
    private Task FlattenAsync(string destination) => _services.Flattener.FlattenAsync(
        _item.FilePath,
        destination,
        _document.Annotations,
        _document.PendingCrop ?? _document.CropBounds,
        CancellationToken.None);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static Windows.UI.Color ParseColor(string hex)
    {
        var value = hex.TrimStart('#');
        var rgb = int.Parse(value, System.Globalization.NumberStyles.HexNumber);
        return Windows.UI.Color.FromArgb(255, (byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
    }
}
