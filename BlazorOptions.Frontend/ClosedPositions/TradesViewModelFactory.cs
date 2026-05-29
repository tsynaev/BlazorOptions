using BlazorOptions.API.TradingHistory;

namespace BlazorOptions.ViewModels;

public sealed class TradesViewModelFactory
{
    private readonly ITradingHistoryPort _tradingHistoryPort;

    public TradesViewModelFactory(ITradingHistoryPort tradingHistoryPort)
    {
        _tradingHistoryPort = tradingHistoryPort;
    }

    public TradesViewModel Create(ClosedPositionsViewModel closedPositionsViewModel)
    {
        return new TradesViewModel(closedPositionsViewModel, _tradingHistoryPort);
    }
}
