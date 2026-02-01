namespace BlazorOptions.Services;

public class AuthSessionService
{
    private readonly ILocalStorageService _localStorageService;
    private readonly AuthSessionOptions _options;
    private readonly Microsoft.Extensions.Options.IOptions<AuthSessionState> _stateOptions;
    private bool _initialized;

    public AuthSessionService(
        ILocalStorageService localStorageService,
        Microsoft.Extensions.Options.IOptions<AuthSessionOptions> options,
        Microsoft.Extensions.Options.IOptions<AuthSessionState> stateOptions)
    {
        _localStorageService = localStorageService;
        _options = options.Value;
        _stateOptions = stateOptions;
    }

    public string? Token { get; private set; }

    public string? UserName { get; private set; }

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(Token);

    public event Action? OnChange;

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        var state = _stateOptions.Value;
        Token = state.Token;
        UserName = state.UserName;
        _initialized = true;
        OnChange?.Invoke();
    }

    public async Task SetSessionAsync(string userName, string token)
    {
        Token = token;
        UserName = userName;
        await _localStorageService.SetItemAsync(_options.TokenKey, token);
        await _localStorageService.SetItemAsync(_options.UserKey, userName);
        OnChange?.Invoke();
    }

    public async Task ClearAsync()
    {
        Token = null;
        UserName = null;
        await _localStorageService.SetItemAsync(_options.TokenKey, string.Empty);
        await _localStorageService.SetItemAsync(_options.UserKey, string.Empty);
        OnChange?.Invoke();
    }
}
