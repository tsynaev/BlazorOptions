using Microsoft.Extensions.Options;
using BlazorOptions.ViewModels;

namespace BlazorOptions.Services;

public sealed class LocalStorageBybitSettingsOptions : IOptions<BybitSettings>
{
    private readonly ILocalStorageService _localStorageService;

    public LocalStorageBybitSettingsOptions(ILocalStorageService localStorageService)
    {
        _localStorageService = localStorageService;
    }

    public BybitSettings Value
    {
        get
        {
            var stored = _localStorageService.GetItem(BybitSettingsStorage.StorageKey);
            var settings = BybitSettingsStorage.TryDeserialize(stored);
            return settings ?? new BybitSettings();
        }
    }
}
