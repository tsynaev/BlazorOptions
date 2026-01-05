using System.Text.Json;
using BlazorOptions.ViewModels;
using Microsoft.JSInterop;

namespace BlazorOptions.Services;

public class PositionStorageService
{
    private const string StorageKey = "blazor-options-positions";
    private readonly IJSRuntime _jsRuntime;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public PositionStorageService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public async Task<List<PositionModel>> LoadPositionsAsync()
    {
        var stored = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", StorageKey);

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
        return _jsRuntime.InvokeVoidAsync("localStorage.setItem", StorageKey, payload).AsTask();
    }
}
