using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class ClosedPositionsViewModelFactory
{
    private readonly TradingHistoryStorageService _storageService;

    public ClosedPositionsViewModelFactory(
        TradingHistoryStorageService storageService)
    {
        _storageService = storageService;
    }

    public ClosedPositionsViewModel Create(PositionBuilderViewModel positionBuilder, PositionModel position)
    {
        return new ClosedPositionsViewModel(positionBuilder, _storageService, position);
    }
}


