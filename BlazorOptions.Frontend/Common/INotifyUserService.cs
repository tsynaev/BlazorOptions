namespace BlazorOptions.ViewModels;

public interface INotifyUserService
{

    void NotifyUser(string message);

    void NotifyUser(string message, int visibleMilliseconds);
}
