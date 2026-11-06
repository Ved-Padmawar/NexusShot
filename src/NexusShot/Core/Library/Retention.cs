namespace NexusShot.Core;

/// <summary>Captures older than the user chose to keep. Starred ones are exempt: a favourite is
/// exactly what an age limit should not take.</summary>
public static class Retention
{
    /// <summary>The periods offered, in days; 0 keeps everything.</summary>
    public static readonly int[] Choices = [0, 30, 90, 365];

    public static List<ScreenshotHistoryItem> Expired(IEnumerable<ScreenshotHistoryItem> history, int days, DateTimeOffset now) =>
        days <= 0 ? [] : [.. history.Where(item => !item.Favorite && now - item.CapturedAt > TimeSpan.FromDays(days))];
}
