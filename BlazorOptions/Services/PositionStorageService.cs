using System.Text.Json;
using BlazorOptions.ViewModels;

namespace BlazorOptions.Services;

public class PositionStorageService
{
    private const string StorageKey = "blazor-options-positions";
    private readonly LocalStorageService _localStorageService;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public PositionStorageService(LocalStorageService localStorageService)
    {
        _localStorageService = localStorageService;
    }

    public async Task<List<PositionModel>> LoadPositionsAsync()
    {
        var stored = await _localStorageService.GetItemAsync(StorageKey);

        if (string.IsNullOrWhiteSpace(stored))
        {
            return new List<PositionModel>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<PositionModel>>(stored, _serializerOptions) ?? new List<PositionModel>();
        }
        catch
        {
            return new List<PositionModel>();
        }
    }

    public Task SavePositionsAsync(IEnumerable<PositionModel> positions)
    {
        var payload = JsonSerializer.Serialize(positions, _serializerOptions);
        return _localStorageService.SetItemAsync(StorageKey, payload).AsTask();
    }
}
