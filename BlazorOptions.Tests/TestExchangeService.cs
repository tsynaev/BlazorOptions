using BlazorOptions.Services;
using BlazorOptions.ViewModels;
using BlazorChart.Models;

namespace BlazorOptions.Tests;

internal sealed class TestExchangeService : IExchangeService
{
    public IOrdersService Orders { get; } = new TestOrdersService();
    public IPositionsService Positions { get; } = new TestPositionsService();
    public ITickersService Tickers { get; } = new TestTickersService();
    public IOptionsChainService OptionsChain { get; } = new TestOptionsChainService();
    public IOptionMarketDataService OptionMarketData { get; } = new TestOptionMarketDataService();
    public IFuturesInstrumentsService FuturesInstruments { get; } = new TestFuturesInstrumentsService();
    public IWalletService Wallet { get; } = new TestWalletService();
    public bool IsLive { set { } }

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

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class TestOrdersService : IOrdersService
    {
        public Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ExchangeOrder>>(Array.Empty<ExchangeOrder>());
        public ValueTask<IDisposable> SubscribeAsync(Func<IReadOnlyList<ExchangeOrder>, Task> handler, CancellationToken cancellationToken = default) => new(new SubscriptionRegistration(() => { }));
    }

    private sealed class TestPositionsService : IPositionsService
    {
        public Task<IEnumerable<ExchangePosition>> GetPositionsAsync() => Task.FromResult<IEnumerable<ExchangePosition>>(Array.Empty<ExchangePosition>());
        public ValueTask<IDisposable> SubscribeAsync(Func<IReadOnlyList<ExchangePosition>, Task> handler, CancellationToken cancellationToken = default) => new(new SubscriptionRegistration(() => { }));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestTickersService : ITickersService
    {
        public bool IsLive { get; set; }
        public ValueTask<IDisposable> SubscribeAsync(string symbol, Func<ExchangePriceUpdate, Task> handler, CancellationToken cancellationToken = default) => new(new SubscriptionRegistration(() => { }));
        public Task UpdateTickersAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<CandlePoint>> GetCandlesAsync(string symbol, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CandlePoint>>(Array.Empty<CandlePoint>());
        public Task<IReadOnlyList<CandleVolumePoint>> GetCandlesWithVolumeAsync(string symbol, DateTime fromUtc, DateTime toUtc, int intervalMinutes = 60, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CandleVolumePoint>>(Array.Empty<CandleVolumePoint>());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestOptionsChainService : IOptionsChainService
    {
        public DateTime? LastUpdatedUtc => null;
        public bool IsRefreshing => false;
        public bool IsLive { get; set; }
        public List<OptionChainTicker> GetTickersByBaseAsset(string baseAsset, LegType? legType = null) => [];
        public IReadOnlyList<string> GetCachedBaseAssets() => Array.Empty<string>();
        public IReadOnlyList<string> GetCachedQuoteAssets(string baseAsset) => Array.Empty<string>();
        public Task<IReadOnlyList<string>> GetAvailableBaseAssetsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<IReadOnlyList<string>> GetAvailableQuoteAssetsAsync(string baseAsset, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task EnsureTickersForBaseAssetAsync(string baseAsset) => Task.CompletedTask;
        public Task UpdateTickersAsync(string? baseAsset = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public OptionChainTicker? FindTickerForLeg(LegModel leg, string? baseAsset = null) => null;
        public void TrackLegs(IEnumerable<LegModel> legs, string? baseAsset = null) { }
        public ValueTask<IDisposable> SubscribeAsync(string symbol, Func<OptionChainTicker, Task> when) => new(new SubscriptionRegistration(() => { }));
    }

    private sealed class TestOptionMarketDataService : IOptionMarketDataService
    {
        public Task<IReadOnlyList<OptionChainTicker>> GetTickersAsync(string? baseAsset = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<OptionChainTicker>>(Array.Empty<OptionChainTicker>());
        public IReadOnlyList<ExchangeTradingPair> GetConfiguredTradingPairs() => Array.Empty<ExchangeTradingPair>();
        public ValueTask<IDisposable> SubscribeAsync(string symbol, Func<OptionChainTicker, Task> handler, CancellationToken cancellationToken = default) => new(new SubscriptionRegistration(() => { }));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestFuturesInstrumentsService : IFuturesInstrumentsService
    {
        public IReadOnlyList<DateTime?> GetCachedExpirations(string baseAsset, string? quoteAsset) => Array.Empty<DateTime?>();
        public Task EnsureExpirationsAsync(string baseAsset, string? quoteAsset, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ExchangeTradingPair>> GetTradingPairsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ExchangeTradingPair>>(Array.Empty<ExchangeTradingPair>());
    }

    private sealed class TestWalletService : IWalletService
    {
        public Task<ExchangeWalletSnapshot?> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult<ExchangeWalletSnapshot?>(null);
        public ValueTask<IDisposable> SubscribeAsync(Func<ExchangeWalletSnapshot, Task> handler, CancellationToken cancellationToken = default) => new(new SubscriptionRegistration(() => { }));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
