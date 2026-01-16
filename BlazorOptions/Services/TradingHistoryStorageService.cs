using System.Text.Json;
using BlazorOptions.ViewModels;

namespace BlazorOptions.Services;

public class TradingHistoryStorageService
{
    private const string StorageKey = "blazor-options-trading-history";
    private readonly LocalStorageService _localStorageService;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public TradingHistoryStorageService(LocalStorageService localStorageService)
    {
        _localStorageService = localStorageService;
    }

    public async Task<TradingHistoryState> LoadStateAsync()
    {
        var stored = await _localStorageService.GetItemAsync(StorageKey);

        if (string.IsNullOrWhiteSpace(stored))
        {
            return new TradingHistoryState();
        }

        try
        {
            return JsonSerializer.Deserialize<TradingHistoryState>(stored, _serializerOptions)
                   ?? new TradingHistoryState();
        }
        catch
        {
            return new TradingHistoryState();
        }
    }

    public Task SaveStateAsync(TradingHistoryState state)
    {
        var payload = JsonSerializer.Serialize(state, _serializerOptions);
        return _localStorageService.SetItemAsync(StorageKey, payload).AsTask();
    }

}
