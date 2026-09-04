using NexusShot.Core;

namespace NexusShot.Render;

/// <summary>
/// A combo box: a closed field that states the current value, and a list that opens over whatever is
/// below it.
///
/// The list has to paint after every row that might sit under it, so the caller draws the field
/// during layout and calls <see cref="DrawOpen"/> once at the end of the frame. A popup drawn in
/// place would be painted over by the next row down.
/// </summary>
public sealed class Dropdown
{
    /// <summary>The field whose list is open, or 0. Only one at a time, like a real combo box.</summary>
    private int _openId;
    private Rect _anchor;
    private string[] _options = [];
    private int _selected;
    private Action<int>? _commit;

    public bool IsOpen => _openId != 0;

    /// <summary>The closed field. Click toggles its list open; the list itself is drawn later.</summary>
    public void Field(Ui ui, int id, Rect bounds, string[] options, int selected, Action<int> set)
    {
        var open = _openId == id;

        // The list swallows clicks that land on it, so a press there must not also toggle the field.
        if (!open && ui.Interact(id, bounds)) Open(id, bounds, options, selected, set);
        else if (open && ui.Interact(id, bounds)) _openId = 0;

        ui.FillRounded(bounds, (float)(Metrics.RadiusControl * ui.Scale),
            open || ui.IsHot(id) ? ui.Theme.FillHover : ui.Theme.SurfaceOverlay);
        ui.StrokeRounded(bounds, (float)(Metrics.RadiusControl * ui.Scale),
            open ? ui.Theme.Accent : ui.Theme.StrokeDefault);

        var padding = 11 * ui.Scale;
        ui.Text(options[Math.Clamp(selected, 0, options.Length - 1)],
            new Rect(bounds.X + padding, bounds.Y, bounds.Width - padding - 24 * ui.Scale, bounds.Height),
            ui.Theme.TextPrimary, (float)(Metrics.FontBody * ui.Scale));

        ui.Icon(Icons.ChevronDown,
            new Rect(bounds.Right - 22 * ui.Scale, bounds.Y, 14 * ui.Scale, bounds.Height),
            ui.Theme.TextTertiary, 8 * ui.Scale);
    }

    private void Open(int id, Rect anchor, string[] options, int selected, Action<int> set)
    {
        _openId = id;
        _anchor = anchor;
        _options = options;
        _selected = selected;
        _commit = set;
    }

    public void Close() => _openId = 0;

    /// <summary>The open list. Called once, last in the frame, so it paints over everything. A list
    /// that would run past <paramref name="within"/>'s bottom opens upward instead.</summary>
    public void DrawOpen(Ui ui, Rect within)
    {
        if (_openId == 0) return;

        var scale = ui.Scale;
        var rowHeight = 32 * scale;
        var gap = 4 * scale;
        var radius = 6 * scale;

        // Rows run edge to edge: an inset leaves the highlight floating inside the surface with a
        // sliver of background showing around it, which reads as a misdrawn row rather than a
        // selection. The list's own corner radius is what keeps the ends from being square.
        var height = _options.Length * rowHeight;

        var below = _anchor.Bottom + gap;
        var above = _anchor.Y - gap - height;

        // Downward unless that overflows and there is room above. Clamped so an over-tall list pins.
        var top = below + height <= within.Bottom || above < within.Y ? below : above;
        top = Math.Clamp(top, within.Y, Math.Max(within.Y, within.Bottom - height));

        var list = new Rect(_anchor.X, top, _anchor.Width, height);

        // Clicking away closes without choosing, and the click must not fall through to a row below.
        if (ui.PointerPressed && !list.Contains(ui.Pointer) && !_anchor.Contains(ui.Pointer))
        {
            _openId = 0;
            return;
        }

        ui.FillRounded(list, (float)radius, ui.Theme.SurfaceOverlay);

        for (var i = 0; i < _options.Length; i++)
        {
            var row = new Rect(list.X, list.Y + i * rowHeight, list.Width, rowHeight);

            var id = Ui.Id(_openId, i);
            if (ui.Interact(id, row))
            {
                var commit = _commit;
                var chosen = i;
                _openId = 0;
                if (chosen != _selected) commit?.Invoke(chosen);
                return;
            }

            // Clipped to the list, so the end rows pick up its corners and the ones between stay
            // square: a full-width fill would otherwise square off the rounded ends.
            var fill = i == _selected ? ui.Theme.Accent : ui.IsHot(id) ? ui.Theme.FillHover : default;
            if (fill.A > 0) ui.FillRowInRounded(row, list, (float)radius, fill);

            ui.Text(_options[i],
                new Rect(row.X + 12 * scale, row.Y, row.Width - 12 * scale, row.Height),
                i == _selected ? ui.Theme.TextOnAccent : ui.Theme.TextPrimary,
                (float)(Metrics.FontBody * scale));
        }
    }
}
