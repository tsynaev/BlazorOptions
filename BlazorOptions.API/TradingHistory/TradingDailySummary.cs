namespace BlazorOptions.API.TradingHistory;

public sealed class TradingDailySummary
{
    public string Key { get; set; } = string.Empty;

    public string Day { get; set; } = string.Empty;

    public string SymbolKey { get; set; } = string.Empty;

    public string Symbol { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public decimal TotalSize { get; set; }

    public decimal TotalValue { get; set; }

    public decimal TotalFee { get; set; }
}
