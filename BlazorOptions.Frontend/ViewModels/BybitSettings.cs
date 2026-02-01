namespace BlazorOptions.ViewModels;

public record BybitSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
    public string WebSocketUrl { get; set; } = "wss://stream.bybit.com/v5/public/linear";
    public int LivePriceUpdateIntervalMilliseconds { get; set; } = 1000;
}
