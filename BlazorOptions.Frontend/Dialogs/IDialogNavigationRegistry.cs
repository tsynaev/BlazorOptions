namespace BlazorOptions.Services;

public interface IDialogNavigationRegistry
{
    Func<object, IServiceProvider, Task> GetAction(Type viewModelType);
}
