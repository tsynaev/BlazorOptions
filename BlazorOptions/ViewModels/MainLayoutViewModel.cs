using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public class MainLayoutViewModel : IDisposable
{
    private readonly AuthSessionService _sessionService;
    private readonly AuthApiService _authApiService;

    public MainLayoutViewModel(AuthSessionService sessionService, AuthApiService authApiService)
    {
        _sessionService = sessionService;
        _authApiService = authApiService;
        _sessionService.OnChange += HandleSessionChanged;
    }

    public event Action? OnChange;

    public string? UserName => _sessionService.UserName;

    public bool IsAuthenticated => _sessionService.IsAuthenticated;

    public async Task InitializeAsync()
    {
        await _sessionService.InitializeAsync();
        await _authApiService.ValidateSessionAsync();
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
