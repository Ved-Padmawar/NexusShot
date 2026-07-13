namespace NexusShot.Render;

/// <summary>
/// Segoe Fluent Icons glyphs. Escapes rather than literals: these are private-use codepoints, so a
/// literal would be an unreadable box in an editor and would not survive an encoding change.
/// </summary>
public static class Icons
{
    public const string Select = "\uE8B0";
    public const string Rectangle = "\uE739";
    public const string Ellipse = "\uEA3A";
    public const string Line = "\uE738";
    public const string Arrow = "\uE8AD";
    public const string Pen = "\uE70F";
    public const string Brush = "\uE790";
    public const string Eraser = "\uE75C";
    public const string Text = "\uE8D2";
    public const string Highlight = "\uE7E6";
    public const string Blur = "\uE890";
    public const string Pixelate = "\uE80A";
    public const string Counter = "\uE8EF";
    public const string Spotlight = "\uE706";
    public const string Crop = "\uE7A8";
    public const string Undo = "\uE7A7";
    public const string Redo = "\uE7A6";
    public const string Delete = "\uE74D";
    public const string Copy = "\uE8C8";
    public const string Tick = "\uE73E";
    public const string Save = "\uE74E";
    public const string CaptureRegion = "\uE7A8";
    public const string CaptureScreen = "\uE7F4";
    public const string CaptureWindow = "\uE737";
    public const string Settings = "\uE713";
    public const string Theme = "\uE793";
    public const string Reveal = "\uE838";
    public const string Edit = "\uE70F";
    public const string Close = "\uE711";
    public const string EmptyState = "\uEB9F";
    public const string Pin = "\uE718";
    public const string ChevronDown = "\uE70D";
    public const string CaptionMinimise = "\uE921";
    public const string CaptionMaximise = "\uE922";
    public const string CaptionRestore = "\uE923";
    public const string CaptionClose = "\uE8BB";

    /// <summary>Present on Windows 10 1809 and later, which is this app's floor anyway. These
    /// codepoints exist in Segoe MDL2 Assets too, so a substituted font still resolves them.</summary>
    public const string Family = "Segoe Fluent Icons";
}
