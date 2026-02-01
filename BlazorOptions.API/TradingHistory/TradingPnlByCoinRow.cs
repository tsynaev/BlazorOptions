namespace BlazorOptions.API.TradingHistory;

public sealed class TradingPnlByCoinRow
{
    public string SettleCoin { get; set; } = string.Empty;

    public decimal RealizedPnl { get; set; }

    public decimal Fees { get; set; }

    public decimal NetPnl { get; set; }
}
