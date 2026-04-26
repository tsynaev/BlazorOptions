using BlazorOptions.API.Common;

namespace BlazorOptions.ViewModels;

public sealed class ExchangeConnectionModel : Bindable
{
    public const string BybitProvider = "bybit";
    public const string BybitMainId = "bybit-main";
    public const string BybitDemoId = "bybit-demo";

    private string _id = BybitMainId;
    private string _name = "Bybit Main";
    private string _provider = BybitProvider;
    private string _apiKey = string.Empty;
    private string _apiSecret = string.Empty;
    private int _livePriceUpdateIntervalMilliseconds = 1000;
    private string _optionBaseCoins = "BTC, ETH, SOL";
    private string _optionQuoteCoins = "USDT";

    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string Provider
    {
        get => _provider;
        set => SetField(ref _provider, value);
    }

    public string ApiKey
    {
        get => _apiKey;
        set => SetField(ref _apiKey, value);
    }

    public string ApiSecret
    {
        get => _apiSecret;
        set => SetField(ref _apiSecret, value);
    }

    public int LivePriceUpdateIntervalMilliseconds
    {
        get => _livePriceUpdateIntervalMilliseconds;
        set => SetField(ref _livePriceUpdateIntervalMilliseconds, value);
    }

    public string OptionBaseCoins
    {
        get => _optionBaseCoins;
        set => SetField(ref _optionBaseCoins, value);
    }

    public string OptionQuoteCoins
    {
        get => _optionQuoteCoins;
        set => SetField(ref _optionQuoteCoins, value);
    }

    public ExchangeConnectionModel Clone()
    {
        return new ExchangeConnectionModel
        {
            Id = Id,
            Name = Name,
            Provider = Provider,
            ApiKey = ApiKey,
            ApiSecret = ApiSecret,
            LivePriceUpdateIntervalMilliseconds = LivePriceUpdateIntervalMilliseconds,
            OptionBaseCoins = OptionBaseCoins,
            OptionQuoteCoins = OptionQuoteCoins
        };
    }

    public BybitSettings ToBybitSettings()
    {
        var isDemoConnection = IsDemoConnectionId(Id);
        BybitSettings settings = isDemoConnection ? new DemoBybitSettings() : new MainBybitSettings();
        settings.ApiKey = ApiKey ?? string.Empty;
        settings.ApiSecret = ApiSecret ?? string.Empty;
        settings.LivePriceUpdateIntervalMilliseconds = Math.Max(100, LivePriceUpdateIntervalMilliseconds);
        settings.OptionBaseCoins = string.IsNullOrWhiteSpace(OptionBaseCoins) ? "BTC, ETH, SOL" : OptionBaseCoins;
        settings.OptionQuoteCoins = string.IsNullOrWhiteSpace(OptionQuoteCoins) ? "USDT" : OptionQuoteCoins;
        return settings;
    }

    public static ExchangeConnectionModel CreateBybitMain()
    {
        return new ExchangeConnectionModel
        {
            Id = BybitMainId,
            Name = "Bybit Main",
            Provider = BybitProvider
        };
    }

    public static ExchangeConnectionModel CreateBybitDemo()
    {
        return new ExchangeConnectionModel
        {
            Id = BybitDemoId,
            Name = "Bybit Demo",
            Provider = BybitProvider
        };
    }

    public static bool IsDemoConnectionId(string? id)
    {
        return string.Equals(id?.Trim(), BybitDemoId, StringComparison.OrdinalIgnoreCase);
    }
}
