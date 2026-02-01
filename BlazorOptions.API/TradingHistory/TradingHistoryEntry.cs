namespace BlazorOptions.API.TradingHistory;

public sealed class TradingHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public long? Timestamp { get; set; }

    public string Symbol { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string TransactionType { get; set; } = string.Empty;

    public string Side { get; set; } = string.Empty;

    public decimal Size { get; set; }

    public decimal Price { get; set; }

    public decimal Fee { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal Change { get; set; }

    public decimal CashFlow { get; set; }

    public string OrderId { get; set; } = string.Empty;

    public string OrderLinkId { get; set; } = string.Empty;

    public string TradeId { get; set; } = string.Empty;

    public string RawJson { get; set; } = string.Empty;

    public long ChangedAt { get; set; }

    public TradingTransactionCalculated Calculated { get; set; } = new();
}
