namespace BlazorOptions.ViewModels;

public record TradingSummaryRow
{
    public string Category { get; init; } = string.Empty;
    public string GroupKey { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public string SettleCoin { get; init; } = string.Empty;
    public int Trades { get; init; }
    public string TotalQty { get; init; } = string.Empty;
    public string TotalValue { get; init; } = string.Empty;
    public string TotalFees { get; init; } = string.Empty;
    public string RealizedPnl { get; init; } = string.Empty;
}
