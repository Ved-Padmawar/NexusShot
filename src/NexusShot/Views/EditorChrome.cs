using NexusShot.Core;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>
/// The editor's chrome: a titlebar strip, the toolbar, and a status footer.
///
/// Tools are grouped by what they do - select, shapes, freehand,
/// redaction, framing - with hairline separators, so fifteen tiles read as five short groups rather
/// than one undifferentiated row. Colour and thickness sit in the centre. Undo/redo/delete sit at
/// the right of the toolbar; Copy and Save live in the *footer*, next to the zoom controls, because
/// they act on the document rather than on the drawing.
///
/// There is no state here: each control reads the document when it draws, so it cannot be stale.
/// </summary>
public sealed class EditorChrome(Ui ui)
{
    /// <summary>The display scale. The render target is pinned at 96 DPI so a unit is a physical
    /// pixel; the chrome scales itself, which is what keeps "100%" meaning one image pixel to one
    /// physical pixel rather than one DIP.</summary>
    public static double Scale { get; set; } = 1;

    public static double TitleBarHeight => 36 * Scale;
    public static double ToolbarHeight => 52 * Scale;
    public static double ChromeTop => TitleBarHeight + ToolbarHeight;
    public static double FooterHeight => 44 * Scale;

    private static double TileSize => 32 * Scale;

    /// <summary>Tools, in toolbar order, with a null marking a group separator.</summary>
    private static readonly EditorTool?[] Groups =
    [
        EditorTool.Select,
        null,
        EditorTool.Rectangle, EditorTool.Ellipse, EditorTool.Arrow, EditorTool.Line,
        null,
        EditorTool.Pen, EditorTool.Brush, EditorTool.Eraser, EditorTool.Text, EditorTool.Counter,
        null,
        EditorTool.Highlight, EditorTool.Blur, EditorTool.Pixelate, EditorTool.Spotlight,
        null,
        EditorTool.Crop,
    ];

    /// <summary>What the user asked for this frame. The window applies it; the chrome reports
    /// intent and never mutates anything it does not own.</summary>
    public EditorTool? ToolPicked { get; private set; }
    public bool UndoPressed { get; private set; }
    public bool RedoPressed { get; private set; }
    public bool DeletePressed { get; private set; }
    public bool SavePressed { get; private set; }
    public bool SaveAsPressed { get; private set; }
    public bool CopyPressed { get; private set; }
    public bool? FitPicked { get; private set; }

    private static double S(double units) => units * Scale;

    private readonly ColorPicker _picker = new();
    private Rect _pickerAnchor;

    /// <summary>True while the picker is open, so the canvas ignores clicks that land on it.</summary>
    public bool PopupOpen => _picker.IsOpen;

    public void Draw(
        EditorDocument document, double width, double height, string title, bool fit, string? toast)
    {
        ToolPicked = null;
        UndoPressed = RedoPressed = DeletePressed = false;
        SavePressed = SaveAsPressed = CopyPressed = false;
        FitPicked = null;

        DrawTitleBar(width, title);
        DrawToolbar(document, width);
        DrawFooter(document, width, height, fit, toast);

        // Last, so it paints over the toolbar rather than under it.
        if (_picker.Draw(ui, _pickerAnchor, Scale) is { } colour)
            document.SetColor(colour.ToHex());
    }

    /// <summary>The titlebar strip: the window's drag region, with the file name as its title.</summary>
    private void DrawTitleBar(double width, string title)
    {
        var bar = new Rect(0, 0, width, TitleBarHeight);
        ui.FillRect(bar, ui.Theme.SurfaceRaised);
        ui.Text(title, bar, ui.Theme.TextTertiary, (float)S(Metrics.FontCaption), align: TextAlign.Center);
    }

    private void DrawToolbar(EditorDocument document, double width)
    {
        var top = TitleBarHeight;
        var bar = new Rect(0, top, width, ToolbarHeight);

        ui.FillRect(bar, ui.Theme.SurfaceRaised);
        ui.FillRect(new Rect(0, bar.Bottom - 1, width, 1), ui.Theme.StrokeSubtle);

        var tile = TileSize;
        var y = top + (ToolbarHeight - tile) / 2;
        var glyph = S(15);

        // ---- tools, left, in groups ----
        var x = S(12);
        foreach (var entry in Groups)
        {
            if (entry is not { } tool)
            {
                // A separator: a hairline the height of a tile's inner content.
                x += S(6);
                ui.Separator(x, y + S(7), tile - S(14));
                x += S(7);
                continue;
            }

            if (ui.Tile((int)tool + 100, new Rect(x, y, tile, tile),
                document.ActiveTool == tool, Glyph(tool), glyph, Label(tool)))
                ToolPicked = tool;

            x += tile + S(1);
        }

        // ---- actions, right ----
        var right = width - S(12);

        right -= tile;
        if (ui.Tile(9024, new Rect(right, y, tile, tile), false, Icons.Delete, S(14), "Delete  (Del)"))
            DeletePressed = true;

        right -= tile + S(2);
        if (ui.Tile(9023, new Rect(right, y, tile, tile), false, Icons.Redo, S(14), "Redo  (Ctrl+Y)")
            && document.CanRedo)
            RedoPressed = true;

        right -= tile + S(2);
        if (ui.Tile(9022, new Rect(right, y, tile, tile), false, Icons.Undo, S(14), "Undo  (Ctrl+Z)")
            && document.CanUndo)
            UndoPressed = true;

        // ---- colour and thickness, centred in what is left ----
        DrawColorAndThickness(document, x + S(14), right - S(14), y, tile);
    }

    /// <summary>
    /// The swatches, the live colour chip, and the thickness slider - centred between the tools and
    /// the actions. The chip states the current colour rather than merely advertising that colours
    /// exist, which is why it carries the hex.
    /// </summary>
    private void DrawColorAndThickness(
        EditorDocument document, double left, double right, double y, double tile)
    {
        var swatch = S(26);
        var swatchSpan = Palette.Swatches.Length * (swatch + S(2));
        var chip = S(104);
        var sliderSpan = S(46) + S(120) + S(30);

        var content = swatchSpan + S(12) + chip + S(18) + sliderSpan;
        var available = right - left;
        if (available < content) return;   // too narrow: the toolbar drops the centre group

        var x = left + (available - content) / 2;

        // Swatches. Mutual exclusion lives in the document.
        foreach (var hex in Palette.Swatches)
        {
            if (ui.Swatch(hex.GetHashCode(), new Rect(x, y, swatch, tile),
                Palette.Parse(hex), document.ColorHex == hex))
                document.SetColor(hex);
            x += swatch + S(2);
        }
        x += S(12);

        // The live colour chip: a 16px well, the hex, and a chevron. Clicking it opens the picker,
        // so any colour is reachable and not just the six swatches.
        var chipBounds = new Rect(x, y + S(3), chip, tile - S(6));
        var open = _picker.IsOpen;

        if (ui.Interact(9002, chipBounds))
        {
            if (open) _picker.Close();
            else _picker.Open(Palette.Parse(document.ColorHex));
        }

        ui.FillRounded(chipBounds, (float)S(Metrics.RadiusControl),
            open ? ui.Theme.FillSelected
            : ui.IsHot(9002) ? ui.Theme.FillHover
            : ui.Theme.SurfaceOverlay);
        ui.StrokeRounded(chipBounds, (float)S(Metrics.RadiusControl), ui.Theme.StrokeSubtle);

        var well = new Rect(chipBounds.X + S(7), chipBounds.Center.Y - S(8), S(16), S(16));
        ui.FillRounded(well, (float)S(4), Palette.Parse(document.ColorHex));
        ui.StrokeRounded(well, (float)S(4), ui.Theme.StrokeStrong);

        ui.Text(document.ColorHex.ToUpperInvariant(),
            new Rect(well.Right + S(7), chipBounds.Y, chip - S(38), chipBounds.Height),
            ui.Theme.TextSecondary, (float)S(Metrics.FontCaption), monospace: true);

        ui.Icon(Icons.ChevronDown,
            new Rect(chipBounds.Right - S(16), chipBounds.Y, S(12), chipBounds.Height),
            ui.Theme.TextTertiary, S(8));

        _pickerAnchor = chipBounds;
        x += chip + S(18);

        // Thickness. Brush and eraser work at a far larger scale than a stroke, so the range follows
        // the tool - the rule the XAML slider's Maximum encoded.
        var isPaint = document.ActiveTool is EditorTool.Brush or EditorTool.Eraser;
        var thickness = document.ActiveThickness;

        ui.Text(isPaint ? "Size" : "Width", new Rect(x, y, S(42), tile),
            ui.Theme.TextTertiary, (float)S(Metrics.FontCaption));
        x += S(46);

        if (ui.Slider(9001, new Rect(x, y, S(120), tile), 1, isPaint ? 300 : 20, ref thickness))
            document.SetStrokeThickness(thickness, isAdjusting: true);
        x += S(126);

        ui.Text(((int)Math.Round(thickness)).ToString(), new Rect(x, y, S(24), tile),
            ui.Theme.TextSecondary, (float)S(Metrics.FontCaption));
    }

    /// <summary>
    /// The footer: status and zoom on the left, Copy and Save on the right.
    ///
    /// Copy and Save belong here rather than in the toolbar. The toolbar is for what you are drawing
    /// with; these act on the whole document, and putting them among the tools made them read as
    /// floating - which is exactly how they looked.
    /// </summary>
    private void DrawFooter(
        EditorDocument document, double width, double height, bool fit, string? toast)
    {
        var bar = new Rect(0, height - FooterHeight, width, FooterHeight);
        ui.FillRect(bar, ui.Theme.SurfaceRaised);
        ui.FillRect(new Rect(0, bar.Y, width, 1), ui.Theme.StrokeSubtle);

        var y = bar.Y + (bar.Height - S(32)) / 2;

        var crop = (document.PendingCrop ?? document.CropBounds) is { } c
            ? $"   ·   Crop {(int)c.Width}×{(int)c.Height} — Enter to apply, Esc to cancel"
            : string.Empty;

        var status = $"{(int)document.ImageWidth}×{(int)document.ImageHeight}"
            + $"   ·   {document.Annotations.Count} annotation(s){crop}";

        ui.Text(status, new Rect(S(14), bar.Y, width * 0.45, bar.Height),
            ui.Theme.TextTertiary, (float)S(Metrics.FontCaption));

        // Zoom, left of centre.
        var x = width * 0.5 - S(66);
        if (ui.Button(9030, new Rect(x, y, S(58), S(32)), "Fit",
            fontSize: S(Metrics.FontCaption), toggled: fit))
            FitPicked = true;

        x += S(62);
        if (ui.Button(9031, new Rect(x, y, S(58), S(32)), "100%",
            fontSize: S(Metrics.FontCaption), toggled: !fit))
            FitPicked = false;

        // Actions, right. Buttons hug their content rather than being fixed-width blocks.
        var right = width - S(14);
        var font = S(Metrics.FontBody);
        var glyph = S(13);

        var save = Width(ui, "Save", font, glyph);
        right -= save;
        if (ui.Button(9020, new Rect(right, y, save, S(32)), "Save",
            primary: true, glyph: Icons.Save, glyphSize: glyph, fontSize: font))
            SavePressed = true;

        var saveAs = Width(ui, "Save as…", font);
        right -= saveAs + S(8);
        if (ui.Button(9022, new Rect(right, y, saveAs, S(32)), "Save as…", fontSize: font))
            SaveAsPressed = true;

        var copy = Width(ui, "Copy", font, glyph);
        right -= copy + S(8);
        if (ui.Button(9021, new Rect(right, y, copy, S(32)), "Copy",
            glyph: Icons.Copy, glyphSize: glyph, fontSize: font))
            CopyPressed = true;

        // A confirmation, so an action that changes nothing visible still says it happened.
        if (toast is null) return;

        var badge = new Rect(right - S(88), y, S(80), S(32));
        ui.FillRounded(badge, (float)S(Metrics.RadiusControl), ui.Theme.Accent);
        ui.Text(toast, badge, ui.Theme.TextOnAccent, (float)font, align: TextAlign.Center);
    }

    /// <summary>A button sized to its content: 14px of padding either side.</summary>
    private static double Width(Ui ui, string label, double font, double glyph = 0)
    {
        var content = ui.MeasureText(label, font, bold: true);
        if (glyph > 0) content += glyph + glyph * 0.55;
        return Math.Round(content + S(28));
    }

    private static string Glyph(EditorTool tool) => tool switch
    {
        EditorTool.Select => Icons.Select,
        EditorTool.Rectangle => Icons.Rectangle,
        EditorTool.Ellipse => Icons.Ellipse,
        EditorTool.Line => Icons.Line,
        EditorTool.Arrow => Icons.Arrow,
        EditorTool.Pen => Icons.Pen,
        EditorTool.Brush => Icons.Brush,
        EditorTool.Eraser => Icons.Eraser,
        EditorTool.Text => Icons.Text,
        EditorTool.Highlight => Icons.Highlight,
        EditorTool.Blur => Icons.Blur,
        EditorTool.Pixelate => Icons.Pixelate,
        EditorTool.Counter => Icons.Counter,
        EditorTool.Spotlight => Icons.Spotlight,
        EditorTool.Crop => Icons.Crop,
        _ => string.Empty,
    };

    /// <summary>Tooltips, with each tool's shortcut.</summary>
    private static string Label(EditorTool tool) => tool switch
    {
        EditorTool.Select => "Select  (V)",
        EditorTool.Rectangle => "Rectangle  (R)",
        EditorTool.Ellipse => "Ellipse  (E)",
        EditorTool.Line => "Line  (L)",
        EditorTool.Arrow => "Arrow  (A)",
        EditorTool.Pen => "Pen  (D)",
        EditorTool.Brush => "Brush  (M)",
        EditorTool.Eraser => "Erase pen and brush  (X)",
        EditorTool.Text => "Text  (T)",
        EditorTool.Highlight => "Highlight  (H)",
        EditorTool.Blur => "Blur  (B)",
        EditorTool.Pixelate => "Pixelate  (P)",
        EditorTool.Counter => "Counter  (N)",
        EditorTool.Spotlight => "Spotlight  (S)",
        EditorTool.Crop => "Crop  (C)",
        _ => tool.ToString(),
    };
}
