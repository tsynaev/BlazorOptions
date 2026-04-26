using BlazorOptions.Services;
using Microsoft.Extensions.Options;

namespace BlazorOptions.ViewModels;

public class BybitSettingsViewModel
{
    private readonly IOptions<BybitSettings> _bybitSettingsOptions;
    private readonly ExchangeConnectionsService _exchangeConnectionsService;

    public BybitSettingsViewModel(
        IOptions<BybitSettings> bybitSettingsOptions,
        ExchangeConnectionsService exchangeConnectionsService)
    {
        _bybitSettingsOptions = bybitSettingsOptions;
        _exchangeConnectionsService = exchangeConnectionsService;
    }

    public string ApiKey { get; set; } = string.Empty;

    public string ApiSecret { get; set; } = string.Empty;

    public int LivePriceUpdateIntervalMilliseconds { get; set; } = 1000;

    public string OptionBaseCoins { get; set; } = "BTC, ETH, SOL";

    public string OptionQuoteCoins { get; set; } = "USDT";

    public event Action? OnChange;

    public Task LoadAsync()
    {
        var settings = _bybitSettingsOptions.Value;
        ApiKey = settings.ApiKey;
        ApiSecret = settings.ApiSecret;
        LivePriceUpdateIntervalMilliseconds = Math.Max(100, settings.LivePriceUpdateIntervalMilliseconds);
        OptionBaseCoins = string.IsNullOrWhiteSpace(settings.OptionBaseCoins) ? "BTC, ETH, SOL" : settings.OptionBaseCoins;
        OptionQuoteCoins = string.IsNullOrWhiteSpace(settings.OptionQuoteCoins) ? "USDT" : settings.OptionQuoteCoins;
        OnChange?.Invoke();
        return Task.CompletedTask;
    }

    public async Task SaveAsync()
    {
        var settings = new MainBybitSettings
        {
            ApiKey = ApiKey,
            ApiSecret = ApiSecret,
            LivePriceUpdateIntervalMilliseconds = Math.Max(100, LivePriceUpdateIntervalMilliseconds),
            OptionBaseCoins = OptionBaseCoins,
            OptionQuoteCoins = OptionQuoteCoins
        };

        var mainConnection = ExchangeConnectionModel.CreateBybitMain();
        mainConnection.ApiKey = settings.ApiKey;
        mainConnection.ApiSecret = settings.ApiSecret;
        mainConnection.LivePriceUpdateIntervalMilliseconds = settings.LivePriceUpdateIntervalMilliseconds;
        mainConnection.OptionBaseCoins = settings.OptionBaseCoins;
        mainConnection.OptionQuoteCoins = settings.OptionQuoteCoins;
        await _exchangeConnectionsService.SaveConnectionsAsync([mainConnection, ExchangeConnectionModel.CreateBybitDemo()]);
        OnChange?.Invoke();
    }
}
