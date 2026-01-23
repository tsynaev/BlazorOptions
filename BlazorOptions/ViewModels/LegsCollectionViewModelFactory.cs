using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class LegsCollectionViewModelFactory
{
    private readonly OptionsChainService _optionsChainService;
    private readonly ILegsCollectionDialogService _dialogService;
    private readonly LegViewModelFactory _legViewModelFactory;
    private readonly INotifyUserService _notifyUserService;


    public LegsCollectionViewModelFactory(
        OptionsChainService optionsChainService,
        ILegsCollectionDialogService dialogService,
        LegViewModelFactory legViewModelFactory,
        INotifyUserService notifyUserService)
    {
        _optionsChainService = optionsChainService;
        _dialogService = dialogService;;
        _legViewModelFactory = legViewModelFactory;
        _notifyUserService = notifyUserService;
    }

    public LegsCollectionViewModel Create(PositionViewModel position, LegsCollectionModel collection)
    {
        var vm = new LegsCollectionViewModel(_dialogService, _optionsChainService, _legViewModelFactory, _notifyUserService)
        {
            Position = position,
            BaseAsset = position.Position.BaseAsset,
            Collection = collection
        };

        return vm;
    }
}
