namespace BlazorOptions.ViewModels;

public record BybitSettings
{
    public string ApiKey { get; init; } = string.Empty;
    public string ApiSecret { get; init; } = string.Empty;
    public string WebSocketUrl { get; init; } = "wss://stream.bybit.com/v5/public/linear";
    public int LivePriceUpdateIntervalSeconds { get; init; } = 1;
}
