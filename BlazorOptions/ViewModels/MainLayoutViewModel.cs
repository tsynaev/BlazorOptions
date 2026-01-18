using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public class MainLayoutViewModel : IDisposable
{
    private readonly AuthSessionService _sessionService;

    public MainLayoutViewModel(AuthSessionService sessionService)
    {
        _sessionService = sessionService;
        _sessionService.OnChange += HandleSessionChanged;
    }

    public event Action? OnChange;

    public string? UserName => _sessionService.UserName;

    public bool IsAuthenticated => _sessionService.IsAuthenticated;

    public Task InitializeAsync()
    {
        return _sessionService.InitializeAsync();
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
