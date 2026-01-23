using BlazorOptions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;
using BlazorOptions.ViewModels;
using BlazorOptions.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped<DeviceIdHeaderHandler>();
builder.Services.AddHttpClient("App", client =>
{
    client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress);
}).AddHttpMessageHandler<DeviceIdHeaderHandler>();
builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("App"));

// register the options helper/service used by the chart page
builder.Services.AddSingleton<BlackScholes>();
builder.Services.AddSingleton<OptionsService>();
builder.Services.AddScoped<LocalStorageService>();
builder.Services.AddScoped<DeviceIdentityService>();
builder.Services.AddScoped<AuthSessionService>();
builder.Services.AddScoped<AuthApiService>();
builder.Services.AddScoped<PositionStorageService>();
builder.Services.AddScoped<PositionSyncOutboxService>();
builder.Services.AddScoped<PositionSyncService>();
builder.Services.AddScoped<ExchangeSettingsService>();
builder.Services.AddScoped<BybitPositionService>();
builder.Services.AddScoped<ActivePositionsService>();
builder.Services.AddScoped<BybitTransactionService>();
builder.Services.AddScoped<TradingHistoryStorageService>();
builder.Services.AddScoped<ExchangeTickerService>();
builder.Services.AddScoped<IExchangeTickerClient, BybitTickerClient>();
builder.Services.AddScoped<OptionsChainService>();
builder.Services.AddOptions<BybitSettings>()
    .Configure<IServiceProvider>((options, serviceProvider) =>
    {
        using var scope = serviceProvider.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<LocalStorageService>();
        var stored = storage.GetItem(BybitSettingsStorage.StorageKey);
        var settings = BybitSettingsStorage.TryDeserialize(stored);
        if (settings is null)
        {
            return;
        }

        options.ApiKey = settings.ApiKey;
        options.ApiSecret = settings.ApiSecret;
        options.WebSocketUrl = settings.WebSocketUrl;
        options.LivePriceUpdateIntervalMilliseconds = settings.LivePriceUpdateIntervalMilliseconds;
    });
builder.Services.AddSingleton<ThemeService>();
builder.Services.AddScoped<MainLayoutViewModel>();
builder.Services.AddTransient<AccountSettingsViewModel>();
builder.Services.AddTransient<BybitSettingsViewModel>();
builder.Services.AddScoped<PositionBuilderViewModel>();
builder.Services.AddScoped<INotifyUserService, NotifyUserService>();
builder.Services.AddScoped<LegsCollectionViewModelFactory>();
builder.Services.AddTransient<LegViewModelFactory>();
builder.Services.AddScoped<ILegsCollectionDialogService, LegsCollectionDialogService>();
builder.Services.AddScoped<ClosedPositionsViewModelFactory>();
builder.Services.AddTransient<ActivePositionsPanelViewModel>();
builder.Services.AddTransient<OptionChainDialogViewModel>();
builder.Services.AddTransient<PortfolioSettingsDialogViewModel>();
builder.Services.AddScoped<TradingHistoryViewModel>();
builder.Services.AddMudServices();

await builder.Build().RunAsync();
