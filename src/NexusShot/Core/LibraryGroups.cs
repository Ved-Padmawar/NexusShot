namespace NexusShot.Core;

/// <summary>How far back the Library looks. Rolling windows, as the day groups are.</summary>
public enum LibraryPeriod { All, Today, Week, Month }

/// <summary>What the Library shows. The query matches a capture's name, or the text read out of it
/// once it has been; <paramref name="FavoritesOnly"/> and <paramref name="Period"/> narrow further.</summary>
public sealed record LibraryFilter(string Query = "", bool FavoritesOnly = false, LibraryPeriod Period = LibraryPeriod.All)
{
    public bool Shows(ScreenshotHistoryItem item, DateTime now, Func<string, string?>? textOf = null)
    {
        if (FavoritesOnly && !item.Favorite) return false;
        var days = (now.Date - item.CapturedAt.LocalDateTime.Date).TotalDays;
        var inPeriod = Period switch
        {
            LibraryPeriod.Today => days <= 0,
            LibraryPeriod.Week => days < 7,
            LibraryPeriod.Month => days < 30,
            _ => true,
        };
        return inPeriod && (Query.Length == 0
            || item.FileName.Contains(Query, StringComparison.OrdinalIgnoreCase)
            || textOf?.Invoke(item.FilePath)?.Contains(Query, StringComparison.OrdinalIgnoreCase) == true);
    }
}

/// <summary>
/// The library's sections: captures filtered and grouped by when they were taken. Newest first
/// within a group, and groups in time order, because the history already is.
/// </summary>
public static class LibraryGroups
{
    public sealed record Group(string Title, IReadOnlyList<ScreenshotHistoryItem> Items);

    /// <summary>Today, Yesterday, This week, then one group per month. The week is the last seven
    /// days rather than the calendar week, so "This week" never means one day on a Monday.</summary>
    public static List<Group> Build(IReadOnlyList<ScreenshotHistoryItem> history, LibraryFilter filter, DateTime now,
        Func<string, string?>? textOf = null)
    {
        var groups = new List<Group>();
        List<ScreenshotHistoryItem>? current = null;
        string? title = null;

        foreach (var item in history)
        {
            if (!filter.Shows(item, now, textOf)) continue;

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
