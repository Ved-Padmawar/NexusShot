namespace NexusShot.Core;

/// <summary>Arrow shaft and head geometry, shared by the renderer and the exporter.</summary>
public static class ArrowGeometry
{
    /// <summary>The shaft stops short of the tip so the head is not drawn over a line end.</summary>
    public static Point ShaftEnd(Annotation annotation)
    {
        var dx = annotation.End.X - annotation.Start.X;
        var dy = annotation.End.Y - annotation.Start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1) return annotation.End;
        var head = Math.Min(length, annotation.StrokeThickness * 5) * 0.8;
        return new Point(annotation.End.X - dx / length * head, annotation.End.Y - dy / length * head);
    }

    public static Point[] Head(Annotation annotation)
    {
        var dx = annotation.End.X - annotation.Start.X;
        var dy = annotation.End.Y - annotation.Start.Y;
        var angle = Math.Atan2(dy, dx);
        var length = Math.Sqrt(dx * dx + dy * dy);
        var head = Math.Min(Math.Max(length, 1), annotation.StrokeThickness * 5);
        const double spread = 0.45;

        return
        [
            annotation.End,
            new(annotation.End.X - Math.Cos(angle - spread) * head, annotation.End.Y - Math.Sin(angle - spread) * head),
            new(annotation.End.X - Math.Cos(angle + spread) * head, annotation.End.Y - Math.Sin(angle + spread) * head),
        ];
    }
}
