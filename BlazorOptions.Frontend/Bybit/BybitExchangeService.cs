using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using BlazorOptions.ViewModels;

namespace BlazorOptions.Services;

public sealed class BybitExchangeService : IExchangeService
{
    private readonly object[] _ownedServices;
    private bool _disposed;

    public BybitExchangeService(
        HttpClient httpClient,
        IOptions<BybitSettings> bybitSettingsOptions,
        ILoggerFactory loggerFactory)
    {
        var privateStreamLogger = loggerFactory.CreateLogger<BybitPrivateStreamService>();
        var activeWalletLogger = loggerFactory.CreateLogger<ActiveWalletService>();
        var bybitPositionService = new BybitPositionService(httpClient, bybitSettingsOptions);
        var bybitOrderService = new BybitOrderService(httpClient, bybitSettingsOptions);
        var bybitWalletService = new BybitWalletService(httpClient, bybitSettingsOptions);
        var bybitPrivateStreamService = new BybitPrivateStreamService(bybitSettingsOptions, privateStreamLogger);
        var activeOrdersService = new ActiveOrdersService(bybitOrderService, bybitPrivateStreamService);
        var activePositionsService = new ActivePositionsService(bybitPositionService, bybitPrivateStreamService, bybitSettingsOptions);
        var activeWalletService = new ActiveWalletService(bybitWalletService, bybitPrivateStreamService, bybitSettingsOptions, activeWalletLogger);
        var bybitTickerService = new BybitTickerService(bybitSettingsOptions, httpClient);
        var optionMarketDataService = new BybitOptionMarketDataService(httpClient, bybitSettingsOptions);
        var optionsChainService = new OptionsChainService(optionMarketDataService);
        var futuresInstrumentsService = new FuturesInstrumentsService(httpClient, bybitSettingsOptions);

        Orders = activeOrdersService;
        Positions = activePositionsService;
        Tickers = bybitTickerService;
        Wallet = activeWalletService;
        OptionsChain = optionsChainService;
        OptionMarketData = optionMarketDataService;
        FuturesInstruments = futuresInstrumentsService;
        _ownedServices =
        [
            bybitPrivateStreamService,
            activeOrdersService,
            activePositionsService,
            activeWalletService,
            bybitTickerService,
            optionMarketDataService,
            optionsChainService,
            futuresInstrumentsService
        ];
    }

    public IOrdersService Orders { get; }

    public IPositionsService Positions { get; }

    public ITickersService Tickers { get; }

    public IWalletService Wallet { get; }

    public IOptionsChainService OptionsChain { get; }

    public IOptionMarketDataService OptionMarketData { get; }

    public IFuturesInstrumentsService FuturesInstruments { get; }

    public bool IsLive
    {
        set
        {
            Tickers.IsLive = value;
            OptionsChain.IsLive = value;
        }
    }

    public string? FormatSymbol(LegModel leg, string? baseAsset = null, string? settleAsset = null)
    {
        return BybitSymbolMapper.FormatSymbol(leg, baseAsset, settleAsset);
    }

    public bool TryParseSymbol(string symbol, out string baseAsset, out DateTime expiration, out decimal strike, out LegType type)
    {
        return BybitSymbolMapper.TryParseSymbol(symbol, out baseAsset, out expiration, out strike, out type);
    }

    public bool TryCreateLeg(string symbol, decimal size, out LegModel leg)
    {
        return BybitSymbolMapper.TryCreateLeg(symbol, size, out leg);
    }

    public bool TryCreateLeg(string symbol, decimal size, string? baseAsset, string? category, out LegModel leg)
    {
        return BybitSymbolMapper.TryCreateLeg(symbol, size, baseAsset, category, out leg);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var ownedService in _ownedServices)
        {
            try
            {
                switch (ownedService)
                {
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync();
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
            catch
            {
                // Child disposal failures must not leak the runtime.
            }
        }
    }

    public void Dispose()
    {
        _ = DisposeAsync();
    }
}
