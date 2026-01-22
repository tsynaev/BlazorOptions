using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class ClosedPositionsViewModelFactory
{
    private readonly PositionBuilderViewModel _positionBuilder;
    private readonly TradingHistoryStorageService _storageService;

    public ClosedPositionsViewModelFactory(
        PositionBuilderViewModel positionBuilder,
        TradingHistoryStorageService storageService)
    {
        _positionBuilder = positionBuilder;
        _storageService = storageService;
    }

    public ClosedPositionsViewModel Create(PositionModel position)
    {
        return new ClosedPositionsViewModel(_positionBuilder, _storageService, position);
    }
}


