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

    /// <summary>The open list. Called once, last in the frame, so it paints over everything.</summary>
    public void DrawOpen(Ui ui)
    {
        if (_openId == 0) return;

        var scale = ui.Scale;
        var rowHeight = 32 * scale;
        var padding = 4 * scale;

        var height = _options.Length * rowHeight + padding * 2;
        var list = new Rect(_anchor.X, _anchor.Bottom + 4 * scale, _anchor.Width, height);

        // Clicking away closes without choosing, and the click must not fall through to a row below.
        if (ui.PointerPressed && !list.Contains(ui.Pointer) && !_anchor.Contains(ui.Pointer))
        {
            _openId = 0;
            return;
        }

        ui.FillRounded(list, (float)(6 * scale), ui.Theme.SurfaceOverlay);
        ui.StrokeRounded(list, (float)(6 * scale), ui.Theme.StrokeDefault);

        for (var i = 0; i < _options.Length; i++)
        {
            var row = new Rect(list.X + padding, list.Y + padding + i * rowHeight,
                list.Width - padding * 2, rowHeight);

            var id = _openId * 100 + i + 1;
            if (ui.Interact(id, row))
            {
                var commit = _commit;
                var chosen = i;
                _openId = 0;
                if (chosen != _selected) commit?.Invoke(chosen);
                return;
            }

            if (i == _selected) ui.FillRounded(row, (float)(4 * scale), ui.Theme.Accent);
            else if (ui.IsHot(id)) ui.FillRounded(row, (float)(4 * scale), ui.Theme.FillHover);

            ui.Text(_options[i],
                new Rect(row.X + 8 * scale, row.Y, row.Width - 8 * scale, row.Height),
                i == _selected ? ui.Theme.TextOnAccent : ui.Theme.TextPrimary,
                (float)(Metrics.FontBody * scale));
        }
    }
}
