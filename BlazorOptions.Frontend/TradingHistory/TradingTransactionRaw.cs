using System;

namespace BlazorOptions.ViewModels;

public record TradingTransactionRaw
{
    public string UniqueKey { get; init; } = Guid.NewGuid().ToString("N");
    public string RawJson { get; init; } = string.Empty;
    public long? Timestamp { get; init; }
    public string Category { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public string TransactionType { get; init; } = string.Empty;
    public string TransSubType { get; init; } = string.Empty;
    public string Side { get; init; } = string.Empty;
    public decimal? Funding { get; init; }
    public string OrderLinkId { get; init; } = string.Empty;
    public string OrderId { get; init; } = string.Empty;
    public decimal? Fee { get; init; }
    public decimal? Change { get; init; } 
    public decimal? CashFlow { get; init; } 
    public decimal? FeeRate { get; init; } 
    public decimal? BonusChange { get; init; }
    public decimal? Size { get; init; } 
    public decimal? Qty { get; init; } 
    public decimal? CashBalance { get; init; } 
    public string Currency { get; init; } = string.Empty;
    public decimal? TradePrice { get; init; } 
    public string TradeId { get; init; } = string.Empty;
    public string ExtraFees { get; init; } = string.Empty;
}
