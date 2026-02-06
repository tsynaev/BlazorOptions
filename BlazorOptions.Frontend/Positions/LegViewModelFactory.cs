using BlazorOptions.Services;
using BlazorOptions;

namespace BlazorOptions.ViewModels;

public sealed class LegViewModelFactory
{
    private readonly OptionsService _optionsService;
    private readonly OptionsChainService _optionsChainService;
    private readonly ExchangeTickerService _exchangeTickerService;
    private readonly IExchangeService _exchangeService;
    private readonly BlackScholes _blackScholes;

    public LegViewModelFactory(
        OptionsService optionsService,
        OptionsChainService optionsChainService,
        ExchangeTickerService exchangeTickerService,
        IExchangeService exchangeService,
        BlackScholes blackScholes)
    {
        _optionsService = optionsService;
        _optionsChainService = optionsChainService;
        _exchangeTickerService = exchangeTickerService;
        _exchangeService = exchangeService;
        _blackScholes = blackScholes;
    }

    public LegViewModel Create(LegsCollectionViewModel collectionViewModel, LegModel leg)
    {
        var vm = new LegViewModel(collectionViewModel, _optionsService, _optionsChainService, _exchangeTickerService, _exchangeService, _blackScholes);
        vm.Leg = leg;
        return vm;
    }
}
