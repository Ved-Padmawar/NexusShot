using NexusShot.Core;

namespace NexusShot.Tests;

/// <summary>Shared helpers for driving an <see cref="EditorDocument"/> the way a pointer does.</summary>
internal static class Editing
{
    internal const double ImageWidth = 1000;
    internal const double ImageHeight = 800;

    internal static EditorDocument NewDocument()
    {
        var document = new EditorDocument();
        document.SetImageSize(ImageWidth, ImageHeight);
        return document;
    }

    /// <summary>Drags a two-point shape from one corner to another.</summary>
    internal static Annotation Draw(
        EditorDocument document, EditorTool tool, Point from, Point to, double thickness = 4)
    {
        document.SelectAnnotation(null);
        document.ActiveTool = tool;
        document.SetStrokeThickness(thickness);
        document.BeginGesture(from);
        document.ContinueGesture(to);
        document.EndGesture(to);
        return document.Annotations[^1];
    }

    /// <summary>Paints a freehand stroke through the given path.</summary>
    internal static Annotation Stroke(
        EditorDocument document, EditorTool tool, IReadOnlyList<Point> path, double thickness = 8)
    {
        document.SelectAnnotation(null);
        document.ActiveTool = tool;
        document.SetStrokeThickness(thickness);
        document.BeginGesture(path[0]);
        for (var i = 1; i < path.Count; i++)
            document.ContinueGesture(path[i]);
        document.EndGesture(path[^1]);
        return document.Annotations[^1];
    }

    /// <summary>Places a counter badge, which is a single click.</summary>
    internal static Annotation Counter(EditorDocument document, Point at)
    {
        document.SelectAnnotation(null);
        document.ActiveTool = EditorTool.Counter;
        document.BeginGesture(at);
        document.EndGesture(at);
        return document.Annotations[^1];
    }

    /// <summary>Drags from one point to another as a single gesture.</summary>
    internal static void Drag(EditorDocument document, Point from, Point to)
    {
        document.BeginGesture(from);
        document.ContinueGesture(to);
        document.EndGesture(to);
    }
}
