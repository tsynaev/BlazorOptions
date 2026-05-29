namespace BlazorOptions.ViewModels;

public sealed class ClosedPositionsViewModelFactory
{
    public ClosedPositionsViewModelFactory()
    {
    }

    public ClosedPositionsViewModel Create(PositionViewModel positionViewModel, PositionModel position)
    {
        var viewModel = new ClosedPositionsViewModel(positionViewModel);

        viewModel.Model = position.Closed;
        viewModel.BaseAsset = position.BaseAsset;

        return viewModel;
    }
}


