namespace BlazorOptions.API.TradingHistory;

public sealed class TradingHistoryRequest
{
    public string Symbol { get; set; } = string.Empty;

    public string? Category { get; set; }

    public DateTime? SinceDateUtc { get; set; }
}
