namespace BlazorOptions.Services;

public sealed class DeviceIdHeaderHandler : DelegatingHandler
{
    private const string HeaderName = "X-User-Device-Id";
    private readonly DeviceIdentityService _deviceIdentityService;

    public DeviceIdHeaderHandler(DeviceIdentityService deviceIdentityService)
    {
        _deviceIdentityService = deviceIdentityService;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Headers.Contains(HeaderName))
        {
            var deviceId = await _deviceIdentityService.GetDeviceIdAsync();
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                request.Headers.Add(HeaderName, deviceId);
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
