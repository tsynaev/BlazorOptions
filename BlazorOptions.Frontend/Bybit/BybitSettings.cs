namespace BlazorOptions.ViewModels;

public record BybitSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
    public Uri ApiBaseUrl { get; set; } = new("https://api.bybit.com");
    public Uri PublicWebSocketUrl { get; set; } = new("wss://stream.bybit.com/v5/public/linear");
    public Uri OptionPublicWebSocketUrl { get; set; } = new("wss://stream.bybit.com/v5/public/option");
    public Uri PrivateWebSocketUrl { get; set; } = new("wss://stream.bybit.com/v5/private?max_active_time=10m");
    public Uri OrderRealtimeUri => BuildApiUri("/v5/order/realtime");
    public Uri PositionListUri => BuildApiUri("/v5/position/list");
    public Uri WalletBalanceUri => BuildApiUri("/v5/account/wallet-balance");
    public Uri TransactionLogUri => BuildApiUri("/v5/account/transaction-log");
    public Uri MarketKlineUri => BuildApiUri("/v5/market/kline");
    public Uri OptionTickersUri => BuildApiUri("/v5/market/tickers?category=option");
    public Uri InstrumentsInfoUri => BuildApiUri("/v5/market/instruments-info?category=linear");
    public string DefaultSettleCoin { get; set; } = "USDT";
    public int LivePriceUpdateIntervalMilliseconds { get; set; } = 1000;
    public string OptionBaseCoins { get; set; } = "BTC, ETH, SOL";
    public string OptionQuoteCoins { get; set; } = "USDT";

    private Uri BuildApiUri(string path)
    {
        return new Uri(ApiBaseUrl, path);
    }
}

public record MainBybitSettings : BybitSettings
{
    public MainBybitSettings()
    {
        ApiBaseUrl = new Uri("https://api.bybit.com");
        PublicWebSocketUrl = new Uri("wss://stream.bybit.com/v5/public/linear");
        OptionPublicWebSocketUrl = new Uri("wss://stream.bybit.com/v5/public/option");
        PrivateWebSocketUrl = new Uri("wss://stream.bybit.com/v5/private?max_active_time=10m");
    }
}

public record DemoBybitSettings : BybitSettings
{
    public DemoBybitSettings()
    {
        ApiBaseUrl = new Uri("https://api-demo.bybit.com");
        PublicWebSocketUrl = new Uri("wss://stream.bybit.com/v5/public/linear");
        OptionPublicWebSocketUrl = new Uri("wss://stream.bybit.com/v5/public/option");
        PrivateWebSocketUrl = new Uri("wss://stream-demo.bybit.com/v5/private?max_active_time=10m");
    }
}
