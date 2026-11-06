namespace NexusShot.Core;

/// <summary>
/// A scroll position and its range. A wheel notch sets a target the position glides to, since a
/// hundred-pixel jump reads as a stutter; a touchpad pan moves the position itself.
/// </summary>
public sealed class ScrollState
{
    /// <summary>The glide's time constant.</summary>
    public const double GlideMs = 70;

    public double Position { get; private set; }
    public double Target { get; private set; }
    public double Max { get; private set; }

    /// <summary>Sets the range, pulling the position and target back inside it.</summary>
    public void SetRange(double max)
    {
        Max = Math.Max(0, max);
        Position = Clamp(Position);
        Target = Clamp(Target);
    }

    /// <summary>Positive moves toward the top; quick steps add up.</summary>
    public void Wheel(double step) => Target = Clamp(Target - step);

    /// <summary>Applied at once, cancelling any glide.</summary>
    public void Pan(double delta)
    {
        Position = Clamp(Position - delta);
        Target = Position;
    }

    public void Reset() => Position = Target = 0;

    /// <summary>Advances a glide; true while it has distance left. It always lands exactly.</summary>
    public bool Step(double elapsedMs)
    {
        var remaining = Target - Position;
        if (Math.Abs(remaining) < 0.5)
        {
            Position = Target;
            return false;
        }
        Position += remaining * (1 - Math.Exp(-Math.Max(0, elapsedMs) / GlideMs));
        return true;
    }

    private double Clamp(double value) => Math.Clamp(value, 0, Max);
}
