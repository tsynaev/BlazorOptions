using BlazorOptions.Services;
using Microsoft.Extensions.Options;

namespace BlazorOptions.ViewModels;

public class AccountSettingsViewModel : IDisposable
{
    private readonly AuthApiService _authApiService;
    private readonly AuthSessionService _sessionService;
    private readonly ILocalStorageService _localStorageService;
    private readonly IOptions<BybitSettings> _bybitSettingsOptions;

    public AccountSettingsViewModel(
        AuthApiService authApiService,
        AuthSessionService sessionService,
        ILocalStorageService localStorageService,
        IOptions<BybitSettings> bybitSettingsOptions)
    {
        _authApiService = authApiService;
        _sessionService = sessionService;
        _localStorageService = localStorageService;
        _bybitSettingsOptions = bybitSettingsOptions;
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

    public string ApiKey { get; set; } = string.Empty;

    public string ApiSecret { get; set; } = string.Empty;

    public string WebSocketUrl { get; set; } = "wss://stream.bybit.com/v5/public/linear";

    public int LivePriceUpdateIntervalMilliseconds { get; set; } = 1000;

    public string OptionBaseCoins { get; set; } = "BTC, ETH, SOL";

    public string OptionQuoteCoins { get; set; } = "USDT";

    public async Task InitializeAsync()
    {
        await _sessionService.InitializeAsync();
        await LoadRiskSettingsAsync();
        await LoadBybitSettingsAsync();
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

    public async Task SaveBybitSettingsAsync()
    {
        var settings = new BybitSettings
        {
            ApiKey = ApiKey,
            ApiSecret = ApiSecret,
            WebSocketUrl = WebSocketUrl,
            LivePriceUpdateIntervalMilliseconds = Math.Max(100, LivePriceUpdateIntervalMilliseconds),
            OptionBaseCoins = OptionBaseCoins,
            OptionQuoteCoins = OptionQuoteCoins
        };

        var payload = BybitSettingsStorage.Serialize(settings);
        await _localStorageService.SetItemAsync(BybitSettingsStorage.StorageKey, payload);
        OnChange?.Invoke();
    }

    private Task LoadBybitSettingsAsync()
    {
        var settings = _bybitSettingsOptions.Value;
        ApiKey = settings.ApiKey;
        ApiSecret = settings.ApiSecret;
        WebSocketUrl = settings.WebSocketUrl;
        LivePriceUpdateIntervalMilliseconds = Math.Max(100, settings.LivePriceUpdateIntervalMilliseconds);
        OptionBaseCoins = string.IsNullOrWhiteSpace(settings.OptionBaseCoins) ? "BTC, ETH, SOL" : settings.OptionBaseCoins;
        OptionQuoteCoins = string.IsNullOrWhiteSpace(settings.OptionQuoteCoins) ? "USDT" : settings.OptionQuoteCoins;
        OnChange?.Invoke();
        return Task.CompletedTask;
    }
}
