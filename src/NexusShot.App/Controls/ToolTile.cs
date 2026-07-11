using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace NexusShot.App.Controls;

/// <summary>
/// A square icon target for the editor toolbar, selected by <see cref="Tool"/>.
///
/// This is not a <c>ToggleButton</c>. The stock toggle template paints its checked state with the
/// system accent brush and enforces a minimum size that a 32px tile cannot satisfy; overriding both
/// leaves a template whose only surviving part is the content presenter.
/// </summary>
public sealed class ToolTile : ContentControl
{
    public static readonly DependencyProperty ToolProperty = DependencyProperty.Register(
        nameof(Tool), typeof(string), typeof(ToolTile), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(ToolTile),
        new PropertyMetadata(false, (d, _) => ((ToolTile)d).UpdateVisualState()));

    private bool _isPointerOver;
    private bool _isPressed;

    public ToolTile()
    {
        DefaultStyleKey = typeof(ToolTile);
        IsTabStop = true;
        UseSystemFocusVisuals = true;
    }

    /// <summary>Identifies which <c>EditorTool</c> this tile activates. Parsed by the editor.</summary>
    public string Tool
    {
        get => (string)GetValue(ToolProperty);
        set => SetValue(ToolProperty, value);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    /// <summary>Raised on click. The owner decides selection; the tile never selects itself.</summary>
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

        // Only a press and release inside the tile is a click.
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
        // Selected outranks hover: a selected tool must stay legible while the pointer crosses it.
        var state = !IsEnabled ? "Disabled"
            : IsSelected ? "Selected"
            : _isPressed ? "Pressed"
            : _isPointerOver ? "PointerOver"
            : "Normal";
        VisualStateManager.GoToState(this, state, useTransitions);
    }
}
