namespace BlazorOptions.Services;


public record ExchangePriceUpdate(
    string Exchange,
    string Symbol,
    decimal? MarkPrice,
    decimal? IndexPrice,
    DateTime Timestamp);
