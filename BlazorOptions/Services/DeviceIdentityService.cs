namespace BlazorOptions.Services;

public class DeviceIdentityService
{
    private const string DeviceKey = "blazor-options-device-id";
    private readonly LocalStorageService _localStorageService;
    private string? _deviceId;

    public DeviceIdentityService(LocalStorageService localStorageService)
    {
        _localStorageService = localStorageService;
    }

    public async Task<string> GetDeviceIdAsync()
    {
        if (!string.IsNullOrWhiteSpace(_deviceId))
        {
            return _deviceId;
        }

        var stored = await _localStorageService.GetItemAsync(DeviceKey);
        if (string.IsNullOrWhiteSpace(stored))
        {
            stored = Guid.NewGuid().ToString("N");
            await _localStorageService.SetItemAsync(DeviceKey, stored);
        }

        _deviceId = stored;
        return stored;
    }
}
