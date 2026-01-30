using System;
using System.Text.Json.Serialization;

namespace BlazorOptions.ViewModels;

public sealed class TradingHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public long? Timestamp { get; set; }

    public string Symbol { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string TransactionType { get; set; } = string.Empty;

    public string Side { get; set; } = string.Empty;

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    [JsonConverter(typeof(SafeDecimalConverter))]
    public decimal Size { get; set; } 

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    [JsonConverter(typeof(SafeDecimalConverter))]
    public decimal Price { get; set; } 

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    [JsonConverter(typeof(SafeDecimalConverter))]
    public decimal Fee { get; set; } 

    public string Currency { get; set; } = string.Empty;

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    [JsonConverter(typeof(SafeDecimalConverter))]
    public decimal Change { get; set; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    [JsonConverter(typeof(SafeDecimalConverter))]
    public decimal CashFlow { get; set; }

    public string OrderId { get; set; } = string.Empty;

    public string OrderLinkId { get; set; } = string.Empty;

    public string TradeId { get; set; } = string.Empty;

    public string RawJson { get; set; } = string.Empty;

    public long ChangedAt { get; set; }

    public TradingTransactionCalculated Calculated { get; set; } = new();
}
