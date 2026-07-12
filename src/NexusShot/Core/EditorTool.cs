namespace NexusShot.Core;

/// <summary>Editor tools. Letters match the CleanShot X shortcuts.</summary>
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
