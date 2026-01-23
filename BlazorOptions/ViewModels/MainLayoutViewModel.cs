using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public class MainLayoutViewModel : IDisposable
{
    private readonly AuthSessionService _sessionService;
    private readonly AuthApiService _authApiService;
    private readonly TradingHistoryViewModel _tradingHistoryViewModel;

    public MainLayoutViewModel(AuthSessionService sessionService, AuthApiService authApiService, TradingHistoryViewModel tradingHistoryViewModel)
    {
        _sessionService = sessionService;
        _authApiService = authApiService;
        _tradingHistoryViewModel = tradingHistoryViewModel;
        _sessionService.OnChange += HandleSessionChanged;
    }

    public event Action? OnChange;

    public string? UserName => _sessionService.UserName;

    public bool IsAuthenticated => _sessionService.IsAuthenticated;

    public async Task InitializeAsync()
    {
        await _sessionService.InitializeAsync();
        await _authApiService.ValidateSessionAsync();
        await _tradingHistoryViewModel.InitializeForBackgroundAsync();
    }

    public void Dispose()
    {
        _sessionService.OnChange -= HandleSessionChanged;
    }

    private void HandleSessionChanged()
    {
        OnChange?.Invoke();
    }
}
