using BlazorOptions.Services;
using Microsoft.Extensions.Options;

namespace BlazorOptions.ViewModels;

public class BybitSettingsViewModel
{
    private readonly IOptions<BybitSettings> _bybitSettingsOptions;
    private readonly ILocalStorageService _localStorageService;

    public BybitSettingsViewModel(IOptions<BybitSettings> bybitSettingsOptions, ILocalStorageService localStorageService)
    {
        _bybitSettingsOptions = bybitSettingsOptions;
        _localStorageService = localStorageService;
    }

    public string ApiKey { get; set; } = string.Empty;

    public string ApiSecret { get; set; } = string.Empty;

    public string WebSocketUrl { get; set; } = "wss://stream.bybit.com/v5/public/linear";

    public int LivePriceUpdateIntervalMilliseconds { get; set; } = 1000;

    public event Action? OnChange;

    public Task LoadAsync()
    {
        var settings = _bybitSettingsOptions.Value;
        ApiKey = settings.ApiKey;
        ApiSecret = settings.ApiSecret;
        WebSocketUrl = settings.WebSocketUrl;
        LivePriceUpdateIntervalMilliseconds = Math.Max(100, settings.LivePriceUpdateIntervalMilliseconds);
        OnChange?.Invoke();
        return Task.CompletedTask;
    }

    public async Task SaveAsync()
    {
        var settings = new BybitSettings
        {
            ApiKey = ApiKey,
            ApiSecret = ApiSecret,
            WebSocketUrl = WebSocketUrl,
            LivePriceUpdateIntervalMilliseconds = Math.Max(100, LivePriceUpdateIntervalMilliseconds)
        };

        var payload = BybitSettingsStorage.Serialize(settings);
        await _localStorageService.SetItemAsync(BybitSettingsStorage.StorageKey, payload);
        OnChange?.Invoke();
    }
}
