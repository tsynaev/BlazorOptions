using BlazorOptions.API.Positions;
using System.Text.Json.Serialization;

namespace BlazorOptions.Services;

public sealed class TopTradesItem
{
    [JsonPropertyName("exchange")] public string? Exchange { get; set; }
    [JsonPropertyName("currency")] public string? Currency { get; set; }
    [JsonPropertyName("instrument")] public string? Instrument { get; set; }
    [JsonPropertyName("blockTradeId")] public string? BlockTradeId { get; set; }
    [JsonPropertyName("numberOfLegs")] public int? NumberOfLegs { get; set; }
    [JsonPropertyName("tradeAmount")] public decimal? TradeAmount { get; set; }
    [JsonPropertyName("blockAmount")] public decimal? BlockAmount { get; set; }
    [JsonPropertyName("tradeIv")] public decimal? TradeIv { get; set; }
    [JsonPropertyName("price")] public decimal? Price { get; set; }
    [JsonPropertyName("priceUsd")] public decimal? PriceUsd { get; set; }
    [JsonPropertyName("sizeUSD")] public decimal? SizeUsd { get; set; }
    [JsonPropertyName("sizeDelta")] public decimal? SizeDelta { get; set; }
    [JsonPropertyName("sizeVega")] public decimal? SizeVega { get; set; }
    [JsonPropertyName("sizeGamma")] public decimal? SizeGamma { get; set; }
    [JsonPropertyName("sizeTheta")] public decimal? SizeTheta { get; set; }
    [JsonPropertyName("date")] public string? Date { get; set; }
    [JsonPropertyName("indexPrice")] public decimal? IndexPrice { get; set; }
    [JsonPropertyName("amberdataDirection")] public string? AmberdataDirection { get; set; }
    [JsonPropertyName("exchangeDirection")] public string? ExchangeDirection { get; set; }
}

public sealed class BlockTradesItem
{
    [JsonPropertyName("uniqueTrade")] public string? UniqueTrade { get; set; }
    [JsonPropertyName("indexPrice")] public decimal? IndexPrice { get; set; }
    [JsonPropertyName("tradeAmount")] public decimal? TradeAmount { get; set; }
    [JsonPropertyName("netPremium")] public decimal? NetPremium { get; set; }
    [JsonPropertyName("numTrades")] public int? NumTrades { get; set; }
}

public sealed record StrategyPosition(
    string Id,
    string Instrument,
    string UniqueTrade,
    string Side,
    decimal Quantity,
    decimal Price,
    decimal PriceUsd,
    decimal SizeUsd,
    DateTime Timestamp,
    LegModel Leg,
    decimal? IndexPrice,
    decimal? NetPremium,
    int? NumTrades);

public sealed record PositionTradeDetailRow(
    DateTime? TimestampUtc,
    string Instrument,
    string Side,
    decimal TradeAmount,
    decimal? TradeIv,
    decimal? Price,
    decimal? PriceUsd,
    decimal? SizeUsd,
    decimal? IndexPrice,
    string? BlockTradeId);

public sealed record PositionTradeSnapshot(
    IReadOnlyList<LegModel> Legs,
    IReadOnlyList<PositionTradeDetailRow> Details);
