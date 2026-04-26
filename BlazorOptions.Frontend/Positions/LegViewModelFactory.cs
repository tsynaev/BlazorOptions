using BlazorOptions.Services;
using BlazorOptions;

namespace BlazorOptions.ViewModels;

public sealed class LegViewModelFactory
{
    private readonly OptionsService _optionsService;
    private readonly BlackScholes _blackScholes;

    public LegViewModelFactory(
        OptionsService optionsService,
        BlackScholes blackScholes)
    {
        _optionsService = optionsService;
        _blackScholes = blackScholes;
    }

    public LegViewModel Create(LegsCollectionViewModel collectionViewModel, LegModel leg, IExchangeService exchangeService)
    {
        var vm = new LegViewModel(
            collectionViewModel,
            _optionsService,
            exchangeService,
            _blackScholes);
        vm.Leg = leg;
        return vm;
    }
}
