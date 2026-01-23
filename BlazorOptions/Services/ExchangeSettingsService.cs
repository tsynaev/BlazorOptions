using BlazorOptions.ViewModels;

namespace BlazorOptions.Services;

public class ExchangeSettingsService
{
    private readonly LocalStorageService _localStorageService;

    public ExchangeSettingsService(LocalStorageService localStorageService)
    {
        _localStorageService = localStorageService;
    }

    public async Task<BybitSettings> LoadBybitSettingsAsync()
    {
        var stored = await _localStorageService.GetItemAsync(BybitSettingsStorage.StorageKey);

        if (string.IsNullOrWhiteSpace(stored))
        {
            return new BybitSettings();
        }

        return BybitSettingsStorage.TryDeserialize(stored) ?? new BybitSettings();
    }

    public Task SaveBybitSettingsAsync(BybitSettings settings)
    {
        var payload = BybitSettingsStorage.Serialize(settings);
        return _localStorageService.SetItemAsync(BybitSettingsStorage.StorageKey, payload).AsTask();
    }
}
