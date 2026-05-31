using BlazorOptions.ViewModels;

namespace BlazorOptions.Services;

public interface ITradingHistoryRealtimeMonitorService : IAsyncDisposable
{
    Task InitializeAsync();
}

public sealed class TradingHistoryRealtimeMonitorService : ITradingHistoryRealtimeMonitorService
{
    private readonly IExchangeServiceFactory _exchangeServiceFactory;
    private readonly ExchangeConnectionsService _exchangeConnectionsService;
    private readonly List<IExchangeService> _exchangeServices = new();
    private bool _isInitialized;

    public TradingHistoryRealtimeMonitorService(
        IExchangeServiceFactory exchangeServiceFactory,
        ExchangeConnectionsService exchangeConnectionsService)
    {
        _exchangeServiceFactory = exchangeServiceFactory;
        _exchangeConnectionsService = exchangeConnectionsService;
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        foreach (var connection in _exchangeConnectionsService.GetConnections())
        {
            var exchangeService = _exchangeServiceFactory.Create(connection.Id);
            _exchangeServices.Add(exchangeService);
            await exchangeService.TransactionHistory.InitializeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var exchangeService in _exchangeServices)
        {
            await exchangeService.DisposeAsync();
        }

        _exchangeServices.Clear();
    }
}
