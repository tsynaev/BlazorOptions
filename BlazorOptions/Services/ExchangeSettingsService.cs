using System.Text.Json;
using BlazorOptions.ViewModels;
using Microsoft.JSInterop;

namespace BlazorOptions.Services;

public class ExchangeSettingsService
{
    private const string BybitStorageKey = "blazor-options-bybit-settings";
    private readonly IJSRuntime _jsRuntime;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ExchangeSettingsService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public async Task<BybitSettings> LoadBybitSettingsAsync()
    {
        var stored = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", BybitStorageKey);

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
        return _jsRuntime.InvokeVoidAsync("localStorage.setItem", BybitStorageKey, payload).AsTask();
    }
}
