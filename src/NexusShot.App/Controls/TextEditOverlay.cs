using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NexusShot.App.Editor;
using Windows.Foundation;
using Windows.UI;

namespace NexusShot.App.Controls;

/// <summary>
/// Inline text editor: a <see cref="TextBox"/> plus its eight resize grips in one element, so the
/// grips sit exactly on the box the user sees. Positioned in image-pixel coordinates. The overlay's
/// own size is the single source of truth — resizing manipulates it directly, and the host reads
/// the final bounds — which avoids the drift a separately-rendered box and grips suffered.
/// </summary>
public sealed partial class TextEditOverlay : Grid
{
    private static readonly Color Accent = Color.FromArgb(255, 10, 132, 255);
    private const double GripSize = 12;
    private const double MinBoxWidth = 24;
    private const double MinBoxHeight = 28;

    private readonly Canvas _gripLayer = new() { IsHitTestVisible = true };
    private readonly List<(ResizeHandle Handle, Rectangle Hit)> _grips = [];

    private double _maxWidth;
    private double _maxHeight;

    // Active resize gesture state, all in image pixels within the parent canvas.
    private ResizeHandle _activeHandle;
    private Point _dragStart;
    private Rect _originBounds;
    private bool _resizing;
    private Point? _pendingResizePoint;
    private bool _resizeFramePending;

    public TextEditOverlay(double adornerScale)
    {
        AdornerScale = adornerScale;
        Box = new TextBox
        {
            PlaceholderText = "Text",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Background = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Accent),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 6, 4),
            // Caret colour is theme-derived and otherwise unsettable; dark => white caret.
            RequestedTheme = ElementTheme.Dark,
        };

        // Pin focus-state brushes so the default template does not flash a light field.
        Box.Resources["TextControlBackgroundFocused"] = Box.Background;
        Box.Resources["TextControlBackgroundPointerOver"] = Box.Background;
        Box.Resources["TextControlBorderBrushFocused"] = Box.BorderBrush;
        Box.Resources["TextControlBorderBrushPointerOver"] = Box.BorderBrush;

        Children.Add(Box);
        Children.Add(_gripLayer);
        BuildGrips();

        // Grips follow the box's measured size.
        Box.SizeChanged += (_, _) => LayoutGrips();
        SizeChanged += (_, _) => LayoutGrips();
    }

    public TextBox Box { get; }

    /// <summary>Inverse display scale, so grips keep a constant on-screen size at any zoom.</summary>
    public double AdornerScale { get; }

    /// <summary>Raised when a grip drag finishes, so the host can commit the new bounds.</summary>
    public event EventHandler? ResizeCompleted;

    /// <summary>Raised continuously during a grip drag, so the host can update live status.</summary>
    public event EventHandler? Resizing;

    /// <summary>The overlay's current bounds within the parent canvas, in image pixels.</summary>
    public Rect Bounds => new(Canvas.GetLeft(this), Canvas.GetTop(this), ActualWidth, ActualHeight);

    /// <summary>Positions and sizes the overlay from an annotation's box; the box fills it.</summary>
    public void SetBounds(Rect bounds, double canvasWidth, double canvasHeight)
    {
        _maxWidth = canvasWidth;
        _maxHeight = canvasHeight;

        var width = Math.Max(MinBoxWidth, bounds.Width);
        var height = Math.Max(MinBoxHeight, bounds.Height);
        var x = Math.Clamp(bounds.X, 0, Math.Max(0, canvasWidth - width));
        var y = Math.Clamp(bounds.Y, 0, Math.Max(0, canvasHeight - height));

        Canvas.SetLeft(this, x);
        Canvas.SetTop(this, y);
        Width = width;
        // Grows with content, capped at the canvas bottom.
        Box.MinHeight = height;
        MaxHeight = Math.Max(height, canvasHeight - y);
    }

    /// <summary>Applies the font/colour look shared with the rendered text and the export.</summary>
    public void ApplyFormat(double fontSize, bool bold, bool italic, Color color)
    {
        Box.FontSize = Math.Max(8, fontSize);
        Box.FontWeight = bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
        Box.FontStyle = italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
        var foreground = new SolidColorBrush(color);
        Box.Foreground = foreground;
        Box.Resources["TextControlForegroundFocused"] = foreground;
        Box.Resources["TextControlForegroundPointerOver"] = foreground;
    }

    /// <summary>One transparent hit target per handle; the visible L-corner and edge-bar strokes
    /// are rebuilt each layout pass. The hit rect is what the pointer grabs.</summary>
    private void BuildGrips()
    {
        foreach (var handle in new[]
        {
            ResizeHandle.TopLeft, ResizeHandle.Top, ResizeHandle.TopRight,
            ResizeHandle.Left, ResizeHandle.Right,
            ResizeHandle.BottomLeft, ResizeHandle.Bottom, ResizeHandle.BottomRight,
        })
        {
            var hit = new Rectangle { Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            hit.PointerPressed += (_, e) => OnGripPressed(handle, e);
            hit.PointerMoved += OnGripMoved;
            hit.PointerReleased += OnGripReleased;
            _gripLayer.Children.Add(hit);
            _grips.Add((handle, hit));
        }
    }

    /// <summary>Draws the same L-corner and edge-bar grips as the crop frame and shape selection,
    /// exactly on the box border, plus a transparent hit rect over each handle point.</summary>
    private void LayoutGrips()
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Rebuild the visible strokes; the persistent hit rects only get repositioned.
        for (var i = _gripLayer.Children.Count - 1; i >= 0; i--)
            if (_gripLayer.Children[i] is Polyline) _gripLayer.Children.RemoveAt(i);

        var rect = new Rect(0, 0, w, h);
        var thickness = 4 * AdornerScale;
        var underlayPad = 1.5 * AdornerScale;
        var arm = Math.Min(18 * AdornerScale, Math.Min(w, h) / 3);
        var bar = Math.Min(26 * AdornerScale, Math.Min(w, h) / 3);
        var hitSize = GripSize * 1.6 * AdornerScale;

        foreach (var (handle, hit) in _grips)
        {
            // inset 0: the grip centrelines sit exactly on the box border, straddling it.
            var points = LiveAnnotationRenderer.BoxGripPath(handle, rect, arm, bar, 0);
            if (points is not null)
            {
                _gripLayer.Children.Add(LiveAnnotationRenderer.CreateGripStroke(points, Color.FromArgb(170, 0, 0, 0), thickness + underlayPad * 2));
                _gripLayer.Children.Add(LiveAnnotationRenderer.CreateGripStroke(points, Microsoft.UI.Colors.White, thickness));
            }

            var (px, py) = HandlePoint(handle, w, h);
            hit.Width = hitSize;
            hit.Height = hitSize;
            Canvas.SetLeft(hit, px - hitSize / 2);
            Canvas.SetTop(hit, py - hitSize / 2);
        }
    }

    private static (double X, double Y) HandlePoint(ResizeHandle handle, double w, double h) => handle switch
    {
        ResizeHandle.TopLeft => (0, 0),
        ResizeHandle.Top => (w / 2, 0),
        ResizeHandle.TopRight => (w, 0),
        ResizeHandle.Left => (0, h / 2),
        ResizeHandle.Right => (w, h / 2),
        ResizeHandle.BottomLeft => (0, h),
        ResizeHandle.Bottom => (w / 2, h),
        _ => (w, h),
    };

    private void OnGripPressed(ResizeHandle handle, PointerRoutedEventArgs e)
    {
        _activeHandle = handle;
        _originBounds = Bounds;
        _dragStart = e.GetCurrentPoint(Parent as UIElement).Position;
        _resizing = true;
        ((UIElement)e.OriginalSource).CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnGripMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizing) return;
        _pendingResizePoint = e.GetCurrentPoint(Parent as UIElement).Position;
        if (!_resizeFramePending)
        {
            _resizeFramePending = true;
            CompositionTarget.Rendering += ApplyPendingResize;
        }
        e.Handled = true;
    }

    private void ApplyPendingResize(object? sender, object e)
    {
        CompositionTarget.Rendering -= ApplyPendingResize;
        _resizeFramePending = false;
        if (!_resizing || _pendingResizePoint is not { } current) return;
        _pendingResizePoint = null;
        var dx = current.X - _dragStart.X;
        var dy = current.Y - _dragStart.Y;

        var o = _originBounds;
        var (left, top, right, bottom) = _activeHandle switch
        {
            ResizeHandle.TopLeft => (o.Left + dx, o.Top + dy, o.Right, o.Bottom),
            ResizeHandle.Top => (o.Left, o.Top + dy, o.Right, o.Bottom),
            ResizeHandle.TopRight => (o.Left, o.Top + dy, o.Right + dx, o.Bottom),
            ResizeHandle.Left => (o.Left + dx, o.Top, o.Right, o.Bottom),
            ResizeHandle.Right => (o.Left, o.Top, o.Right + dx, o.Bottom),
            ResizeHandle.BottomLeft => (o.Left + dx, o.Top, o.Right, o.Bottom + dy),
            ResizeHandle.Bottom => (o.Left, o.Top, o.Right, o.Bottom + dy),
            _ => (o.Left, o.Top, o.Right + dx, o.Bottom + dy),
        };

        // Clamp to the canvas with a minimum size, so the box never inverts or escapes the image.
        left = Math.Clamp(left, 0, right - MinBoxWidth);
        top = Math.Clamp(top, 0, bottom - MinBoxHeight);
        right = Math.Clamp(right, left + MinBoxWidth, _maxWidth);
        bottom = Math.Clamp(bottom, top + MinBoxHeight, _maxHeight);

        Canvas.SetLeft(this, left);
        Canvas.SetTop(this, top);
        Width = right - left;
        Box.MinHeight = bottom - top;
        MaxHeight = Math.Max(bottom - top, _maxHeight - top);

        Resizing?.Invoke(this, EventArgs.Empty);
    }

    private void OnGripReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizing) return;

        if (_resizeFramePending)
        {
            CompositionTarget.Rendering -= ApplyPendingResize;
            _resizeFramePending = false;
        }
        _pendingResizePoint = e.GetCurrentPoint(Parent as UIElement).Position;
        ApplyPendingResize(this, EventArgs.Empty);
        _resizing = false;
        ((UIElement)e.OriginalSource).ReleasePointerCapture(e.Pointer);
        ResizeCompleted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}
