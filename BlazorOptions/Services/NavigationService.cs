using Microsoft.Extensions.DependencyInjection;

namespace BlazorOptions.Services;

public sealed class NavigationService : INavigationService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IDialogNavigationRegistry _registry;

    public NavigationService(
        IServiceProvider serviceProvider,
        IDialogNavigationRegistry registry)
    {
        _serviceProvider = serviceProvider;
        _registry = registry;
    }

    public async Task<TViewModel> NavigateToAsync<TViewModel>(Func<TViewModel, Task>? configure = null)
        where TViewModel : class
    {
        var viewModel = _serviceProvider.GetRequiredService<TViewModel>();

        if (configure is not null)
        {
            await configure(viewModel);
        }

        var action = _registry.GetAction(typeof(TViewModel));
        await action(viewModel, _serviceProvider);

        return viewModel;
    }
}
