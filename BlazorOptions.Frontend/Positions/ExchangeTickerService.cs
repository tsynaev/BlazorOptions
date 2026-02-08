using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using BlazorOptions.ViewModels;
using BlazorChart.Models;
using Microsoft.Extensions.Options;

namespace BlazorOptions.Services;

public class ExchangeTickerService : ITickersService
{
    private static readonly Uri DefaultBybitWebSocketUrl = new("wss://stream.bybit.com/v5/public/linear");
    private const string BybitKlineUrl = "https://api.bybit.com/v5/market/kline";
    private readonly IReadOnlyDictionary<string, IExchangeTickerClient> _clients;
    private readonly ConcurrentDictionary<IExchangeTickerClient, Func<ExchangePriceUpdate, Task>> _handlers = new();
    private readonly object _subscriberLock = new();
    private readonly Dictionary<string, List<Func<ExchangePriceUpdate, Task>>> _subscriberHandlers = new(StringComparer.OrdinalIgnoreCase);
    private IExchangeTickerClient? _activeClient;
    private Uri? _activeWebSocketUrl;
    private readonly IOptions<BybitSettings> _bybitSettingsOptions;
    private readonly HttpClient _httpClient;
    private TimeSpan _livePriceUpdateInterval = TimeSpan.FromMilliseconds(1000);
    private readonly Dictionary<string, DateTime> _lastUpdateUtc = new(StringComparer.OrdinalIgnoreCase);

    public ExchangeTickerService(
        IEnumerable<IExchangeTickerClient> clients,
        IOptions<BybitSettings> bybitSettingsOptions,
        HttpClient httpClient)
    {
        // TODO: Evaluate Bybit.Net usage when it supports Blazor WebAssembly.
        _clients = clients.ToDictionary(client => client.Exchange, StringComparer.OrdinalIgnoreCase);
        _bybitSettingsOptions = bybitSettingsOptions;
        _httpClient = httpClient;

        foreach (var client in _clients.Values)
        {
            var handler = HandleClientPriceUpdated;
            _handlers[client] = handler;
            client.PriceUpdated += handler;
        }
    }

    public async Task<IReadOnlyList<CandlePoint>> GetCandlesAsync(
        string symbol,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return Array.Empty<CandlePoint>();
        }

        var from = NormalizeUtc(fromUtc);
        var to = NormalizeUtc(toUtc);
        if (to <= from)
        {
            to = from.AddMinutes(1);
        }

        var query = $"category=linear&symbol={Uri.EscapeDataString(symbol.Trim().ToUpperInvariant())}&interval=60&start={new DateTimeOffset(from).ToUnixTimeMilliseconds()}&end={new DateTimeOffset(to).ToUnixTimeMilliseconds()}&limit=1000";
        using var response = await _httpClient.GetAsync($"{BybitKlineUrl}?{query}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return Array.Empty<CandlePoint>();
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (!root.TryGetProperty("retCode", out var retCodeElement)
            || !retCodeElement.TryGetInt32(out var retCode)
            || retCode != 0)
        {
            return Array.Empty<CandlePoint>();
        }

        if (!root.TryGetProperty("result", out var resultElement)
            || !resultElement.TryGetProperty("list", out var listElement)
            || listElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<CandlePoint>();
        }

        var candles = new List<CandlePoint>();
        foreach (var item in listElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 5)
            {
                continue;
            }

            var values = item.EnumerateArray().ToArray();
            if (!TryParseLong(values[0], out var timeMs)
                || !TryParseDouble(values[1], out var open)
                || !TryParseDouble(values[2], out var high)
                || !TryParseDouble(values[3], out var low)
                || !TryParseDouble(values[4], out var close))
            {
                continue;
            }

            candles.Add(new CandlePoint(timeMs, open, high, low, close));
        }

        return candles
            .OrderBy(c => c.Time)
            .ToArray();
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

        await EnsureConnectedAsync(cancellationToken);

        if (shouldSubscribe)
        {
            await _activeClient!.SubscribeAsync(normalizedSymbol, cancellationToken);
        }

        return new SubscriptionRegistration(() => _ = UnsubscribeAsync(normalizedSymbol, handler));
    }

    private Uri ResolveWebSocketUrl(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return parsed;
        }

        return DefaultBybitWebSocketUrl;
    }

 
    private async Task DisconnectAsync()
    {
        if (_activeClient is null)
        {
            return;
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

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (!_clients.TryGetValue("Bybit", out var client))
        {
            throw new InvalidOperationException("Exchange 'Bybit' is not registered.");
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

        if (_subscriberHandlers.Count == 0)
        {
            await _activeClient.DisconnectAsync();
            _activeClient = null;
            _activeWebSocketUrl = null;
        }
    }

    private static bool TryParseLong(JsonElement element, out long value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String
            && long.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryParseDouble(JsonElement element, out double value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String
            && double.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    }
}
