namespace BlazorOptions.Services;

public interface ILocalStorageService
{
    ValueTask<string?> GetItemAsync(string key);
    string? GetItem(string key);
    ValueTask SetItemAsync(string key, string value);
}
