using BlazorOptions.API.TradingHistory;
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
    private readonly ITradingHistoryPort _tradingHistoryPort;

    public ExchangeServiceFactory(
        ExchangeConnectionsService exchangeConnectionsService,
        HttpClient httpClient,
        ILoggerFactory loggerFactory,
        ITradingHistoryPort tradingHistoryPort)
    {
        _exchangeConnectionsService = exchangeConnectionsService;
        _httpClient = httpClient;
        _loggerFactory = loggerFactory;
        _tradingHistoryPort = tradingHistoryPort;
    }

    public IExchangeService Create(string? exchangeConnectionId)
    {
        var normalizedConnectionId = _exchangeConnectionsService.GetConnectionOrDefault(exchangeConnectionId).Id;
        var settings = _exchangeConnectionsService.GetConnectionOrDefault(normalizedConnectionId).ToBybitSettings();
        var bybitSettingsOptions = new OptionsWrapper<BybitSettings>(settings);
        return new BybitExchangeService(
            _httpClient,
            bybitSettingsOptions,
            _loggerFactory,
            _tradingHistoryPort,
            normalizedConnectionId);
    }
}
