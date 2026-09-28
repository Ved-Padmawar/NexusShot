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
        var rowHeight = 32 * s;
        var gap = 6 * s;

        var height = _options.Count * rowHeight;
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

        // Rows run edge to edge, clipped to the menu's rounded corners.
        int? chosen = null;
        ui.PushRoundedLayer(menu, radius);
        for (var i = 0; i < _options.Count; i++)
        {
            var row = new Rect(menu.X, menu.Y + i * rowHeight, menu.Width, rowHeight);
            var id = Ui.Id(_openId, i);
            if (ui.Interact(id, row)) chosen = i;

            if (ui.IsHot(id)) ui.FillRect(row, ui.Theme.SurfaceHover);
            ui.Text(_options[i], new Rect(row.X + 14 * s, row.Y, row.Width - 44 * s, row.Height),
                ui.Theme.TextPrimary, Metrics.FontMd * s);
            if (i == _selected)
                ui.Icon(Icons.Tick, new Rect(row.Right - 30 * s, row.Y, 20 * s, row.Height), ui.Theme.AccentText, 16 * s);
        }
        ui.PopLayer();
        ui.StrokeRounded(menu, radius, ui.Theme.StrokeDefault);

        if (chosen is not { } pick) return;
        var commit = _commit;
        _openId = 0;
        if (pick != _selected) commit?.Invoke(pick);
    }
}
