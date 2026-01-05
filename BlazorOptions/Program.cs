using BlazorOptions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;
using BlazorOptions.ViewModels;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// register the options helper/service used by the chart page
builder.Services.AddSingleton<OptionsService>();
builder.Services.AddTransient<PositionBuilderViewModel>();
builder.Services.AddMudServices();

await builder.Build().RunAsync();
