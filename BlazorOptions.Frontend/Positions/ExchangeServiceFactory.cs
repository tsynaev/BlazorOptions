using BlazorOptions.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlazorOptions.Services;

public interface IExchangeServiceFactory
{
    IExchangeService Create(string? exchangeConnectionId);
}

public sealed class ExchangeServiceFactory : IExchangeServiceFactory
{
    private readonly ExchangeConnectionsService _exchangeConnectionsService;
    private readonly HttpClient _httpClient;
    private readonly ILoggerFactory _loggerFactory;

    public ExchangeServiceFactory(
        ExchangeConnectionsService exchangeConnectionsService,
        HttpClient httpClient,
        ILoggerFactory loggerFactory)
    {
        _exchangeConnectionsService = exchangeConnectionsService;
        _httpClient = httpClient;
        _loggerFactory = loggerFactory;
    }

    public IExchangeService Create(string? exchangeConnectionId)
    {
        var settings = _exchangeConnectionsService.GetConnectionOrDefault(exchangeConnectionId).ToBybitSettings();
        var bybitSettingsOptions = new OptionsWrapper<BybitSettings>(settings);
        return new BybitExchangeService(
            _httpClient,
            bybitSettingsOptions,
            _loggerFactory);
    }
}
