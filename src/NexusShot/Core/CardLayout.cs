namespace NexusShot.Core;

public enum CardAction { Pin, Close, Edit, SaveAs, Copy, CopyText }

/// <summary>
/// The quick-access card, in design units: one size for every capture, filled edge to edge. Round
/// buttons sit in the corners and the two copies are text pills in the centre; everywhere else drags.
/// </summary>
public static class CardLayout
{
    public const double Width = 144;
    public const double Height = 90;
    public const double ButtonSize = 18;
    public const double Inset = 5;
    public const double PillWidth = 56;
    public const double PillHeight = 16;
    public const double PillGap = 3;

    public static readonly CardAction[] Actions = Enum.GetValues<CardAction>();

    public static Rect Bounds => new(0, 0, Width, Height);

    /// <summary>
    /// Where a card of <paramref name="card"/> size sits when <paramref name="offset"/> of the stack is
    /// already below it (above it, for a top corner): the stack grows away from its corner, so the
    /// newest card is always the one nearest it.
    /// </summary>
    public static Point Slot(Rect workArea, Size card, double margin, double offset, CardCorner corner)
    {
        var left = corner is CardCorner.BottomLeft or CardCorner.TopLeft;
        var top = corner is CardCorner.TopLeft or CardCorner.TopRight;
        return new Point(
            left ? workArea.X + margin : workArea.Right - margin - card.Width,
            top ? workArea.Y + margin + offset : workArea.Bottom - margin - card.Height - offset);
    }

    /// <summary>The way a dismissed card drifts: toward the screen edge it came from.</summary>
    public static int DismissDirection(CardCorner corner) =>
        corner is CardCorner.BottomLeft or CardCorner.TopLeft ? -1 : 1;

    /// <summary>The capture scaled to cover the card, cropped at whichever edges overhang.</summary>
    public static Rect Image(Size capture) => Bounds.Cover(capture);

    public static Rect Button(CardAction action)
    {
        const double right = Width - Inset - ButtonSize;
        const double bottom = Height - Inset - ButtonSize;
        const double pillX = (Width - PillWidth) / 2;
        const double pillY = (Height - PillHeight * 2 - PillGap) / 2;

        return action switch
        {
            CardAction.Pin => new Rect(Inset, Inset, ButtonSize, ButtonSize),
            CardAction.Close => new Rect(right, Inset, ButtonSize, ButtonSize),
            CardAction.Edit => new Rect(Inset, bottom, ButtonSize, ButtonSize),
            CardAction.SaveAs => new Rect(right, bottom, ButtonSize, ButtonSize),
            CardAction.Copy => new Rect(pillX, pillY, PillWidth, PillHeight),
            CardAction.CopyText => new Rect(pillX, pillY + PillHeight + PillGap, PillWidth, PillHeight),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    /// <summary>The button under <paramref name="point"/>, or null where a press starts a drag.</summary>
    public static CardAction? ButtonAt(Point point)
    {
        foreach (var action in Actions)
            if (Button(action).Contains(point)) return action;
        return null;
    }
}
