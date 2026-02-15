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
    ValueTask<IDisposable> SubscribeAsync(
        string symbol,
        Func<ExchangePriceUpdate, Task> handler,
        CancellationToken cancellationToken = default);

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

public sealed record ExchangeTradingPair(string BaseAsset, string QuoteAsset);
