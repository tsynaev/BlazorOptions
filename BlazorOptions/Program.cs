using BlazorOptions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;
using BlazorOptions.ViewModels;
using BlazorOptions.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// register the options helper/service used by the chart page
builder.Services.AddSingleton<BlackScholes>();
builder.Services.AddSingleton<OptionsService>();
builder.Services.AddScoped<LocalStorageService>();
builder.Services.AddScoped<PositionStorageService>();
builder.Services.AddScoped<ExchangeSettingsService>();
builder.Services.AddScoped<ExchangeTickerService>();
builder.Services.AddScoped<IExchangeTickerClient, BybitTickerClient>();
builder.Services.AddScoped<OptionsChainService>();
builder.Services.AddSingleton<ThemeService>();
builder.Services.AddTransient<AccountSettingsViewModel>();
builder.Services.AddTransient<BybitSettingsViewModel>();
builder.Services.AddScoped<PositionBuilderViewModel>();
builder.Services.AddScoped<LegsCollectionViewModelFactory>();
builder.Services.AddTransient<OptionChainDialogViewModel>();
builder.Services.AddTransient<PortfolioSettingsDialogViewModel>();
builder.Services.AddMudServices();

await builder.Build().RunAsync();
