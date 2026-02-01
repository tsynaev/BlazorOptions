namespace BlazorOptions.API.TradingHistory;

public sealed class TradingTransactionCalculated
{
    public decimal SizeAfter { get; set; }

    public decimal AvgPriceAfter { get; set; }

    public decimal RealizedPnl { get; set; }

    public decimal CumulativePnl { get; set; }
}
