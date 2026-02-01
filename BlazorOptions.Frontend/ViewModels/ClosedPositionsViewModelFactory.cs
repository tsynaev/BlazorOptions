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

    public ClosedPositionsViewModel Create(PositionBuilderViewModel positionBuilder,  ITelemetryService telemetryService, PositionModel position)
    {

        var viewModel =
            new ClosedPositionsViewModel(
                positionBuilder,
                _tradingHistoryPort,
                telemetryService,
                _exchangeService);

        viewModel.Model = position.Closed;

        return viewModel;
    }
}


