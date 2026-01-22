using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class ClosedPositionsViewModelFactory
{
    private readonly PositionBuilderViewModel _positionBuilder;

    public ClosedPositionsViewModelFactory(PositionBuilderViewModel positionBuilder)
    {
        _positionBuilder = positionBuilder;
    }

    public ClosedPositionsViewModel Create(PositionModel position)
    {
        return new ClosedPositionsViewModel(_positionBuilder, position);
    }
}


