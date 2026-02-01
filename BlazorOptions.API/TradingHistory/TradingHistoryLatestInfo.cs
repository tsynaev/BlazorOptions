namespace BlazorOptions.API.TradingHistory;

public sealed class TradingHistoryLatestInfo
{
    public long? Timestamp { get; set; }

    public List<string> Ids { get; set; } = new();
}
