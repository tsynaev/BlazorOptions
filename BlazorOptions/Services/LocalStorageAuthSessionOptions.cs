using Microsoft.Extensions.Options;

namespace BlazorOptions.Services;

public sealed class LocalStorageAuthSessionOptions : IOptions<AuthSessionState>
{
    private readonly LocalStorageService _localStorageService;
    private readonly AuthSessionOptions _options;

    public LocalStorageAuthSessionOptions(
        LocalStorageService localStorageService,
        IOptions<AuthSessionOptions> options)
    {
        _localStorageService = localStorageService;
        _options = options.Value;
    }

    public AuthSessionState Value
    {
        get
        {
            return new AuthSessionState
            {
                Token = _localStorageService.GetItem(_options.TokenKey),
                UserName = _localStorageService.GetItem(_options.UserKey)
            };
        }
    }
}
