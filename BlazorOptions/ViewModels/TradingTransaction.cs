using System;

namespace BlazorOptions.ViewModels;

public record TradingTransaction
{
    public string UniqueKey { get; init; } = Guid.NewGuid().ToString("N");
    public string TimeLabel { get; init; } = "N/A";
    public long? Timestamp { get; init; }
    public string Category { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public string TransactionType { get; init; } = string.Empty;
    public string TransSubType { get; init; } = string.Empty;
    public string Side { get; init; } = string.Empty;
    public string Funding { get; init; } = string.Empty;
    public string OrderLinkId { get; init; } = string.Empty;
    public string OrderId { get; init; } = string.Empty;
    public string Fee { get; init; } = string.Empty;
    public string Change { get; init; } = string.Empty;
    public string CashFlow { get; init; } = string.Empty;
    public string FeeRate { get; init; } = string.Empty;
    public string BonusChange { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;
    public string Qty { get; init; } = string.Empty;
    public string CashBalance { get; init; } = string.Empty;
    public string SizeAfter { get; init; } = string.Empty;
    public string AvgPriceAfter { get; init; } = string.Empty;
    public string RealizedPnl { get; init; } = string.Empty;
    public string CumulativePnl { get; init; } = string.Empty;
    public string TradePrice { get; init; } = string.Empty;
    public string TradeId { get; init; } = string.Empty;
    public string ExtraFees { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}
