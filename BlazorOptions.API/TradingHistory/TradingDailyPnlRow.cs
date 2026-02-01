namespace BlazorOptions.API.TradingHistory;

public sealed class TradingDailyPnlRow
{
    public string Day { get; set; } = string.Empty;

    public string SettleCoin { get; set; } = string.Empty;

    public decimal RealizedPnl { get; set; }
}
