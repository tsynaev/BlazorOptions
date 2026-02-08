using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class LegsCollectionViewModelFactory
{
    private readonly ILegsCollectionDialogService _dialogService;
    private readonly LegViewModelFactory _legViewModelFactory;
    private readonly INotifyUserService _notifyUserService;
    private readonly IExchangeService _exchangeService;
    private readonly ILegsParserService _legsParserService;


    public LegsCollectionViewModelFactory(
        ILegsCollectionDialogService dialogService,
        LegViewModelFactory legViewModelFactory,
        INotifyUserService notifyUserService,
        IExchangeService exchangeService,
        ILegsParserService legsParserService)
    {
        _dialogService = dialogService;
        _legViewModelFactory = legViewModelFactory;
        _notifyUserService = notifyUserService;
        _exchangeService = exchangeService;
        _legsParserService = legsParserService;
    }

    public LegsCollectionViewModel Create(PositionViewModel position, LegsCollectionModel collection)
    {
        var vm = new LegsCollectionViewModel(
            _dialogService,
            _legViewModelFactory,
            _notifyUserService,
            _exchangeService,
            _legsParserService)
        {
            Position = position,
            BaseAsset = position.Position.BaseAsset,
            Collection = collection
        };

        return vm;
    }
}
