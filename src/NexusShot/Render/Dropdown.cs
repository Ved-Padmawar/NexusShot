using NexusShot.Core;

namespace NexusShot.Render;

/// <summary>
/// A combo box: a closed field that states the current value, and a menu that opens over whatever is
/// below it.
///
/// The menu has to paint after every row that might sit under it, so the caller draws the field
/// during layout and calls <see cref="DrawOpen"/> once at the end of the frame. A popup drawn in
/// place would be painted over by the next row down.
/// </summary>
public sealed class Dropdown
{
    /// <summary>The field whose menu is open, or 0. Only one at a time, like a real combo box.</summary>
    private int _openId;
    private Rect _anchor;
    private IReadOnlyList<string> _options = [];
    private int _selected;
    private Action<int>? _commit;

    public bool IsOpen => _openId != 0;

    /// <summary>The field's width for its widest option, so the value never truncates and a set of
    /// dropdowns in one list line up.</summary>
    public static double Width(Ui ui, IReadOnlyList<string> options) =>
        Math.Max(ui.Scale * 150, options.Max(option => ui.MeasureText(option, ui.Scale * Metrics.FontMd, Weight.Medium))
            + ui.Scale * 44);

    /// <summary>The closed field. Click toggles its menu; the menu itself is drawn later.</summary>
    public void Field(Ui ui, int id, Rect bounds, IReadOnlyList<string> options, int selected, Action<int> set)
    {
        var open = _openId == id;

        if (ui.Interact(id, bounds))
        {
            if (open) _openId = 0;
            else
            {
                _openId = id;
                _anchor = bounds;
                _options = options;
                _selected = selected;
                _commit = set;
            }
        }

        var s = ui.Scale;
        var radius = (float)(Metrics.RadiusSm * s);
        ui.FillRounded(bounds, radius, open || ui.IsHot(id) ? ui.Theme.SurfaceHover : ui.Theme.SurfacePane);
        ui.StrokeRounded(bounds, radius, open || ui.IsHot(id) ? ui.Theme.StrokeStrong : ui.Theme.StrokeDefault);

        ui.TextFit(options[Math.Clamp(selected, 0, options.Count - 1)],
            new Rect(bounds.X + 12 * s, bounds.Y, bounds.Width - 40 * s, bounds.Height),
            ui.Theme.TextPrimary, Metrics.FontMd * s, Weight.Medium);

        ui.Icon(Icons.ChevronDown, new Rect(bounds.Right - 26 * s, bounds.Y, 16 * s, bounds.Height),
            ui.Theme.TextSecondary, 15 * s);
    }

    public void Close() => _openId = 0;

    /// <summary>The open menu. Called once, last in the frame, so it paints over everything. A menu
    /// that would run past <paramref name="within"/>'s bottom opens upward instead.</summary>
    public void DrawOpen(Ui ui, Rect within)
    {
        if (_openId == 0) return;

        var s = ui.Scale;
        var rowHeight = 30 * s;
        var padding = 4 * s;
        var gap = 6 * s;

        var height = _options.Count * rowHeight + padding * 2;
        var width = Math.Max(_anchor.Width,
            _options.Max(option => ui.MeasureText(option, Metrics.FontMd * s)) + 52 * s);

        var below = _anchor.Bottom + gap;
        var above = _anchor.Y - gap - height;
        var top = below + height <= within.Bottom || above < within.Y ? below : above;

        var menu = new Rect(_anchor.Right - width, top, width, height);

        // Clicking away closes without choosing, and the click must not fall through to a row below.
        if (ui.PointerPressed && !menu.Contains(ui.Pointer) && !_anchor.Contains(ui.Pointer))
        {
            _openId = 0;
            return;
        }

        var radius = (float)(Metrics.RadiusMd * s);
        ui.FloatShadow(menu, radius);
        ui.FillRounded(menu, radius, ui.Theme.SurfaceRaised);
        ui.StrokeRounded(menu, radius, ui.Theme.StrokeDefault);

        for (var i = 0; i < _options.Count; i++)
        {
            var row = new Rect(menu.X + padding, menu.Y + padding + i * rowHeight, menu.Width - padding * 2, rowHeight);

            var id = Ui.Id(_openId, i);
            if (ui.Interact(id, row))
            {
                var commit = _commit;
                var chosen = i;
                _openId = 0;
                if (chosen != _selected) commit?.Invoke(chosen);
                return;
            }

            if (ui.IsHot(id)) ui.FillRounded(row, (float)(Metrics.RadiusSm * s), ui.Theme.SurfaceHover);
            ui.Text(_options[i], new Rect(row.X + 10 * s, row.Y, row.Width - 30 * s, row.Height),
                ui.Theme.TextPrimary, Metrics.FontMd * s);

            // A dot, not a filled row: a filled row reads as the hovered one.
            if (i == _selected)
                ui.FillCircle(new Point(row.Right - 13 * s, row.Center.Y), (float)(3 * s), ui.Theme.Accent);
        }
    }
}
