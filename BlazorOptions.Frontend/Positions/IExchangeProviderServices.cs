using BlazorOptions.ViewModels;
namespace BlazorOptions.Services;

public interface IOrdersService
{
    Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(CancellationToken cancellationToken = default);
}

public interface IOptionsChainService
{
    DateTime? LastUpdatedUtc { get; }

    bool IsRefreshing { get; }

    List<OptionChainTicker> GetTickersByBaseAsset(string baseAsset, LegType? legType = null);

    Task EnsureBaseAssetAsync(string baseAsset);

    Task RefreshAsync(string? baseAsset = null, CancellationToken cancellationToken = default);

    OptionChainTicker? FindTickerForLeg(LegModel leg, string? baseAsset = null);

    void TrackLegs(IEnumerable<LegModel> legs, string? baseAsset = null);

    ValueTask<IDisposable> SubscribeAsync(string symbol, Func<OptionChainTicker, Task> when);
}

public interface IFuturesInstrumentsService
{
    IReadOnlyList<DateTime?> GetCachedExpirations(string baseAsset, string? quoteAsset);

    Task EnsureExpirationsAsync(string baseAsset, string? quoteAsset, CancellationToken cancellationToken = default);
}

public interface IPositionsService : IAsyncDisposable
{
    Task<IEnumerable<BybitPosition>> GetPositionsAsync();

    ValueTask<IDisposable> SubscribeAsync(
        Func<IReadOnlyList<BybitPosition>, Task> handler,
        CancellationToken cancellationToken = default);
}

public interface ITickersService : IAsyncDisposable
{
    ValueTask<IDisposable> SubscribeAsync(
        string symbol,
        Func<ExchangePriceUpdate, Task> handler,
        CancellationToken cancellationToken = default);
}
