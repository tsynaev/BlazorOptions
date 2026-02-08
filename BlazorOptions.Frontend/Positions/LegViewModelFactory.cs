using BlazorOptions.Services;
using BlazorOptions;

namespace BlazorOptions.ViewModels;

public sealed class LegViewModelFactory
{
    private readonly OptionsService _optionsService;
    private readonly IExchangeService _exchangeService;
    private readonly BlackScholes _blackScholes;

    public LegViewModelFactory(
        OptionsService optionsService,
        IExchangeService exchangeService,
        BlackScholes blackScholes)
    {
        _optionsService = optionsService;
        _exchangeService = exchangeService;
        _blackScholes = blackScholes;
    }

    public LegViewModel Create(LegsCollectionViewModel collectionViewModel, LegModel leg)
    {
        var vm = new LegViewModel(
            collectionViewModel,
            _optionsService,
            _exchangeService,
            _blackScholes);
        vm.Leg = leg;
        return vm;
    }
}
