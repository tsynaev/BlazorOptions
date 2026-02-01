namespace BlazorOptions.Services;

public interface INavigationService
{
    Task<TViewModel> NavigateToAsync<TViewModel>(Func<TViewModel, Task>? configure = null)
        where TViewModel : class;
}
