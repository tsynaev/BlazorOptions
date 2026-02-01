using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MudBlazor;

namespace BlazorOptions.Services;

public static class ServiceCollectionDialogExtensions
{
    public static IServiceCollection AddDialog<TDialog, TViewModel>(this IServiceCollection services)
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

            _ = dialogService.ShowAsync<TDialog>(typeof(TDialog).Name, parameters, options);
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
