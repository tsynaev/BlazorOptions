using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MudBlazor;

namespace BlazorOptions.Services;

public static class ServiceCollectionDialogExtensions
{
    public static IServiceCollection AddDialog<TDialog, TViewModel>(
        this IServiceCollection services,
        Func<TViewModel, string>? titleSelector = null)
        where TDialog : class, IComponent
        where TViewModel : class
    {
        services.AddTransient<TDialog>();
        return services.AddDialogAction<TViewModel>((viewModel, serviceProvider) =>
        {
            var dialogService = serviceProvider.GetRequiredService<IDialogService>();
            var parameters = new DialogParameters
            {
                ["ViewModel"] = viewModel
            };

            var options = new DialogOptions
            {
                CloseOnEscapeKey = true,
                MaxWidth = MaxWidth.Large,
                FullWidth = true
            };

            var title = titleSelector?.Invoke(viewModel);
            if (string.IsNullOrWhiteSpace(title))
            {
                title = typeof(TDialog).Name;
            }

            _ = dialogService.ShowAsync<TDialog>(title, parameters, options);
            return Task.CompletedTask;
        });
    }

    public static IServiceCollection AddDialogAction<TViewModel>(
        this IServiceCollection services,
        Func<TViewModel, IServiceProvider, Task> action)
        where TViewModel : class
    {
        services.TryAddScoped<INavigationService, NavigationService>();
        services.TryAddSingleton<IDialogNavigationRegistry, DialogNavigationRegistry>();

        services.AddTransient<TViewModel>();
        services.AddSingleton(new DialogActionRegistration(
            typeof(TViewModel),
            (viewModel, serviceProvider) => action((TViewModel)viewModel, serviceProvider)));

        return services;
    }
}
