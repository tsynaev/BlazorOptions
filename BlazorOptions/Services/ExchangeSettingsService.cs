using System.Text.Json;
using BlazorOptions.ViewModels;

namespace BlazorOptions.Services;

public class ExchangeSettingsService
{
    private const string BybitStorageKey = "blazor-options-bybit-settings";
    private readonly LocalStorageService _localStorageService;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ExchangeSettingsService(LocalStorageService localStorageService)
    {
        _localStorageService = localStorageService;
    }

    public async Task<BybitSettings> LoadBybitSettingsAsync()
    {
        var stored = await _localStorageService.GetItemAsync(BybitStorageKey);

        if (string.IsNullOrWhiteSpace(stored))
        {
            return new BybitSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<BybitSettings>(stored, _serializerOptions) ?? new BybitSettings();
        }
        catch
        {
            return new BybitSettings();
        }
    }

    public Task SaveBybitSettingsAsync(BybitSettings settings)
    {
        var payload = JsonSerializer.Serialize(settings, _serializerOptions);
        return _localStorageService.SetItemAsync(BybitStorageKey, payload).AsTask();
    }
}
