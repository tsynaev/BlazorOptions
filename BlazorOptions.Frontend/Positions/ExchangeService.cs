using System.Globalization;
using BlazorChart.Models;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorOptions.Services;

public interface IExchangeService
{
    string? FormatSymbol(LegModel leg, string? baseAsset = null, string? settleAsset = null);
    bool TryParseSymbol(string symbol, out string baseAsset, out DateTime expiration, out decimal strike, out LegType type);
    bool TryCreateLeg(string symbol, decimal size, out LegModel leg);
    IOrdersService Orders { get; }
    IPositionsService Positions { get; }
    ITickersService Tickers { get; }
    IOptionsChainService OptionsChain { get; }
    IFuturesInstrumentsService FuturesInstruments { get; }
}

public sealed class ExchangeService : IExchangeService
{
    private static readonly string[] ExpirationFormats = { "dMMMyy", "ddMMMyy", "ddMMMyyyy" };
    private readonly IServiceProvider? _services;
    private IOptionsChainService? _optionsChain;
    private IFuturesInstrumentsService? _futuresInstruments;

    public ExchangeService()
        : this(new NullOrdersService(), new NullPositionsService(), new NullTickersService(), null)
    {
        _optionsChain = new NullOptionsChainService();
        _futuresInstruments = new NullFuturesInstrumentsService();
    }

    public ExchangeService(IOrdersService orders, IPositionsService positions, ITickersService tickers, IServiceProvider? services)
    {
        Orders = orders;
        Positions = positions;
        Tickers = tickers;
        _services = services;
    }

    public IOrdersService Orders { get; }

    public IPositionsService Positions { get; }

    public ITickersService Tickers { get; }

    public IOptionsChainService OptionsChain => _optionsChain ??= _services?.GetRequiredService<IOptionsChainService>()
        ?? new NullOptionsChainService();

    public IFuturesInstrumentsService FuturesInstruments => _futuresInstruments ??= _services?.GetRequiredService<IFuturesInstrumentsService>()
        ?? new NullFuturesInstrumentsService();

    public string? FormatSymbol(LegModel leg, string? baseAsset = null, string? settleAsset = null)
    {
        if (leg is null)
        {
            return null;
        }

        if (leg.Type == LegType.Future)
        {
            if (leg.ExpirationDate.HasValue)
            {
                return $"{baseAsset}{settleAsset}-{leg.ExpirationDate.Value.ToString("ddMMMyy", CultureInfo.InvariantCulture)}".ToUpper();
            }

            return $"{baseAsset}{settleAsset}";
        }

        if (!leg.Strike.HasValue || !leg.ExpirationDate.HasValue)
        {
            return null;
        }

        var typeToken = leg.Type == LegType.Put ? "P" : "C";

        return $"{baseAsset}-{leg.ExpirationDate.Value.ToString("dMMMyy", CultureInfo.InvariantCulture)}-{leg.Strike.Value:0.##}-{typeToken}".ToUpper();
    }

    public bool TryParseSymbol(string symbol, out string baseAsset, out DateTime expiration, out decimal strike, out LegType type)
    {
        baseAsset = string.Empty;
        expiration = default;
        strike = 0;
        type = LegType.Call;

        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var parts = symbol.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 4)
        {
            return false;
        }

        baseAsset = parts[0];
        if (!DateTime.TryParseExact(parts[1], ExpirationFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedExpiration))
        {
            return false;
        }

        expiration = parsedExpiration.Date;

        if (!decimal.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out strike))
        {
            return false;
        }

        var typeToken = parts[3].Trim();
        type = typeToken.Equals("P", StringComparison.OrdinalIgnoreCase) ? LegType.Put : LegType.Call;
        return true;
    }

    public bool TryCreateLeg(string symbol, decimal size, out LegModel leg)
    {
        leg = new LegModel();
        var parsed = TryParseSymbol(symbol, out var baseAsset, out var expiration, out var strike, out var type);
        if (!parsed)
        {
            return false;
        }

        leg.ExpirationDate = expiration;
        leg.Strike = strike;
        leg.Type = type;
        leg.Size = size;
        leg.Symbol = symbol;
        return true;
    }

    private sealed class NullOrdersService : IOrdersService
    {
        public Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ExchangeOrder>>(Array.Empty<ExchangeOrder>());
        }

        public ValueTask<IDisposable> SubscribeAsync(
            Func<IReadOnlyList<ExchangeOrder>, Task> handler,
            CancellationToken cancellationToken = default)
        {
            return new ValueTask<IDisposable>(new SubscriptionRegistration(() => { }));
        }
    }

    private sealed class NullPositionsService : IPositionsService
    {
        public Task<IEnumerable<ExchangePosition>> GetPositionsAsync()
        {
            return Task.FromResult<IEnumerable<ExchangePosition>>(Array.Empty<ExchangePosition>());
        }

        public ValueTask<IDisposable> SubscribeAsync(
            Func<IReadOnlyList<ExchangePosition>, Task> handler,
            CancellationToken cancellationToken = default)
        {
            return new ValueTask<IDisposable>(new SubscriptionRegistration(() => { }));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullTickersService : ITickersService
    {
        public ValueTask<IDisposable> SubscribeAsync(string symbol, Func<ExchangePriceUpdate, Task> handler, CancellationToken cancellationToken = default)
        {
            return new ValueTask<IDisposable>(new SubscriptionRegistration(() => { }));
        }

        public Task<IReadOnlyList<CandlePoint>> GetCandlesAsync(
            string symbol,
            DateTime fromUtc,
            DateTime toUtc,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<CandlePoint>>(Array.Empty<CandlePoint>());
        }

        public Task<IReadOnlyList<CandleVolumePoint>> GetCandlesWithVolumeAsync(
            string symbol,
            DateTime fromUtc,
            DateTime toUtc,
            int intervalMinutes = 60,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<CandleVolumePoint>>(Array.Empty<CandleVolumePoint>());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullOptionsChainService : IOptionsChainService
    {
        public DateTime? LastUpdatedUtc => null;

        public bool IsRefreshing => false;

        public List<OptionChainTicker> GetTickersByBaseAsset(string baseAsset, LegType? legType = null)
        {
            return new List<OptionChainTicker>();
        }

        public IReadOnlyList<string> GetCachedBaseAssets()
        {
            return Array.Empty<string>();
        }

        public IReadOnlyList<string> GetCachedQuoteAssets(string baseAsset)
        {
            return Array.Empty<string>();
        }

        public Task<IReadOnlyList<string>> GetAvailableBaseAssetsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        public Task<IReadOnlyList<string>> GetAvailableQuoteAssetsAsync(string baseAsset, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        public Task EnsureBaseAssetAsync(string baseAsset) => Task.CompletedTask;

        public Task RefreshAsync(string? baseAsset = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public OptionChainTicker? FindTickerForLeg(LegModel leg, string? baseAsset = null) => null;

        public void TrackLegs(IEnumerable<LegModel> legs, string? baseAsset = null)
        {
        }

        public ValueTask<IDisposable> SubscribeAsync(string symbol, Func<OptionChainTicker, Task> when)
        {
            return new ValueTask<IDisposable>(new SubscriptionRegistration(() => { }));
        }
    }

    private sealed class NullFuturesInstrumentsService : IFuturesInstrumentsService
    {
        public IReadOnlyList<DateTime?> GetCachedExpirations(string baseAsset, string? quoteAsset)
        {
            return Array.Empty<DateTime?>();
        }

        public Task EnsureExpirationsAsync(string baseAsset, string? quoteAsset, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ExchangeTradingPair>> GetTradingPairsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ExchangeTradingPair>>(Array.Empty<ExchangeTradingPair>());
        }
    }

}
