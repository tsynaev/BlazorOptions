namespace BlazorOptions.API.TradingHistory;

public static class TradingHistoryEntryOrdering
{
    public static IOrderedEnumerable<TradingHistoryEntry> OrderAscending(IEnumerable<TradingHistoryEntry> entries)
    {
        return entries
            .OrderBy(entry => entry.Timestamp ?? 0)
            .ThenBy(entry => GetIdPrefix(entry.Id), StringComparer.Ordinal)
            .ThenBy(entry => GetTrailingSequence(entry.Id))
            .ThenBy(entry => entry.Id, StringComparer.Ordinal);
    }

    public static IOrderedEnumerable<TradingHistoryEntry> OrderDescending(IEnumerable<TradingHistoryEntry> entries)
    {
        return entries
            .OrderByDescending(entry => entry.Timestamp ?? 0)
            .ThenBy(entry => GetIdPrefix(entry.Id), StringComparer.Ordinal)
            .ThenBy(entry => GetTrailingSequence(entry.Id))
            .ThenBy(entry => entry.Id, StringComparer.Ordinal);
    }

    private static string GetIdPrefix(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        var value = id!;
        var lastUnderscore = value.LastIndexOf('_');
        if (lastUnderscore <= 0 || lastUnderscore >= value.Length - 1)
        {
            return value;
        }

        for (var i = lastUnderscore + 1; i < value.Length; i++)
        {
            if (!char.IsDigit(value[i]))
            {
                return value;
            }
        }

        return value.Substring(0, lastUnderscore);
    }

    private static int GetTrailingSequence(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return int.MaxValue;
        }

        var value = id!;
        var lastUnderscore = value.LastIndexOf('_');
        if (lastUnderscore < 0 || lastUnderscore >= value.Length - 1)
        {
            return int.MaxValue;
        }

        return int.TryParse(value.Substring(lastUnderscore + 1), out var sequence)
            ? sequence
            : int.MaxValue;
    }
}
