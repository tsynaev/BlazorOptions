namespace BlazorOptions.API.TradingHistory;

public sealed class TradingSummaryBySymbolRow
{
    public string Category { get; set; } = string.Empty;

    public string Symbol { get; set; } = string.Empty;

    public string SettleCoin { get; set; } = string.Empty;

    public int Trades { get; set; }

    public decimal TotalQty { get; set; }

    public decimal TotalValue { get; set; }

    public decimal TotalFees { get; set; }

    public decimal RealizedPnl { get; set; }
}
