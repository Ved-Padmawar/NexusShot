namespace NexusShot.Core;

/// <summary>Editor tools. Letters match the shortcuts used by comparable capture tools.</summary>
public enum EditorTool
{
    Select,
    Rectangle,
    Ellipse,
    Line,
    Arrow,
    Pen,
    Brush,
    Eraser,
    Text,
    Highlight,
    Blur,
    Pixelate,
    Counter,
    Spotlight,
    Crop,
}

/// <summary>A grab point on the selected annotation: box corners and edges, or line endpoints.</summary>
public enum ResizeHandle
{
    TopLeft, Top, TopRight,
    Left, Right,
    BottomLeft, Bottom, BottomRight,
    LineStart, LineEnd,
}

/// <summary>
/// Each tool's one-letter shortcut. B is Blur, not Brush; P is Pixelate, not Pen - the letters users
/// arrive with. The key handler and the rail's tooltips both read this table, so a tooltip cannot
/// advertise a key that does something else.
/// </summary>
public static class ToolShortcuts
{
    public static char Letter(EditorTool tool) => tool switch
    {
        EditorTool.Select => 'V',
        EditorTool.Rectangle => 'R',
        EditorTool.Ellipse => 'E',
        EditorTool.Arrow => 'A',
        EditorTool.Line => 'L',
        EditorTool.Pen => 'D',
        EditorTool.Brush => 'M',
        EditorTool.Eraser => 'X',
        EditorTool.Text => 'T',
        EditorTool.Counter => 'N',
        EditorTool.Highlight => 'H',
        EditorTool.Blur => 'B',
        EditorTool.Pixelate => 'P',
        EditorTool.Spotlight => 'S',
        EditorTool.Crop => 'C',
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
    };

    /// <summary>The tool an uppercase letter selects, or null for a letter no tool uses.</summary>
    public static EditorTool? ToolFor(char letter)
    {
        foreach (var tool in Enum.GetValues<EditorTool>())
            if (Letter(tool) == letter) return tool;
        return null;
    }
}
