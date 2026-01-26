using System.Collections.Concurrent;
using BlazorOptions.ViewModels;
using Microsoft.Extensions.Options;

namespace BlazorOptions.Services;

public class ExchangeTickerService : IAsyncDisposable
{
    private static readonly Uri DefaultBybitWebSocketUrl = new("wss://stream.bybit.com/v5/public/linear");
    private readonly IReadOnlyDictionary<string, IExchangeTickerClient> _clients;
    private readonly ConcurrentDictionary<IExchangeTickerClient, Func<ExchangePriceUpdate, Task>> _handlers = new();
    private readonly object _subscriberLock = new();
    private readonly Dictionary<string, List<Func<ExchangePriceUpdate, Task>>> _subscriberHandlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _legacySubscriptions = new(StringComparer.OrdinalIgnoreCase);
    private IExchangeTickerClient? _activeClient;
    private Uri? _activeWebSocketUrl;
    private readonly IOptions<BybitSettings> _bybitSettingsOptions;
    private TimeSpan _livePriceUpdateInterval = TimeSpan.FromMilliseconds(1000);
    private readonly Dictionary<string, DateTime> _lastUpdateUtc = new(StringComparer.OrdinalIgnoreCase);

    public ExchangeTickerService(IEnumerable<IExchangeTickerClient> clients, IOptions<BybitSettings> bybitSettingsOptions)
    {
        // TODO: Evaluate Bybit.Net usage when it supports Blazor WebAssembly.
        _clients = clients.ToDictionary(client => client.Exchange, StringComparer.OrdinalIgnoreCase);
        _bybitSettingsOptions = bybitSettingsOptions;

        foreach (var client in _clients.Values)
        {
            var handler = HandleClientPriceUpdated;
            _handlers[client] = handler;
            client.PriceUpdated += handler;
        }
    }

  
    public async ValueTask<IDisposable> SubscribeAsync(
        string symbol,
        Func<ExchangePriceUpdate, Task> handler,
        CancellationToken cancellationToken = default)
    {
        if (handler is null || string.IsNullOrWhiteSpace(symbol))
        {
            return new SubscriptionRegistration(() => { });
        }

        var normalizedSymbol = symbol.Trim();
        var shouldSubscribe = false;

        lock (_subscriberLock)
        {
            if (!_subscriberHandlers.TryGetValue(normalizedSymbol, out var handlers))
            {
                handlers = new List<Func<ExchangePriceUpdate, Task>>();
                _subscriberHandlers[normalizedSymbol] = handlers;
                shouldSubscribe = true;
            }

            handlers.Add(handler);
        }

        await EnsureConnectedAsync(new ExchangeTickerSubscription("Bybit", normalizedSymbol, ResolveWebSocketUrl(null)), cancellationToken);

        if (shouldSubscribe)
        {
            await _activeClient!.SubscribeAsync(normalizedSymbol, cancellationToken);
        }

        return new SubscriptionRegistration(() => _ = UnsubscribeAsync(normalizedSymbol, handler));
    }

    public async Task ConnectAsync(ExchangeTickerSubscription subscription, CancellationToken cancellationToken)
    {
        if (!_clients.TryGetValue(subscription.Exchange, out var client))
        {
            throw new InvalidOperationException($"Exchange '{subscription.Exchange}' is not registered.");
        }

        if (_activeClient != client || _activeWebSocketUrl != subscription.WebSocketUrl)
        {
            await DisconnectAsync();
            _activeClient = client;
            _activeWebSocketUrl = subscription.WebSocketUrl;
        }

        await _activeClient.EnsureConnectedAsync(subscription.WebSocketUrl, cancellationToken);
        await _activeClient.SubscribeAsync(subscription.Symbol, cancellationToken);
        lock (_subscriberLock)
        {
            _legacySubscriptions.Add(subscription.Symbol);
        }
    }

    public Uri ResolveWebSocketUrl(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return parsed;
        }

        return DefaultBybitWebSocketUrl;
    }

 
    public async Task DisconnectAsync()
    {
        if (_activeClient is null)
        {
            return;
        }

        List<string> legacySymbols;
        lock (_subscriberLock)
        {
            legacySymbols = _legacySubscriptions.ToList();
            _legacySubscriptions.Clear();
        }

        foreach (var symbol in legacySymbols)
        {
            await _activeClient.UnsubscribeAsync(symbol, CancellationToken.None);
        }

        await _activeClient.DisconnectAsync();
        _activeClient = null;
        _activeWebSocketUrl = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();

        foreach (var (client, handler) in _handlers)
        {
            client.PriceUpdated -= handler;
        }

        _handlers.Clear();
    }

    private async Task EnsureConnectedAsync(ExchangeTickerSubscription subscription, CancellationToken cancellationToken)
    {
        if (!_clients.TryGetValue(subscription.Exchange, out var client))
        {
            throw new InvalidOperationException($"Exchange '{subscription.Exchange}' is not registered.");
        }

        var settings = _bybitSettingsOptions.Value;
        var resolvedUrl = ResolveWebSocketUrl(settings.WebSocketUrl);
        if (_activeClient != client || _activeWebSocketUrl != resolvedUrl)
        {
            await DisconnectAsync();
            _activeClient = client;
            _activeWebSocketUrl = resolvedUrl;
        }

        await _activeClient.EnsureConnectedAsync(_activeWebSocketUrl, cancellationToken);

        List<string> symbols;
        lock (_subscriberLock)
        {
            symbols = _subscriberHandlers.Keys
                .Concat(_legacySubscriptions)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        foreach (var symbol in symbols)
        {
            await _activeClient.SubscribeAsync(symbol, cancellationToken);
        }
    }

    private async Task HandleClientPriceUpdated(ExchangePriceUpdate update)
    {
        var settings = _bybitSettingsOptions.Value;
        _livePriceUpdateInterval = TimeSpan.FromMilliseconds(Math.Max(100, settings.LivePriceUpdateIntervalMilliseconds));
        var now = DateTime.UtcNow;
        if (_lastUpdateUtc.TryGetValue(update.Symbol, out var lastUpdate) &&
            now - lastUpdate < _livePriceUpdateInterval)
        {
            return;
        }

        _lastUpdateUtc[update.Symbol] = now;

        List<Func<ExchangePriceUpdate, Task>>? handlers = null;
        lock (_subscriberLock)
        {
            if (_subscriberHandlers.TryGetValue(update.Symbol, out var symbolHandlers))
            {
                handlers = symbolHandlers.ToList();
            }
        }

        if (handlers is not null)
        {
            foreach (var handler in handlers)
            {
                await handler.Invoke(update);
            }
        }
    }

    private async Task UnsubscribeAsync(string symbol, Func<ExchangePriceUpdate, Task> handler)
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

        if (_activeClient is null)
        {
            return;
        }

        if (shouldUnsubscribe)
        {
            await _activeClient.UnsubscribeAsync(symbol, CancellationToken.None);
        }

        if (_subscriberHandlers.Count == 0 && _legacySubscriptions.Count == 0)
        {
            await _activeClient.DisconnectAsync();
            _activeClient = null;
            _activeWebSocketUrl = null;
        }
    }
}
