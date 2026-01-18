namespace BlazorOptions.Services;

public class AuthSessionService
{
    private const string TokenKey = "blazor-options-auth-token";
    private const string UserKey = "blazor-options-auth-user";
    private readonly LocalStorageService _localStorageService;
    private bool _initialized;

    public AuthSessionService(LocalStorageService localStorageService)
    {
        _localStorageService = localStorageService;
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

        Token = await _localStorageService.GetItemAsync(TokenKey);
        UserName = await _localStorageService.GetItemAsync(UserKey);
        _initialized = true;
        OnChange?.Invoke();
    }

    public async Task SetSessionAsync(string userName, string token)
    {
        Token = token;
        UserName = userName;
        await _localStorageService.SetItemAsync(TokenKey, token);
        await _localStorageService.SetItemAsync(UserKey, userName);
        OnChange?.Invoke();
    }

    public async Task ClearAsync()
    {
        Token = null;
        UserName = null;
        await _localStorageService.SetItemAsync(TokenKey, string.Empty);
        await _localStorageService.SetItemAsync(UserKey, string.Empty);
        OnChange?.Invoke();
    }
}
