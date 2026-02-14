using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public class AccountSettingsViewModel : IDisposable
{
    private readonly AuthApiService _authApiService;
    private readonly AuthSessionService _sessionService;
    private readonly ILocalStorageService _localStorageService;

    public AccountSettingsViewModel(
        AuthApiService authApiService,
        AuthSessionService sessionService,
        ILocalStorageService localStorageService)
    {
        _authApiService = authApiService;
        _sessionService = sessionService;
        _localStorageService = localStorageService;
        _sessionService.OnChange += HandleSessionChanged;
    }

    public event Action? OnChange;

    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public bool IsAuthBusy { get; private set; }

    public string? AuthError { get; private set; }

    public bool IsAuthenticated => _sessionService.IsAuthenticated;

    public string CurrentUserName => _sessionService.UserName ?? string.Empty;

    public string AuthStatusLabel => IsAuthenticated
        ? $"Signed in as {_sessionService.UserName}"
        : "Not signed in";

    public decimal MaxLossOptionPercent { get; set; } = 30m;

    public decimal MaxLossFuturesPercent { get; set; } = 30m;

    public async Task InitializeAsync()
    {
        await _sessionService.InitializeAsync();
        await LoadRiskSettingsAsync();
    }

    public async Task RegisterAsync()
    {
        await RunAuthAsync(_authApiService.RegisterAsync);
    }

    public async Task LoginAsync()
    {
        await RunAuthAsync(_authApiService.LoginAsync);
    }

    public async Task LogoutAsync()
    {
        if (IsAuthBusy)
        {
            return;
        }

        IsAuthBusy = true;
        AuthError = null;
        OnChange?.Invoke();

        try
        {
            await _authApiService.LogoutAsync();
        }
        finally
        {
            IsAuthBusy = false;
            OnChange?.Invoke();
        }
    }

    public void Dispose()
    {
        _sessionService.OnChange -= HandleSessionChanged;
    }

    private void HandleSessionChanged()
    {
        OnChange?.Invoke();
    }

    private async Task RunAuthAsync(Func<string, string, Task<(bool Success, string? Error)>> authAction)
    {
        if (IsAuthBusy)
        {
            return;
        }

        var userName = UserName?.Trim() ?? string.Empty;
        var password = Password ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            AuthError = "Enter both user name and password.";
            OnChange?.Invoke();
            return;
        }

        IsAuthBusy = true;
        AuthError = null;
        OnChange?.Invoke();

        try
        {
            var (success, error) = await authAction(userName, password);
            if (!success)
            {
                AuthError = error ?? "Request failed.";
            }
            else
            {
                Password = string.Empty;
                AuthError = null;
            }
        }
        finally
        {
            IsAuthBusy = false;
            OnChange?.Invoke();
        }
    }

    public async Task SaveRiskSettingsAsync()
    {
        MaxLossOptionPercent = Math.Max(1m, MaxLossOptionPercent);
        MaxLossFuturesPercent = Math.Max(1m, MaxLossFuturesPercent);

        var payload = AccountRiskSettingsStorage.Serialize(new AccountRiskSettings
        {
            MaxLossOptionPercent = MaxLossOptionPercent,
            MaxLossFuturesPercent = MaxLossFuturesPercent
        });

        await _localStorageService.SetItemAsync(AccountRiskSettingsStorage.StorageKey, payload);
        OnChange?.Invoke();
    }

    private async Task LoadRiskSettingsAsync()
    {
        var payload = await _localStorageService.GetItemAsync(AccountRiskSettingsStorage.StorageKey);
        var settings = AccountRiskSettingsStorage.Parse(payload);
        MaxLossOptionPercent = Math.Max(1m, settings.MaxLossOptionPercent);
        MaxLossFuturesPercent = Math.Max(1m, settings.MaxLossFuturesPercent);
        OnChange?.Invoke();
    }
}
