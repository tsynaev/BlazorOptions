namespace BlazorOptions.ViewModels;

public record TradingPnlByCoinRow
{
    public string SettleCoin { get; init; } = string.Empty;
    public string RealizedPnl { get; init; } = string.Empty;
    public string Fees { get; init; } = string.Empty;
    public string NetPnl { get; init; } = string.Empty;
}
