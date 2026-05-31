namespace BlazorOptions.API.TradingHistory;

public sealed class TradingHistorySaveResult
{
    public int RequestedCount { get; set; }
    public int InsertedCount { get; set; }
    public IReadOnlyList<string> InsertedIds { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> InsertedSymbols { get; set; } = Array.Empty<string>();
}
