using BlazorOptions.Services;
using System.Collections.ObjectModel;

namespace BlazorOptions.ViewModels;

public sealed class ClosedPositionsViewModelFactory
{
    private readonly TradingHistoryStorageService _storageService;
    private readonly IExchangeService _exchangeService;

    public ClosedPositionsViewModelFactory(
        TradingHistoryStorageService storageService,
        IExchangeService exchangeService)
    {
        _storageService = storageService;
        _exchangeService = exchangeService;
    }

    public ClosedPositionsViewModel Create(PositionBuilderViewModel positionBuilder,  ITelemetryService telemetryService, PositionModel position)
    {

        var viewModel =
            new ClosedPositionsViewModel(positionBuilder, _storageService, telemetryService, _exchangeService);

        viewModel.Model = position.Closed;

        return viewModel;
    }
}


