using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace NexusShot.App.Controls;

/// <summary>
/// A compact hex-first colour picker: saturation/value field, hue rail, live preview and an
/// editable <c>#RRGGBB</c> box.
///
/// The stock WinUI <c>ColorPicker</c> is not used because its template is fixed — the spectrum,
/// sliders and channel boxes cannot be rearranged or restyled into the app's token palette, and
/// its default footprint is roughly three times this one.
/// </summary>
public sealed class HexColorPicker : Control
{
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(Color), typeof(HexColorPicker),
        new PropertyMetadata(Colors.Red, (d, e) => ((HexColorPicker)d).OnColorChanged((Color)e.NewValue)));

    private Canvas? _spectrum;
    private Rectangle? _spectrumHue;
    private FrameworkElement? _spectrumThumb;
    private Canvas? _hueRail;
    private FrameworkElement? _hueThumb;
    private TextBox? _hexBox;
    private TextBox? _redBox;
    private TextBox? _greenBox;
    private TextBox? _blueBox;
    private Rectangle? _preview;

    // Kept alongside Color: pure black/white cannot express a hue in RGB, so HSV is the source of truth.
    private double _hue;
    private double _saturation = 1;
    private double _value = 1;

    // Writing Color re-enters OnColorChanged; rebuilding HSV from that RGB would discard the
    // hue/saturation the user holds — at value 0 every hue is black, snapping the thumb mid-drag.
    private bool _isPublishing;
    private bool _isTemplateReady;

    public HexColorPicker()
    {
        DefaultStyleKey = typeof(HexColorPicker);
    }

    /// <summary>The selected colour. Alpha is always opaque.</summary>
    public Color Color
    {
        get => (Color)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    /// <summary>Raised only for user-driven changes, never when <see cref="Color"/> is set by code.</summary>
    public event EventHandler<Color>? ColorPicked;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _spectrum = GetTemplateChild("PART_Spectrum") as Canvas;
        _spectrumHue = GetTemplateChild("PART_SpectrumHue") as Rectangle;
        _spectrumThumb = GetTemplateChild("PART_SpectrumThumb") as FrameworkElement;
        _hueRail = GetTemplateChild("PART_HueRail") as Canvas;
        _hueThumb = GetTemplateChild("PART_HueThumb") as FrameworkElement;
        _hexBox = GetTemplateChild("PART_HexBox") as TextBox;
        _redBox = GetTemplateChild("PART_RBox") as TextBox;
        _greenBox = GetTemplateChild("PART_GBox") as TextBox;
        _blueBox = GetTemplateChild("PART_BBox") as TextBox;
        _preview = GetTemplateChild("PART_Preview") as Rectangle;

        if (_spectrum is not null)
        {
            _spectrum.PointerPressed += Spectrum_PointerPressed;
            _spectrum.PointerMoved += Spectrum_PointerMoved;
            _spectrum.PointerReleased += Surface_PointerReleased;
            _spectrum.SizeChanged += (_, _) => SyncThumbs();
        }
        if (_hueRail is not null)
        {
            _hueRail.PointerPressed += Hue_PointerPressed;
            _hueRail.PointerMoved += Hue_PointerMoved;
            _hueRail.PointerReleased += Surface_PointerReleased;
            _hueRail.SizeChanged += (_, _) => SyncThumbs();
        }
        if (_hexBox is not null)
        {
            _hexBox.KeyDown += Hex_KeyDown;
            _hexBox.LostFocus += (_, _) => CommitHex();
        }
        foreach (var box in ChannelBoxes())
        {
            box.KeyDown += Channel_KeyDown;
            box.LostFocus += (_, _) => CommitChannels();
        }

        _isTemplateReady = true;
        AdoptHsvFrom(Color);
        SyncVisuals();
    }

    /// <summary>Applies an external colour. Setting <see cref="Color"/> never raises
    /// <see cref="ColorPicked"/>, so this cannot echo back as a user edit.</summary>
    public void SetColorSilently(Color color) => Color = color;

    private void OnColorChanged(Color color)
    {
        if (_isPublishing || !_isTemplateReady) return;
        AdoptHsvFrom(color);
        SyncVisuals();
    }

    /// <summary>Rebuilds the HSV working state from an RGB colour, preserving the hue the user was
    /// on when the new colour is a grey that carries no hue of its own.</summary>
    private void AdoptHsvFrom(Color color)
    {
        var (hue, saturation, value) = ToHsv(color);
        _hue = saturation <= 0 ? _hue : hue;
        _saturation = saturation;
        _value = value;
    }

    private void Spectrum_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _spectrum?.CapturePointer(e.Pointer);
        ApplySpectrum(e.GetCurrentPoint(_spectrum).Position);
    }

    private void Spectrum_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(_spectrum).Properties.IsLeftButtonPressed) return;
        ApplySpectrum(e.GetCurrentPoint(_spectrum).Position);
    }

    private void Hue_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _hueRail?.CapturePointer(e.Pointer);
        ApplyHue(e.GetCurrentPoint(_hueRail).Position);
    }

    private void Hue_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(_hueRail).Properties.IsLeftButtonPressed) return;
        ApplyHue(e.GetCurrentPoint(_hueRail).Position);
    }

    private void Surface_PointerReleased(object sender, PointerRoutedEventArgs e) =>
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);

    private void ApplySpectrum(Point position)
    {
        if (_spectrum is null || _spectrum.ActualWidth <= 0 || _spectrum.ActualHeight <= 0) return;
        _saturation = Math.Clamp(position.X / _spectrum.ActualWidth, 0, 1);
        _value = 1 - Math.Clamp(position.Y / _spectrum.ActualHeight, 0, 1);
        PublishFromHsv();
    }

    private void ApplyHue(Point position)
    {
        if (_hueRail is null || _hueRail.ActualWidth <= 0) return;
        _hue = Math.Clamp(position.X / _hueRail.ActualWidth, 0, 1) * 360;
        if (_hue >= 360) _hue = 0;
        PublishFromHsv();
    }

    private void Hex_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        CommitHex();
        e.Handled = true;
    }

    /// <summary>Parses the hex box, restoring the current colour's text when it does not parse.</summary>
    private void CommitHex()
    {
        if (_hexBox is null) return;
        if (TryParseHex(_hexBox.Text, out var color))
        {
            AdoptHsvFrom(color);
            PublishFromHsv();
            return;
        }
        _hexBox.Text = ToHex(Color);
    }

    private void Channel_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        CommitChannels();
        e.Handled = true;
    }

    /// <summary>Rebuilds the colour from the three channel boxes. An unparsable box keeps the
    /// channel it already had, so a half-typed value never blanks the colour.</summary>
    private void CommitChannels()
    {
        var current = Color;
        var color = Color.FromArgb(
            255,
            ParseChannel(_redBox, current.R),
            ParseChannel(_greenBox, current.G),
            ParseChannel(_blueBox, current.B));

        AdoptHsvFrom(color);
        PublishFromHsv();

        static byte ParseChannel(TextBox? box, byte fallback) =>
            byte.TryParse(box?.Text, out var value) ? value : fallback;
    }

    private IEnumerable<TextBox> ChannelBoxes()
    {
        if (_redBox is not null) yield return _redBox;
        if (_greenBox is not null) yield return _greenBox;
        if (_blueBox is not null) yield return _blueBox;
    }

    private void PublishFromHsv()
    {
        var color = FromHsv(_hue, _saturation, _value);
        _isPublishing = true;
        Color = color;
        _isPublishing = false;
        SyncVisuals();
        ColorPicked?.Invoke(this, color);
    }

    private void SyncVisuals()
    {
        if (!_isTemplateReady) return;
        var color = Color;
        if (_preview is not null) _preview.Fill = new SolidColorBrush(color);
        if (_spectrumHue is not null) _spectrumHue.Fill = new SolidColorBrush(FromHsv(_hue, 1, 1));

        // Never fight the caret while the user is mid-edit in that box.
        SetIfUnfocused(_hexBox, ToHex(color));
        SetIfUnfocused(_redBox, color.R.ToString());
        SetIfUnfocused(_greenBox, color.G.ToString());
        SetIfUnfocused(_blueBox, color.B.ToString());
        SyncThumbs();

        static void SetIfUnfocused(TextBox? box, string text)
        {
            if (box is not null && box.FocusState == FocusState.Unfocused) box.Text = text;
        }
    }

    private void SyncThumbs()
    {
        if (_spectrum is not null && _spectrumThumb is not null
            && _spectrum.ActualWidth > 0 && _spectrum.ActualHeight > 0)
        {
            Canvas.SetLeft(_spectrumThumb, _saturation * _spectrum.ActualWidth - _spectrumThumb.Width / 2);
            Canvas.SetTop(_spectrumThumb, (1 - _value) * _spectrum.ActualHeight - _spectrumThumb.Height / 2);
        }
        if (_hueRail is not null && _hueThumb is not null && _hueRail.ActualWidth > 0)
        {
            Canvas.SetLeft(_hueThumb, _hue / 360 * _hueRail.ActualWidth - _hueThumb.Width / 2);
            Canvas.SetTop(_hueThumb, (_hueRail.ActualHeight - _hueThumb.Height) / 2);
        }
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>Accepts <c>#RGB</c>, <c>#RRGGBB</c> and the same without the hash.</summary>
    public static bool TryParseHex(string? text, out Color color)
    {
        color = Colors.Black;
        var hex = text?.Trim().TrimStart('#');
        if (string.IsNullOrEmpty(hex)) return false;

        if (hex.Length == 3)
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        if (hex.Length != 6) return false;

        if (!byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
            || !byte.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            || !byte.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            return false;

        color = Color.FromArgb(255, r, g, b);
        return true;
    }

    private static (double Hue, double Saturation, double Value) ToHsv(Color color)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        double hue;
        if (delta == 0) hue = 0;
        else if (max == r) hue = 60 * (((g - b) / delta + 6) % 6);
        else if (max == g) hue = 60 * ((b - r) / delta + 2);
        else hue = 60 * ((r - g) / delta + 4);

        return (hue, max == 0 ? 0 : delta / max, max);
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - c;

        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };

        return Color.FromArgb(255,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
