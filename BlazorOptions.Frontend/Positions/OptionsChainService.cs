using BlazorOptions.Diagnostics;

namespace BlazorOptions.Services;

public class OptionsChainService : IOptionsChainService
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly SemaphoreSlim _liveModeLock = new(1, 1);
    private readonly object _cacheLock = new();
    private readonly object _subscriberLock = new();
    private readonly IOptionMarketDataService _optionMarketDataService;
    private readonly Dictionary<string, List<Func<OptionChainTicker, Task>>> _subscriberHandlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IDisposable> _marketDataSubscriptions = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<OptionChainTicker>> _cachedTickers = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _trackedSymbols = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ExchangeTradingPair> _availableOptionPairs = Array.Empty<ExchangeTradingPair>();
    private bool _isLive;

    public OptionsChainService(IOptionMarketDataService optionMarketDataService)
    {
        _optionMarketDataService = optionMarketDataService;
    }

    public DateTime? LastUpdatedUtc { get; private set; }

    public bool IsRefreshing { get; private set; }

    public bool IsLive
    {
        get => _isLive;
        set
        {
            if (_isLive == value)
            {
                return;
            }

            _isLive = value;
            _ = SetLiveModeAsync(value);
        }
    }

    public List<OptionChainTicker> GetTickersByBaseAsset(string baseAsset, LegType? legType = null)
    {
        using var activity = ActivitySources.Telemetry.StartActivity("OptionsChainService.GetTickersByBaseAsset");
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            return [];
        }

        var normalizedBase = baseAsset.Trim().ToUpperInvariant();
        lock (_cacheLock)
        {
            if (!_cachedTickers.TryGetValue(normalizedBase, out var tickers))
            {
                return [];
            }

            return legType.HasValue
                ? tickers.Where(x => x.Type == legType).ToList()
                : tickers.ToList();
        }
    }

    public IReadOnlyList<string> GetCachedBaseAssets()
    {
        lock (_cacheLock)
        {
            return _cachedTickers.Keys
                .OrderBy(x => x)
                .ToArray();
        }
    }

    public IReadOnlyList<string> GetCachedQuoteAssets(string baseAsset)
    {
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            return Array.Empty<string>();
        }

        var normalizedBase = baseAsset.Trim().ToUpperInvariant();
        lock (_cacheLock)
        {
            if (!_cachedTickers.TryGetValue(normalizedBase, out var tickers))
            {
                return Array.Empty<string>();
            }

            return tickers
                .Select(t => ExtractQuoteAsset(t.Symbol))
                .Where(q => !string.IsNullOrWhiteSpace(q))
                .Select(q => q!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)
                .ToArray();
        }
    }

    public async Task<IReadOnlyList<string>> GetAvailableBaseAssetsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAvailableOptionPairsAsync(cancellationToken);
        lock (_cacheLock)
        {
            return _availableOptionPairs
                .Select(p => p.BaseAsset)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)
                .ToArray();
        }
    }

    public async Task<IReadOnlyList<string>> GetAvailableQuoteAssetsAsync(string baseAsset, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            return Array.Empty<string>();
        }

        await EnsureAvailableOptionPairsAsync(cancellationToken);
        var normalizedBase = baseAsset.Trim().ToUpperInvariant();
        lock (_cacheLock)
        {
            return _availableOptionPairs
                .Where(p => string.Equals(p.BaseAsset, normalizedBase, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.QuoteAsset)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)
                .ToArray();
        }
    }

    public async Task EnsureTickersForBaseAssetAsync(string baseAsset)
    {
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            return;
        }

        lock (_cacheLock)
        {
            if (_cachedTickers.ContainsKey(baseAsset.Trim().ToUpperInvariant()))
            {
                return;
            }
        }

        await UpdateTickersAsync(baseAsset);
    }

    public async Task UpdateTickersAsync(string? baseAsset = null, CancellationToken cancellationToken = default)
    {
        var baseAssets = ResolveBaseAssetsToRefresh(baseAsset);
        if (baseAssets.Count == 0)
        {
            return;
        }

        if (!await _refreshLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        using var activity = ActivitySources.Telemetry.StartActivity("OptionsChainService.RefreshAsync");

        try
        {
            IsRefreshing = true;
            var allUpdated = new List<OptionChainTicker>();
            foreach (var currentBase in baseAssets)
            {
                var updated = await _optionMarketDataService.GetTickersAsync(currentBase, cancellationToken);
                if (updated.Count == 0)
                {
                    continue;
                }

                lock (_cacheLock)
                {
                    _cachedTickers[currentBase] = updated.ToList();
                }

                allUpdated.AddRange(updated);
            }

            if (allUpdated.Count == 0)
            {
                return;
            }

            LastUpdatedUtc = DateTime.UtcNow;
            foreach (var ticker in allUpdated
                         .GroupBy(t => t.Symbol, StringComparer.OrdinalIgnoreCase)
                         .Select(group => group.Last()))
            {
                await DispatchSubscriberHandlers(ticker);
            }
        }
        finally
        {
            IsRefreshing = false;
            _refreshLock.Release();
        }
    }

    public OptionChainTicker? FindTickerForLeg(LegModel leg, string? baseAsset = null)
    {
        if (string.IsNullOrEmpty(leg.Symbol))
        {
            return null;
        }

        if (string.IsNullOrEmpty(baseAsset))
        {
            BybitSymbolMapper.TryParseSymbol(leg.Symbol, out baseAsset, out _, out _, out _);
        }

        var tickers = GetTickersByBaseAsset(baseAsset);
        return tickers.FirstOrDefault(ticker => string.Equals(ticker.Symbol, leg.Symbol, StringComparison.OrdinalIgnoreCase));
    }

    public void TrackLegs(IEnumerable<LegModel> legs, string? baseAsset = null)
    {
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var leg in legs)
        {
            var ticker = FindTickerForLeg(leg, baseAsset);
            if (ticker is not null)
            {
                symbols.Add(ticker.Symbol);
            }
            else if (!string.IsNullOrWhiteSpace(leg.Symbol) &&
                     BybitSymbolMapper.TryParseSymbol(leg.Symbol, out _, out _, out _, out _))
            {
                symbols.Add(leg.Symbol.Trim());
            }
        }

        lock (_cacheLock)
        {
            _trackedSymbols = symbols;
        }

        if (IsLive)
        {
            _ = SyncMarketDataSubscriptionsAsync();
        }
    }

    public async ValueTask<IDisposable> SubscribeAsync(string symbol, Func<OptionChainTicker, Task> when)
    {
        if (string.IsNullOrWhiteSpace(symbol) || when is null)
        {
            return new SubscriptionRegistration(() => { });
        }

        symbol = symbol.Trim();
        var shouldSubscribe = false;

        lock (_subscriberLock)
        {
            if (!_subscriberHandlers.TryGetValue(symbol, out var handlers))
            {
                handlers = new List<Func<OptionChainTicker, Task>>();
                _subscriberHandlers[symbol] = handlers;
                shouldSubscribe = true;
            }

            handlers.Add(when);
        }

        var cachedTicker = FindCachedTicker(symbol);
        if (cachedTicker is not null)
        {
            try
            {
                await when(cachedTicker);
            }
            catch
            {
                // Consumer callbacks must not break subscription registration.
            }
        }

        if (shouldSubscribe && IsLive)
        {
            await EnsureMarketDataSubscriptionsAsync(new[] { symbol });
        }

        return new SubscriptionRegistration(() => _ = UnsubscribeAsync(symbol, when));
    }

    private async Task EnsureAvailableOptionPairsAsync(CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        lock (_cacheLock)
        {
            _availableOptionPairs = _optionMarketDataService.GetConfiguredTradingPairs();
        }
    }

    private IReadOnlyList<ExchangeTradingPair> BuildPairsFromExchange()
    {
        return _optionMarketDataService.GetConfiguredTradingPairs();
    }

    private List<string> ResolveBaseAssetsToRefresh(string? baseAsset)
    {
        if (!string.IsNullOrWhiteSpace(baseAsset))
        {
            return new List<string> { baseAsset.Trim().ToUpperInvariant() };
        }

        lock (_cacheLock)
        {
            if (_cachedTickers.Count > 0)
            {
                return _cachedTickers.Keys
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x)
                    .ToList();
            }
        }

        return BuildPairsFromExchange()
            .Select(pair => pair.BaseAsset)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();
    }

    private async Task SetLiveModeAsync(bool isLive)
    {
        await _liveModeLock.WaitAsync();
        try
        {
            if (!isLive)
            {
                DisposeMarketDataSubscriptions();
                return;
            }

            await SyncMarketDataSubscriptionsAsync();
        }
        catch
        {
            // Keep the option-chain cache usable if live transport fails.
        }
        finally
        {
            _liveModeLock.Release();
        }
    }

    private async Task SyncMarketDataSubscriptionsAsync()
    {
        var symbols = GetAllSymbolsToSubscribe();
        if (symbols.Count == 0)
        {
            return;
        }

        await EnsureMarketDataSubscriptionsAsync(symbols);
    }

    private async Task EnsureMarketDataSubscriptionsAsync(IEnumerable<string> symbols)
    {
        if (!IsLive)
        {
            return;
        }

        foreach (var symbol in symbols
                     .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
                     .Select(symbol => symbol.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_marketDataSubscriptions.ContainsKey(symbol))
            {
                continue;
            }

            _marketDataSubscriptions[symbol] = await _optionMarketDataService.SubscribeAsync(symbol, HandleMarketDataTickerAsync);
        }
    }

    private async Task HandleMarketDataTickerAsync(OptionChainTicker ticker)
    {
        UpdateTicker(ticker);
        await DispatchSubscriberHandlers(ticker);
    }

    private void UpdateTicker(OptionChainTicker ticker)
    {
        lock (_cacheLock)
        {
            if (!_cachedTickers.TryGetValue(ticker.BaseAsset, out var tickers))
            {
                tickers = new List<OptionChainTicker>();
                _cachedTickers[ticker.BaseAsset] = tickers;
            }

            var index = tickers.FindIndex(existing =>
                string.Equals(existing.Symbol, ticker.Symbol, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                tickers[index] = ticker;
            }
            else
            {
                tickers.Add(ticker);
            }
        }
    }

    private List<string> GetAllSymbolsToSubscribe()
    {
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        lock (_cacheLock)
        {
            foreach (var symbol in _trackedSymbols)
            {
                if (!string.IsNullOrWhiteSpace(symbol))
                {
                    symbols.Add(symbol.Trim());
                }
            }
        }

        lock (_subscriberLock)
        {
            foreach (var symbol in _subscriberHandlers.Keys)
            {
                if (!string.IsNullOrWhiteSpace(symbol))
                {
                    symbols.Add(symbol.Trim());
                }
            }
        }

        return symbols.ToList();
    }

    private OptionChainTicker? FindCachedTicker(string symbol)
    {
        lock (_cacheLock)
        {
            foreach (var tickers in _cachedTickers.Values)
            {
                var cachedTicker = tickers.FirstOrDefault(t =>
                    string.Equals(t.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
                if (cachedTicker is not null)
                {
                    return cachedTicker;
                }
            }
        }

        return null;
    }

    private async Task DispatchSubscriberHandlers(OptionChainTicker ticker)
    {
        List<Func<OptionChainTicker, Task>> handlers;
        lock (_subscriberLock)
        {
            if (!_subscriberHandlers.TryGetValue(ticker.Symbol, out var list) || list.Count == 0)
            {
                return;
            }

            handlers = list.ToList();
        }

        foreach (var handler in handlers)
        {
            try
            {
                await handler(ticker);
            }
            catch
            {
                // Subscriber failures are isolated so market updates continue.
            }
        }
    }

    private async Task UnsubscribeAsync(string symbol, Func<OptionChainTicker, Task> handler)
    {
        var shouldUnsubscribe = false;
        lock (_subscriberLock)
        {
            if (_subscriberHandlers.TryGetValue(symbol, out var handlers))
            {
                handlers.Remove(handler);
                if (handlers.Count == 0)
                {
                    _subscriberHandlers.Remove(symbol);
                    shouldUnsubscribe = true;
                }
            }
        }

        if (!shouldUnsubscribe || IsSymbolTracked(symbol))
        {
            return;
        }

        if (_marketDataSubscriptions.Remove(symbol, out var registration))
        {
            registration.Dispose();
        }

        await Task.CompletedTask;
    }

    private void DisposeMarketDataSubscriptions()
    {
        foreach (var registration in _marketDataSubscriptions.Values)
        {
            registration.Dispose();
        }

        _marketDataSubscriptions.Clear();
    }

    private bool IsSymbolTracked(string symbol)
    {
        lock (_cacheLock)
        {
            return _trackedSymbols.Contains(symbol);
        }
    }

    private static string? ExtractQuoteAsset(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return null;
        }

        var parts = symbol.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 5 ? parts[4] : null;
    }
}
