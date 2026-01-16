namespace BlazorOptions.ViewModels;

public record TradingTransactionCalculated
{
    public string SizeAfter { get; init; } = string.Empty;
    public string AvgPriceAfter { get; init; } = string.Empty;
    public string RealizedPnl { get; init; } = string.Empty;
    public string CumulativePnl { get; init; } = string.Empty;
}
