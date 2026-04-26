using Microsoft.Extensions.Options;
using BlazorOptions.ViewModels;

namespace BlazorOptions.Services;

public sealed class LocalStorageBybitSettingsOptions : IOptions<BybitSettings>
{
    private readonly ExchangeConnectionsService _exchangeConnectionsService;

    public LocalStorageBybitSettingsOptions(ExchangeConnectionsService exchangeConnectionsService)
    {
        _exchangeConnectionsService = exchangeConnectionsService;
    }

    public BybitSettings Value
    {
        get
        {
            return _exchangeConnectionsService
                .GetConnectionOrDefault(null)
                .ToBybitSettings();
        }
    }
}
