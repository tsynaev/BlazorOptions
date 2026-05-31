using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public class MainLayoutViewModel : IDisposable
{
    private readonly AuthSessionService _sessionService;
    private readonly AuthApiService _authApiService;
    private readonly ITradingHistoryRealtimeMonitorService _tradingHistoryRealtimeMonitorService;

    public MainLayoutViewModel(
        AuthSessionService sessionService,
        AuthApiService authApiService,
        ITradingHistoryRealtimeMonitorService tradingHistoryRealtimeMonitorService)
    {
        _sessionService = sessionService;
        _authApiService = authApiService;
        _tradingHistoryRealtimeMonitorService = tradingHistoryRealtimeMonitorService;
        _sessionService.OnChange += HandleSessionChanged;
    }

    public event Action? OnChange;

    public string? UserName => _sessionService.UserName;

    public bool IsAuthenticated => _sessionService.IsAuthenticated;

    public async Task InitializeAsync()
    {
        await _sessionService.InitializeAsync();
        await _authApiService.ValidateSessionAsync();
        await _tradingHistoryRealtimeMonitorService.InitializeAsync();
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
