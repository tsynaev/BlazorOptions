using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public class BybitSettingsViewModel
{
    private readonly ExchangeSettingsService _settingsService;

    public BybitSettingsViewModel(ExchangeSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public string ApiKey { get; set; } = string.Empty;

    public string ApiSecret { get; set; } = string.Empty;

    public string WebSocketUrl { get; set; } = "wss://stream.bybit.com/v5/public/linear";

    public event Action? OnChange;

    public async Task LoadAsync()
    {
        var settings = await _settingsService.LoadBybitSettingsAsync();
        ApiKey = settings.ApiKey;
        ApiSecret = settings.ApiSecret;
        WebSocketUrl = settings.WebSocketUrl;
        OnChange?.Invoke();
    }

    public async Task SaveAsync()
    {
        var settings = new BybitSettings
        {
            ApiKey = ApiKey,
            ApiSecret = ApiSecret,
            WebSocketUrl = WebSocketUrl
        };

        await _settingsService.SaveBybitSettingsAsync(settings);
        OnChange?.Invoke();
    }
}
