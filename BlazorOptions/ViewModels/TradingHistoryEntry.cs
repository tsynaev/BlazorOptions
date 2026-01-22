using System;

namespace BlazorOptions.ViewModels;

public sealed class TradingHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public long? Timestamp { get; set; }

    public string Symbol { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string TransactionType { get; set; } = string.Empty;

    public string Side { get; set; } = string.Empty;

    public string Size { get; set; } = string.Empty;

    public string Price { get; set; } = string.Empty;

    public string Fee { get; set; } = string.Empty;

    public string Currency { get; set; } = string.Empty;

    public string Change { get; set; } = string.Empty;

    public string CashFlow { get; set; } = string.Empty;

    public string OrderId { get; set; } = string.Empty;

    public string OrderLinkId { get; set; } = string.Empty;

    public string TradeId { get; set; } = string.Empty;

    public string RawJson { get; set; } = string.Empty;

    public long ChangedAt { get; set; }

    public TradingTransactionCalculated Calculated { get; set; } = new();
}
