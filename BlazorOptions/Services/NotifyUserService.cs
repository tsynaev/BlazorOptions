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
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _snackbar.Add(message, Severity.Warning);
    }
}
