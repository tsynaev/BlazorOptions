using BlazorOptions.API.TradingHistory;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class ClosedPositionsViewModelFactory
{
    private readonly ITradingHistoryPort _tradingHistoryPort;

    public ClosedPositionsViewModelFactory(ITradingHistoryPort tradingHistoryPort)
    {
        _tradingHistoryPort = tradingHistoryPort;
    }

    public ClosedPositionsViewModel Create(PositionViewModel positionViewModel, PositionModel position, IExchangeService exchangeService)
    {

        var viewModel =
            new ClosedPositionsViewModel(
                positionViewModel,
                _tradingHistoryPort,
                exchangeService);

        viewModel.Model = position.Closed;
        viewModel.BaseAsset = position.BaseAsset;
        viewModel.PositionCreationTimeUtc = position.CreationTimeUtc;

        return viewModel;
    }
}


