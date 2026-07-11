using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace NexusShot.App.Controls;

/// <summary>
/// A single colour dot in the editor palette.
///
/// This replaces the <c>RadioButton</c> the palette used to be built from. A RadioButton's template
/// always lays out a 20px indicator column ahead of its content and enforces a 32px minimum height,
/// so a 24px swatch holding an 18px ellipse had nowhere to put the dot and clipped it.
/// </summary>
public sealed class ColorSwatch : Control
{
    public static readonly DependencyProperty SwatchBrushProperty = DependencyProperty.Register(
        nameof(SwatchBrush), typeof(Brush), typeof(ColorSwatch), new PropertyMetadata(null));

    public static readonly DependencyProperty ColorHexProperty = DependencyProperty.Register(
        nameof(ColorHex), typeof(string), typeof(ColorSwatch), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(ColorSwatch),
        new PropertyMetadata(false, (d, _) => ((ColorSwatch)d).UpdateVisualState()));

    private bool _isPointerOver;
    private bool _isPressed;

    public ColorSwatch()
    {
        DefaultStyleKey = typeof(ColorSwatch);
        IsTabStop = true;
        UseSystemFocusVisuals = true;
    }

    public Brush? SwatchBrush
    {
        get => (Brush?)GetValue(SwatchBrushProperty);
        set => SetValue(SwatchBrushProperty, value);
    }

    /// <summary>The colour this swatch applies, as <c>#RRGGBB</c>.</summary>
    public string ColorHex
    {
        get => (string)GetValue(ColorHexProperty);
        set => SetValue(ColorHexProperty, value);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    /// <summary>Raised on click. The owner decides selection; the swatch never selects itself.</summary>
    public event EventHandler? Invoked;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateVisualState(useTransitions: false);
    }

    protected override void OnPointerEntered(PointerRoutedEventArgs e)
    {
        base.OnPointerEntered(e);
        _isPointerOver = true;
        UpdateVisualState();
    }

    protected override void OnPointerExited(PointerRoutedEventArgs e)
    {
        base.OnPointerExited(e);
        _isPointerOver = false;
        _isPressed = false;
        UpdateVisualState();
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
        _isPressed = true;
        CapturePointer(e.Pointer);
        UpdateVisualState();
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);
        ReleasePointerCapture(e.Pointer);

        var wasPressed = _isPressed;
        _isPressed = false;
        UpdateVisualState();
        if (wasPressed && _isPointerOver) Invoked?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (Windows.System.VirtualKey.Space or Windows.System.VirtualKey.Enter)) return;
        Invoked?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void UpdateVisualState(bool useTransitions = true)
    {
        var state = IsSelected ? "Selected"
            : _isPressed ? "Pressed"
            : _isPointerOver ? "PointerOver"
            : "Normal";
        VisualStateManager.GoToState(this, state, useTransitions);
    }
}
