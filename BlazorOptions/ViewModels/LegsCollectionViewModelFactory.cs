using MudBlazor;

namespace BlazorOptions.ViewModels;

public sealed class LegsCollectionViewModelFactory
{
    private readonly PositionBuilderViewModel _positionBuilder;
    private readonly IDialogService _dialogService;

    public LegsCollectionViewModelFactory(PositionBuilderViewModel positionBuilder, IDialogService dialogService)
    {
        _positionBuilder = positionBuilder;
        _dialogService = dialogService;
    }

    public LegsCollectionViewModel Create(LegsCollectionModel collection)
    {
        return new LegsCollectionViewModel(_positionBuilder, _dialogService, collection);
    }
}
