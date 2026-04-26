namespace BlazorOptions.Services;

public interface IExchangeService : IDisposable, IAsyncDisposable
{
    string? FormatSymbol(LegModel leg, string? baseAsset = null, string? settleAsset = null);
    bool TryParseSymbol(string symbol, out string baseAsset, out DateTime expiration, out decimal strike, out LegType type);
    bool TryCreateLeg(string symbol, decimal size, out LegModel leg);
    bool TryCreateLeg(string symbol, decimal size, string? baseAsset, string? category, out LegModel leg);
    IOrdersService Orders { get; }
    IPositionsService Positions { get; }
    ITickersService Tickers { get; }
    IOptionsChainService OptionsChain { get; }
    IOptionMarketDataService OptionMarketData { get; }
    IFuturesInstrumentsService FuturesInstruments { get; }
    IWalletService Wallet { get; }
    bool IsLive { set; }
}
