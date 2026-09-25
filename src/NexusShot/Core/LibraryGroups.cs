namespace NexusShot.Core;

/// <summary>
/// The library's sections: captures filtered by a search and grouped by when they were taken.
/// Newest first within a group, and groups in time order, because the history already is.
/// </summary>
public static class LibraryGroups
{
    public sealed record Group(string Title, IReadOnlyList<ScreenshotHistoryItem> Items);

    /// <summary>Today, Yesterday, This week, then one group per month. The week is the last seven
    /// days rather than the calendar week, so "This week" never means one day on a Monday.</summary>
    public static List<Group> Build(IReadOnlyList<ScreenshotHistoryItem> history, string query, DateTime now)
    {
        var groups = new List<Group>();
        List<ScreenshotHistoryItem>? current = null;
        string? title = null;

        foreach (var item in history)
        {
            if (query.Length > 0 && !item.FileName.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            var itemTitle = Title(item.CapturedAt.LocalDateTime, now);
            if (itemTitle != title)
            {
                current = [];
                title = itemTitle;
                groups.Add(new Group(itemTitle, current));
            }
            current!.Add(item);
        }
        return groups;
    }

    public static string Title(DateTime captured, DateTime now)
    {
        var days = (now.Date - captured.Date).TotalDays;
        return days switch
        {
            <= 0 => "Today",
            1 => "Yesterday",
            < 7 => "This week",
            _ => captured.ToString("MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture),
        };
    }

    /// <summary>"2 min ago", "3 h ago", "Yesterday 18:42", "Mon 09:12", or a date - the tile caption.</summary>
    public static string Ago(DateTime captured, DateTime now)
    {
        var elapsed = now - captured;
        var days = (now.Date - captured.Date).TotalDays;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        if (elapsed.TotalMinutes < 1) return "Just now";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes} min ago";
        if (days <= 0) return $"{(int)elapsed.TotalHours} h ago";
        if (days == 1) return $"Yesterday {captured.ToString("HH:mm", culture)}";
        if (days < 7) return captured.ToString("ddd HH:mm", culture);
        return captured.ToString("d MMM", culture);
    }
}
