using BlazorOptions.API.TradingHistory;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class ClosedPositionsViewModelFactory
{
    private readonly ITradingHistoryPort _tradingHistoryPort;
    private readonly IExchangeService _exchangeService;

    public ClosedPositionsViewModelFactory(
        ITradingHistoryPort tradingHistoryPort,
        IExchangeService exchangeService)
    {
        _tradingHistoryPort = tradingHistoryPort;
        _exchangeService = exchangeService;
    }

    public ClosedPositionsViewModel Create(PositionViewModel positionViewModel, PositionModel position)
    {

        var viewModel =
            new ClosedPositionsViewModel(
                positionViewModel,
                _tradingHistoryPort,
                _exchangeService);

        viewModel.Model = position.Closed;
        viewModel.BaseAsset = position.BaseAsset;

        return viewModel;
    }
}


