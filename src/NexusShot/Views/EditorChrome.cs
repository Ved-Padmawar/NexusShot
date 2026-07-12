using NexusShot.Core;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>
/// The editor's toolbar, drawn immediately.
///
/// This is what replaces ToolTile, ColorSwatch, HexColorPicker, the thickness Slider, the font
/// ComboBox and the B/I/U ToggleButtons, along with the XAML that laid them out and the code-behind
/// that kept them in sync with the document (_isLoadingThickness, _isLoadingTextFormat, and the
/// re-entrancy guards those flags existed to suppress).
///
/// There is no syncing here because there is no duplicated state: each control reads the document
/// when it draws, so it cannot be stale, and writes to it when clicked.
/// </summary>
public sealed class EditorChrome(Ui ui)
{
    /// <summary>
    /// The display scale. The render target is fixed at 96 DPI so a unit is a physical pixel; the
    /// chrome then scales itself, which is what keeps "100% zoom" meaning one image pixel to one
    /// physical pixel rather than one DIP. The canvas deliberately does not scale: a screenshot's
    /// pixels are its own, and stretching them to DIPs is exactly the softness this rewrite removed.
    /// </summary>
    public static double Scale { get; set; } = 1;

    public static double ToolbarHeight => 52 * Scale;
    public static double FooterHeight => 30 * Scale;

    private static double TileSize => 36 * Scale;
    private static double TileGap => 2 * Scale;

    private static readonly EditorTool[] Tools =
    [
        EditorTool.Select, EditorTool.Rectangle, EditorTool.Ellipse, EditorTool.Line, EditorTool.Arrow,
        EditorTool.Pen, EditorTool.Brush, EditorTool.Eraser, EditorTool.Text, EditorTool.Highlight,
        EditorTool.Blur, EditorTool.Pixelate, EditorTool.Counter, EditorTool.Spotlight, EditorTool.Crop,
    ];

    /// <summary>What the user asked for this frame. The window applies it; the chrome never mutates
    /// anything it does not own.</summary>
    public EditorTool? ToolPicked { get; private set; }
    public bool UndoPressed { get; private set; }
    public bool RedoPressed { get; private set; }
    public bool SavePressed { get; private set; }
    public bool CopyPressed { get; private set; }

    public void Draw(EditorDocument document, double width, double height, string title)
    {
        ToolPicked = null;
        UndoPressed = RedoPressed = SavePressed = CopyPressed = false;

        DrawToolbar(document, width);
        DrawFooter(document, width, height, title);
    }

    /// <summary>Design units to physical pixels. Every metric in this file goes through here, so
    /// there is no way to leave one unscaled and have it look right only at 100%.</summary>
    private static double S(double units) => units * Scale;

    private void DrawToolbar(EditorDocument document, double width)
    {
        // Raised, not base: the toolbar is chrome sitting above the sunken canvas well, and reads as
        // lifted rather than flush - the same elevation the XAML build gave it.
        ui.FillRect(new Rect(0, 0, width, ToolbarHeight), ui.Theme.SurfaceRaised);
        ui.FillRect(new Rect(0, ToolbarHeight - S(1), width, S(1)), ui.Theme.StrokeSubtle);

        var tile = TileSize;
        var y = (ToolbarHeight - tile) / 2;
        var x = S(8);

        // Tools. The id is derived from the tool, so it is stable however the bar is laid out.
        foreach (var tool in Tools)
        {
            if (ui.Tile((int)tool + 100, new Rect(x, y, tile, tile),
                document.ActiveTool == tool, ToolIcons.For(tool), Label(tool)))
                ToolPicked = tool;
            x += tile + TileGap;
        }

        x += S(8);
        ui.Separator(x, y + S(4), tile - S(8));
        x += S(14);

        // Colour swatches. Mutual exclusion lives in the document, not in a RadioButton group.
        foreach (var hex in Palette.Swatches)
        {
            if (ui.Swatch(hex.GetHashCode(), new Rect(x, y, S(30), tile),
                Palette.Parse(hex), document.ColorHex == hex))
                document.SetColor(hex);
            x += S(32);
        }

        x += S(8);
        ui.Separator(x, y + S(4), tile - S(8));
        x += S(14);

        // Thickness. Brush and eraser work at a far larger scale than a stroke, so the range follows
        // the tool - the rule the XAML slider's Maximum encoded.
        var isPaint = document.ActiveTool is EditorTool.Brush or EditorTool.Eraser;
        var thickness = document.ActiveThickness;

        ui.Text(isPaint ? "Size" : "Width", new Rect(x, y, S(38), tile),
            ui.Theme.TextTertiary, Font(Metrics.FontCaption));
        x += S(42);

        if (ui.Slider(9001, new Rect(x, y, S(110), tile), 1, isPaint ? 300 : 20, ref thickness))
            document.SetStrokeThickness(thickness, isAdjusting: true);
        x += S(118);

        ui.Text(((int)Math.Round(thickness)).ToString(), new Rect(x, y, S(26), tile),
            ui.Theme.TextSecondary, Font(Metrics.FontCaption));
        x += S(32);

        // Text formatting appears only when text is in play, exactly as the XAML panel did.
        var textTarget = document.Selected is { Tool: EditorTool.Text } selected ? selected : null;
        if (textTarget is not null || document.ActiveTool == EditorTool.Text)
            DrawTextFormat(document, textTarget, ref x, y, tile);

        // Right-aligned actions.
        var right = width - S(8);

        right -= S(74);
        if (ui.Button(9020, new Rect(right, y + S(6), S(74), S(24)), "Save", primary: true))
            SavePressed = true;

        right -= S(70);
        if (ui.Button(9021, new Rect(right, y + S(6), S(62), S(24)), "Copy"))
            CopyPressed = true;

        right -= tile + S(6);
        if (ui.Tile(9023, new Rect(right, y, tile, tile), false, ToolIcons.Redo, "Redo") && document.CanRedo)
            RedoPressed = true;

        right -= tile + S(2);
        if (ui.Tile(9022, new Rect(right, y, tile, tile), false, ToolIcons.Undo, "Undo") && document.CanUndo)
            UndoPressed = true;
    }

    private void DrawTextFormat(EditorDocument document, Annotation? target, ref double x, double y, double tile)
    {
        ui.Separator(x, y + S(4), tile - S(8));
        x += S(14);

        var bold = target?.IsBold ?? document.TextBold;
        var italic = target?.IsItalic ?? document.TextItalic;
        var underline = target?.IsUnderline ?? document.TextUnderline;

        if (ui.Toggle(9010, new Rect(x, y, S(30), tile), "B", bold, bold: true))
            document.SetTextFormat(d => d.TextBold = !bold, a => a.IsBold = !bold);
        x += S(32);

        if (ui.Toggle(9011, new Rect(x, y, S(30), tile), "I", italic, italic: true))
            document.SetTextFormat(d => d.TextItalic = !italic, a => a.IsItalic = !italic);
        x += S(32);

        if (ui.Toggle(9012, new Rect(x, y, S(30), tile), "U", underline))
            document.SetTextFormat(d => d.TextUnderline = !underline, a => a.IsUnderline = !underline);
        x += S(40);

        // Font size as a stepper: a dropdown would need a whole popup layer to pick one integer.
        var size = target?.FontSize ?? document.TextFontSize;

        if (ui.Button(9013, new Rect(x, y + S(6), S(24), S(24)), "−"))
            StepFontSize(document, target, -1);
        x += S(28);

        ui.Text(((int)size).ToString(), new Rect(x, y, S(26), tile),
            ui.Theme.TextPrimary, Font(Metrics.FontBody), align: TextAlign.Center);
        x += S(30);

        if (ui.Button(9014, new Rect(x, y + S(6), S(24), S(24)), "+"))
            StepFontSize(document, target, 1);
        x += S(30);
    }

    /// <summary>Font sizes are in points, so they scale with the display like every other metric.</summary>
    private static float Font(float size) => (float)S(size);

    private static void StepFontSize(EditorDocument document, Annotation? target, int direction)
    {
        var current = (int)Math.Round(target?.FontSize ?? document.TextFontSize);
        var sizes = Palette.FontSizes;
        var index = Array.IndexOf(sizes, current);
        if (index < 0)
        {
            // Not on a step: snap to the nearest, then move.
            index = 0;
            for (var i = 1; i < sizes.Length; i++)
                if (Math.Abs(sizes[i] - current) < Math.Abs(sizes[index] - current)) index = i;
        }
        var next = sizes[Math.Clamp(index + direction, 0, sizes.Length - 1)];
        document.SetTextFormat(d => d.TextFontSize = next, a => a.FontSize = next);
    }

    private void DrawFooter(EditorDocument document, double width, double height, string title)
    {
        var bar = new Rect(0, height - FooterHeight, width, FooterHeight);
        ui.FillRect(bar, ui.Theme.SurfaceRaised);
        ui.FillRect(new Rect(0, bar.Y, width, S(1)), ui.Theme.StrokeSubtle);

        var crop = (document.PendingCrop ?? document.CropBounds) is { } c
            ? $"   •   Crop {(int)c.Width}×{(int)c.Height} — Save to apply, Esc to cancel"
            : string.Empty;

        var status = $"{(int)document.ImageWidth}×{(int)document.ImageHeight}"
            + $"   •   {document.Annotations.Count} annotation(s){crop}";

        var text = new Rect(S(12), bar.Y, width - S(24), FooterHeight);
        ui.Text(status, text, ui.Theme.TextTertiary, Font(Metrics.FontCaption));
        ui.Text(title, text, ui.Theme.TextTertiary, Font(Metrics.FontCaption), align: TextAlign.Right);
    }

    private static string Label(EditorTool tool) => tool switch
    {
        EditorTool.Select => "Select  V",
        EditorTool.Rectangle => "Rectangle  R",
        EditorTool.Ellipse => "Ellipse  O",
        EditorTool.Line => "Line  L",
        EditorTool.Arrow => "Arrow  A",
        EditorTool.Pen => "Pen  P",
        EditorTool.Brush => "Brush  B",
        EditorTool.Eraser => "Eraser  E",
        EditorTool.Text => "Text  T",
        EditorTool.Highlight => "Highlight  H",
        EditorTool.Blur => "Blur  U",
        EditorTool.Pixelate => "Pixelate  X",
        EditorTool.Counter => "Counter  N",
        EditorTool.Spotlight => "Spotlight  S",
        EditorTool.Crop => "Crop  C",
        _ => tool.ToString(),
    };
}
