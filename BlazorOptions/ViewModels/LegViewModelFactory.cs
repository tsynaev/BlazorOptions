using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class LegViewModelFactory
{
    private readonly OptionsService _optionsService;
    private readonly OptionsChainService _optionsChainService;

    public LegViewModelFactory(OptionsService optionsService, OptionsChainService optionsChainService)
    {
        _optionsService = optionsService;
        _optionsChainService = optionsChainService;
    }

    public LegViewModel Create(LegsCollectionViewModel collectionViewModel, LegModel leg)
    {
        var vm = new LegViewModel(collectionViewModel, _optionsService, _optionsChainService);
        vm.Leg = leg;
        return vm;
    }
}
