using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class LegsCollectionViewModelFactory
{
    private readonly PositionBuilderViewModel _positionBuilder;
    private readonly OptionsChainService _optionsChainService;
    private readonly ILegsCollectionDialogService _dialogService;


    public LegsCollectionViewModelFactory(
        PositionBuilderViewModel positionBuilder,
        OptionsChainService optionsChainService,
        ILegsCollectionDialogService dialogService)
    {
        _positionBuilder = positionBuilder;
        _optionsChainService = optionsChainService;
        _dialogService = dialogService;;
    }

    public LegsCollectionViewModel Create()
    {
        var vm = new LegsCollectionViewModel(_positionBuilder, _dialogService, _optionsChainService);
        //TODO: set BaseAsset in PositionViewModel 
        vm.BaseAsset = _positionBuilder.SelectedPosition?.BaseAsset;

        return vm;
    }
}
