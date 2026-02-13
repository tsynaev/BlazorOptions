using BlazorOptions.ViewModels;
using MudBlazor;

namespace BlazorOptions.Services;

public sealed class NotifyUserService : INotifyUserService
{
    private readonly ISnackbar _snackbar;

    public NotifyUserService(ISnackbar snackbar)
    {
        _snackbar = snackbar;
    }

    public void NotifyUser(string message)
    {
        NotifyUser(message, visibleMilliseconds: 5000);
    }

    public void NotifyUser(string message, int visibleMilliseconds)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var duration = visibleMilliseconds > 0 ? visibleMilliseconds : 5000;
        _snackbar.Add(
            message,
            Severity.Warning,
            configure => configure.VisibleStateDuration = duration);
    }
}
