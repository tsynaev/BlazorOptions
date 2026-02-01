using Microsoft.JSInterop;

namespace BlazorOptions.Services;

public class LocalStorageService : ILocalStorageService
{
    private readonly IJSRuntime _jsRuntime;

    public LocalStorageService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public ValueTask<string?> GetItemAsync(string key)
    {
        return _jsRuntime.InvokeAsync<string?>("localStorage.getItem", key);
    }

    public string? GetItem(string key)
    {
        if (_jsRuntime is IJSInProcessRuntime syncRuntime)
        {
            return syncRuntime.Invoke<string?>("localStorage.getItem", key);
        }

        return null;
    }

    public ValueTask SetItemAsync(string key, string value)
    {
        return _jsRuntime.InvokeVoidAsync("localStorage.setItem", key, value);
    }
}
