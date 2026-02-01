namespace BlazorOptions.API.TradingHistory;

public sealed class TradingHistoryResult
{
    public IReadOnlyList<TradingHistoryEntry> Entries { get; set; } = Array.Empty<TradingHistoryEntry>();

    public int TotalEntries { get; set; }
}
