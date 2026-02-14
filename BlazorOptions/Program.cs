using BlazorOptions;
using BlazorOptions.API.TradingHistory;
using BlazorOptions.Shared;
using BlazorOptions.API.Positions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using BlazorOptions.ViewModels;
using BlazorOptions.Services;
using BlazorOptions.Diagnostics;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);

builder.Services.AddScoped<DeviceIdHeaderHandler>();
builder.Services.AddHttpClient("App", client =>
{
    client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress);
}).AddHttpMessageHandler<DeviceIdHeaderHandler>();
builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("App"));

// register the options helper/service used by the chart page
builder.Services.AddSingleton<BlackScholes>();
builder.Services.AddSingleton<TelemetryService>();
builder.Services.AddSingleton<OptionsService>();
builder.Services.AddScoped<ILocalStorageService, LocalStorageService>();
builder.Services.AddScoped<DeviceIdentityService>();
builder.Services.Configure<AuthSessionOptions>(builder.Configuration.GetSection(AuthSessionOptions.SectionName));
builder.Services.AddScoped<LocalStorageAuthSessionOptions>();
builder.Services.AddScoped<Microsoft.Extensions.Options.IOptions<AuthSessionState>>(sp => sp.GetRequiredService<LocalStorageAuthSessionOptions>());
builder.Services.AddScoped<AuthSessionService>();
builder.Services.AddScoped<AuthApiService>();
builder.Services.AddScoped<PositionsPort>();
builder.Services.AddScoped<IPositionsPort>(sp => sp.GetRequiredService<PositionsPort>());
builder.Services.AddScoped<BybitPositionService>();
builder.Services.AddScoped<BybitOrderService>();
builder.Services.AddScoped<ActiveOrdersService>();
builder.Services.AddScoped<ActivePositionsService>();
builder.Services.AddScoped<IActivePositionsService>(sp => sp.GetRequiredService<ActivePositionsService>());
builder.Services.AddScoped<IPositionsService>(sp => sp.GetRequiredService<ActivePositionsService>());
builder.Services.AddScoped<IOrdersService>(sp => sp.GetRequiredService<ActiveOrdersService>());
builder.Services.AddScoped<BybitTransactionService>();
builder.Services.AddScoped<TradingHistoryPort>();
builder.Services.AddScoped<ITradingHistoryPort>(sp => sp.GetRequiredService<TradingHistoryPort>());
builder.Services.AddScoped<ExchangeTickerService>();
builder.Services.AddScoped<ITickersService>(sp => sp.GetRequiredService<ExchangeTickerService>());
builder.Services.AddScoped<IExchangeService, ExchangeService>();
builder.Services.AddScoped<IExchangeTickerClient, BybitTickerClient>();
builder.Services.AddScoped<OptionsChainService>();
builder.Services.AddScoped<IOptionsChainService>(sp => sp.GetRequiredService<OptionsChainService>());
builder.Services.AddScoped<FuturesInstrumentsService>();
builder.Services.AddScoped<IFuturesInstrumentsService>(sp => sp.GetRequiredService<FuturesInstrumentsService>());
builder.Services.AddScoped<ILegsParserService, LegsParserService>();
builder.Services.AddScoped<IOptions<BybitSettings>, LocalStorageBybitSettingsOptions>();
builder.Services.AddScoped<MainLayoutViewModel>();
builder.Services.AddTransient<AccountSettingsViewModel>();
builder.Services.AddTransient<BybitSettingsViewModel>();
builder.Services.AddScoped<HomeDashboardViewModel>();
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
builder.Services.AddScoped<VolumeHeatmapViewModel>();
builder.Services.AddDialog<PositionCreateDialog, PositionCreateDialogViewModel>();
builder.Services.AddDialog<TradingSymbolDialog, TradingSymbolDialogViewModel>();
builder.Services.AddDialog<TradingHistorySelectionDialog, TradingHistorySelectionDialogViewModel>();
builder.Services.AddMudServices();

var host = builder.Build();
var storedTelemetry = await host.Services.GetRequiredService<ILocalStorageService>()
    .GetItemAsync(TelemetryOptions.StorageKey);
if (TelemetryOptions.IsEnabled(storedTelemetry))
{
    host.Services.GetRequiredService<TelemetryService>();
}

await host.RunAsync();
