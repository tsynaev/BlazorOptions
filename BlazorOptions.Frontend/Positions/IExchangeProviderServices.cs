using BlazorOptions.ViewModels;
using BlazorChart.Models;
namespace BlazorOptions.Services;

public interface IOrdersService
{
    Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(CancellationToken cancellationToken = default);

    ValueTask<IDisposable> SubscribeAsync(
        Func<IReadOnlyList<ExchangeOrder>, Task> handler,
        CancellationToken cancellationToken = default);
}

public interface IOptionsChainService
{
    DateTime? LastUpdatedUtc { get; }

    bool IsRefreshing { get; }

    bool IsLive { get; set; }

    List<OptionChainTicker> GetTickersByBaseAsset(string baseAsset, LegType? legType = null);

    IReadOnlyList<string> GetCachedBaseAssets();

    IReadOnlyList<string> GetCachedQuoteAssets(string baseAsset);

    Task<IReadOnlyList<string>> GetAvailableBaseAssetsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetAvailableQuoteAssetsAsync(string baseAsset, CancellationToken cancellationToken = default);

    Task EnsureTickersForBaseAssetAsync(string baseAsset);

    Task UpdateTickersAsync(string? baseAsset = null, CancellationToken cancellationToken = default);

    OptionChainTicker? FindTickerForLeg(LegModel leg, string? baseAsset = null);

    void TrackLegs(IEnumerable<LegModel> legs, string? baseAsset = null);

    ValueTask<IDisposable> SubscribeAsync(string symbol, Func<OptionChainTicker, Task> when);
}

public interface IOptionMarketDataService : IAsyncDisposable
{
    Task<IReadOnlyList<OptionChainTicker>> GetTickersAsync(string? baseAsset = null, CancellationToken cancellationToken = default);

    IReadOnlyList<ExchangeTradingPair> GetConfiguredTradingPairs();

    ValueTask<IDisposable> SubscribeAsync(
        string symbol,
        Func<OptionChainTicker, Task> handler,
        CancellationToken cancellationToken = default);
}

public interface IFuturesInstrumentsService
{
    IReadOnlyList<DateTime?> GetCachedExpirations(string baseAsset, string? quoteAsset);

    Task EnsureExpirationsAsync(string baseAsset, string? quoteAsset, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExchangeTradingPair>> GetTradingPairsAsync(CancellationToken cancellationToken = default);
}

public interface IPositionsService : IAsyncDisposable
{
    Task<IEnumerable<ExchangePosition>> GetPositionsAsync();

    ValueTask<IDisposable> SubscribeAsync(
        Func<IReadOnlyList<ExchangePosition>, Task> handler,
        CancellationToken cancellationToken = default);
}

public interface ITickersService : IAsyncDisposable
{
    bool IsLive { get; set; }

    ValueTask<IDisposable> SubscribeAsync(
        string symbol,
        Func<ExchangePriceUpdate, Task> handler,
        CancellationToken cancellationToken = default);

    Task UpdateTickersAsync(CancellationToken cancellationToken = default);


// TODO: remove GetCandlesWithVolumeAsync, add volume to CandlePoint, add intervalMinutes parameter
    Task<IReadOnlyList<CandlePoint>> GetCandlesAsync(
        string symbol,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CandleVolumePoint>> GetCandlesWithVolumeAsync(
        string symbol,
        DateTime fromUtc,
        DateTime toUtc,
        int intervalMinutes = 60,
        CancellationToken cancellationToken = default);
}

public interface IWalletService : IAsyncDisposable
{
    Task<ExchangeWalletSnapshot?> GetSnapshotAsync(CancellationToken cancellationToken = default);

    ValueTask<IDisposable> SubscribeAsync(
        Func<ExchangeWalletSnapshot, Task> handler,
        CancellationToken cancellationToken = default);
}

public sealed record ExchangeTradingPair(string BaseAsset, string QuoteAsset);

public sealed record ExchangeWalletCoin(
    string Coin,
    decimal? Equity,
    decimal? WalletBalance,
    decimal? AvailableToWithdraw,
    decimal? UsdValue);

public sealed record ExchangeWalletSnapshot(
    DateTime UpdatedUtc,
    string AccountType,
    decimal? TotalEquity,
    decimal? TotalWalletBalance,
    decimal? TotalMarginBalance,
    decimal? TotalInitialMargin,
    decimal? TotalMaintenanceMargin,
    decimal? TotalAvailableBalance,
    decimal? TotalPerpUpl,
    IReadOnlyList<ExchangeWalletCoin> Coins);
