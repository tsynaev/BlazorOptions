using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class LegsCollectionViewModelFactory
{
    private readonly LegViewModelFactory _legViewModelFactory;
    private readonly INotifyUserService _notifyUserService;
    private readonly ExchangeSnapshotLegSyncService _exchangeSnapshotLegSyncService;
    private readonly ILegsParserService _legsParserService;


    public LegsCollectionViewModelFactory(
        LegViewModelFactory legViewModelFactory,
        INotifyUserService notifyUserService,
        ExchangeSnapshotLegSyncService exchangeSnapshotLegSyncService,
        ILegsParserService legsParserService)
    {
        _legViewModelFactory = legViewModelFactory;
        _notifyUserService = notifyUserService;
        _exchangeSnapshotLegSyncService = exchangeSnapshotLegSyncService;
        _legsParserService = legsParserService;
    }

    public LegsCollectionViewModel Create(PositionViewModel position, LegsCollectionModel collection, IExchangeService exchangeService)
    {
        var vm = new LegsCollectionViewModel(
            _legViewModelFactory,
            _notifyUserService,
            _exchangeSnapshotLegSyncService,
            exchangeService,
            _legsParserService)
        {
            Position = position,
            BaseAsset = position.Position.BaseAsset,
            Collection = collection
        };

        return vm;
    }
}
