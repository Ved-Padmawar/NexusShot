namespace NexusShot.Render;

/// <summary>
/// The app's icons: Segoe Fluent Icons glyphs, the same codepoints the XAML build's FontIcon used.
///
/// This replaces a file of hand-drawn vector approximations. An icon assembled from line segments
/// reads as a diagram of an icon rather than an icon - off-weight, wrong joins, and not sitting on
/// the same optical grid as everything else in the OS. The font ships with Windows, so the real
/// glyphs cost nothing and are exactly what the old build drew.
///
/// Escapes rather than literal characters: these live in a private-use area, so a literal would be
/// an unreadable box in an editor and would not survive an encoding change.
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

    /// <summary>
    /// Segoe Fluent Icons: present on Windows 10 1809 and later, which is this app's floor anyway
    /// (Direct2D effects and per-monitor v2 DPI both need at least that).
    ///
    /// These codepoints exist in Segoe MDL2 Assets too, so even on a machine where DirectWrite
    /// substitutes the older font the glyphs still resolve - which is why there is no probe here.
    /// </summary>
    public const string Family = "Segoe Fluent Icons";
}
