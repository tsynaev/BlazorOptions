using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BlazorOptions.ViewModels;
using BlazorChart.Models;
using Microsoft.Extensions.Options;

namespace BlazorOptions.Services;

public sealed class BybitTickerService : ITickersService
{
    private static readonly Uri DefaultBybitWebSocketUrl = new("wss://stream.bybit.com/v5/public/linear");
    private const int MaxKlineRequestLimit = 1000;
    private readonly object _subscriberLock = new();
    private readonly Dictionary<string, List<Func<ExchangePriceUpdate, Task>>> _subscriberHandlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ExchangePriceUpdate> _lastPriceBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly IOptions<BybitSettings> _bybitSettingsOptions;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly HashSet<string> _subscribedSymbols = new(StringComparer.OrdinalIgnoreCase);
    private TimeSpan _livePriceUpdateInterval = TimeSpan.FromMilliseconds(1000);
    private readonly Dictionary<string, DateTime> _lastUpdateUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _liveModeLock = new(1, 1);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _connectionCts;
    private Task? _receiveTask;
    private Uri? _activeWebSocketUrl;
    private bool _isLive;

    public BybitTickerService(
        IOptions<BybitSettings> bybitSettingsOptions,
        HttpClient httpClient)
    {
        _bybitSettingsOptions = bybitSettingsOptions;
        _httpClient = httpClient;
    }

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

    public async Task<IReadOnlyList<CandlePoint>> GetCandlesAsync(
        string symbol,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default)
    {
        var withVolume = await GetCandlesWithVolumeAsync(symbol, fromUtc, toUtc, 60, cancellationToken);
        return withVolume
            .OrderBy(c => c.Time)
            .Select(c => new CandlePoint(c.Time, c.Open, c.High, c.Low, c.Close))
            .ToArray();
    }

    public async Task<IReadOnlyList<CandleVolumePoint>> GetCandlesWithVolumeAsync(
        string symbol,
        DateTime fromUtc,
        DateTime toUtc,
        int intervalMinutes = 60,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return Array.Empty<CandleVolumePoint>();
        }

        var from = NormalizeUtc(fromUtc);
        var to = NormalizeUtc(toUtc);
        if (to <= from)
        {
            to = from.AddMinutes(Math.Max(intervalMinutes, 1));
        }

        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        var normalizedInterval = Math.Max(intervalMinutes, 1);
        var intervalMs = normalizedInterval * 60L * 1000L;
        var maxWindowMs = intervalMs * (MaxKlineRequestLimit - 1L);
        var currentFrom = from;
        var byTime = new Dictionary<long, CandleVolumePoint>();

        while (currentFrom < to)
        {
            var maxToTicks = currentFrom.Ticks + TimeSpan.FromMilliseconds(maxWindowMs).Ticks;
            var windowTo = maxToTicks >= to.Ticks ? to : new DateTime(maxToTicks, DateTimeKind.Utc);

            var windowCandles = await FetchCandlesWindowAsync(
                normalizedSymbol,
                currentFrom,
                windowTo,
                normalizedInterval,
                cancellationToken);

            foreach (var candle in windowCandles)
            {
                byTime[candle.Time] = candle;
            }

            if (windowTo >= to)
            {
                break;
            }

            currentFrom = windowTo.AddMilliseconds(intervalMs);
        }

        return byTime.Values
            .OrderBy(c => c.Time)
            .ToArray();
    }

    private async Task<IReadOnlyList<CandleVolumePoint>> FetchCandlesWindowAsync(
        string symbol,
        DateTime fromUtc,
        DateTime toUtc,
        int intervalMinutes,
        CancellationToken cancellationToken)
    {
        var bybitKlineUrl = _bybitSettingsOptions.Value.MarketKlineUri;
        var query = $"category=linear&symbol={Uri.EscapeDataString(symbol)}&interval={intervalMinutes}&start={new DateTimeOffset(fromUtc).ToUnixTimeMilliseconds()}&end={new DateTimeOffset(toUtc).ToUnixTimeMilliseconds()}&limit={MaxKlineRequestLimit}";
        using var response = await _httpClient.GetAsync(new Uri($"{bybitKlineUrl}?{query}"), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return Array.Empty<CandleVolumePoint>();
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (!root.TryGetProperty("retCode", out var retCodeElement)
            || !retCodeElement.TryGetInt32(out var retCode)
            || retCode != 0)
        {
            return Array.Empty<CandleVolumePoint>();
        }

        if (!root.TryGetProperty("result", out var resultElement)
            || !resultElement.TryGetProperty("list", out var listElement)
            || listElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<CandleVolumePoint>();
        }

        var candles = new List<CandleVolumePoint>();
        foreach (var item in listElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 6)
            {
                continue;
            }

            var values = item.EnumerateArray().ToArray();
            if (!TryParseLong(values[0], out var timeMs)
                || !TryParseDouble(values[1], out var open)
                || !TryParseDouble(values[2], out var high)
                || !TryParseDouble(values[3], out var low)
                || !TryParseDouble(values[4], out var close)
                || !TryParseDouble(values[5], out var volume))
            {
                continue;
            }

            candles.Add(new CandleVolumePoint(timeMs, open, high, low, close, volume));
        }

        return candles;
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

        ExchangePriceUpdate? cachedPrice;
        var hasCachedPrice = false;
        lock (_subscriberLock)
        {
            hasCachedPrice = _lastPriceBySymbol.TryGetValue(normalizedSymbol, out cachedPrice);
        }

        if (hasCachedPrice)
        {
            try
            {
                await handler(cachedPrice! with { Timestamp = DateTime.UtcNow });
            }
            catch
            {
                // Keep subscription flow resilient if consumer callback fails.
            }
        }

        if (IsLive)
        {
            await EnsureConnectedAsync(cancellationToken);

            if (shouldSubscribe)
            {
                await SubscribeSymbolAsync(normalizedSymbol, cancellationToken);
            }
        }

        return new SubscriptionRegistration(() => _ = UnsubscribeAsync(normalizedSymbol, handler));
    }

    public async Task UpdateTickersAsync(CancellationToken cancellationToken = default)
    {
        string[] targetSymbols;

        lock (_subscriberLock)
        {
            targetSymbols = _subscriberHandlers.Keys
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }


        if (targetSymbols.Length == 0)
        {
            return;
        }

        foreach (var symbol in targetSymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            decimal? price = null;
            try
            {
                var toUtc = DateTime.UtcNow;
                var fromUtc = toUtc.AddHours(-2);
                var candles = await GetCandlesWithVolumeAsync(symbol, fromUtc, toUtc, 60, cancellationToken);
                var last = candles
                    .OrderBy(c => c.Time)
                    .LastOrDefault();
                if (last is not null && last.Close > 0)
                {
                    price = (decimal)last.Close;
                }
            }
            catch
            {
                // Keep snapshot update resilient.
            }

            if (price.HasValue)
            {
                lock (_subscriberLock)
                {
                    _lastPriceBySymbol[symbol] = new ExchangePriceUpdate("Bybit", symbol, price.Value, price.Value, DateTime.UtcNow);
                }

                await DispatchUpdateAsync(new ExchangePriceUpdate("Bybit", symbol, price.Value, price.Value, DateTime.UtcNow));
                continue;
            }

            ExchangePriceUpdate? cachedPrice;
            lock (_subscriberLock)
            {
                if (!_lastPriceBySymbol.TryGetValue(symbol, out cachedPrice))
                {
                    continue;
                }
            }

            // Fallback to cached symbol price to ensure subscribers are notified.
            await DispatchUpdateAsync(cachedPrice! with { Timestamp = DateTime.UtcNow });
        }
    }

    private Uri ResolveWebSocketUrl(Uri? url)
    {
        if (url?.IsAbsoluteUri == true)
        {
            return url;
        }

        return DefaultBybitWebSocketUrl;
    }

 
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }

    private async Task SetLiveModeAsync(bool isLive)
    {
        await _liveModeLock.WaitAsync();
        try
        {
            if (isLive)
            {
                await EnsureConnectedAsync(CancellationToken.None);
                return;
            }

            await DisconnectAsync();
        }
        catch
        {
            // Ignore toggling failures and keep service usable.
        }
        finally
        {
            _liveModeLock.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        var settings = _bybitSettingsOptions.Value;
        var resolvedUrl = ResolveWebSocketUrl(settings.PublicWebSocketUrl);
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_socket is not null
                && _socket.State == WebSocketState.Open
                && _activeWebSocketUrl == resolvedUrl)
            {
                return;
            }

            await DisconnectAsync();
            _activeWebSocketUrl = resolvedUrl;
            _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _connectionCts.Token;
            _socket = new ClientWebSocket();
            await _socket.ConnectAsync(resolvedUrl, token);
            _receiveTask = ReceiveLoopAsync(token);
        }
        finally
        {
            _connectionLock.Release();
        }

        List<string> symbols;
        lock (_subscriberLock)
        {
            symbols = _subscriberHandlers.Keys
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        foreach (var symbol in symbols)
        {
            await SubscribeSymbolAsync(symbol, cancellationToken);
        }
    }

    private async Task HandlePriceUpdatedAsync(ExchangePriceUpdate update)
    {
        if (!IsLive)
        {
            return;
        }

        var settings = _bybitSettingsOptions.Value;
        _livePriceUpdateInterval = TimeSpan.FromMilliseconds(Math.Max(100, settings.LivePriceUpdateIntervalMilliseconds));
        var now = DateTime.UtcNow;
        if (_lastUpdateUtc.TryGetValue(update.Symbol, out var lastUpdate) &&
            now - lastUpdate < _livePriceUpdateInterval)
        {
            return;
        }

        _lastUpdateUtc[update.Symbol] = now;
        lock (_subscriberLock)
        {
            var cachedUpdate = _lastPriceBySymbol.TryGetValue(update.Symbol, out var previous)
                ? previous
                : null;
            _lastPriceBySymbol[update.Symbol] = new ExchangePriceUpdate(
                update.Exchange,
                update.Symbol,
                update.MarkPrice ?? cachedUpdate?.MarkPrice,
                update.IndexPrice ?? cachedUpdate?.IndexPrice,
                update.Timestamp);
        }

        await DispatchUpdateAsync(update);
    }

    private async Task DispatchUpdateAsync(ExchangePriceUpdate update)
    {
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

        if (_socket is null)
        {
            return;
        }

        if (shouldUnsubscribe)
        {
            await UnsubscribeSymbolAsync(symbol, CancellationToken.None);
        }

        if (_subscriberHandlers.Count == 0)
        {
            await DisconnectAsync();
        }
    }

    private async Task SubscribeSymbolAsync(string symbol, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        var normalized = symbol.Trim();
        if (!_subscribedSymbols.Add(normalized))
        {
            return;
        }

        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            return;
        }

        var subscribePayload = JsonSerializer.Serialize(new
        {
            op = "subscribe",
            args = new[] { $"tickers.{normalized}" }
        });

        var subscribeBytes = Encoding.UTF8.GetBytes(subscribePayload);
        await _socket.SendAsync(subscribeBytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private async Task UnsubscribeSymbolAsync(string symbol, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        var normalized = symbol.Trim();
        if (!_subscribedSymbols.Remove(normalized))
        {
            return;
        }

        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            return;
        }

        var unsubscribePayload = JsonSerializer.Serialize(new
        {
            op = "unsubscribe",
            args = new[] { $"tickers.{normalized}" }
        });

        var unsubscribeBytes = Encoding.UTF8.GetBytes(unsubscribePayload);
        await _socket.SendAsync(unsubscribeBytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private async Task DisconnectAsync()
    {
        if (_connectionCts is not null)
        {
            _connectionCts.Cancel();
            _connectionCts.Dispose();
            _connectionCts = null;
        }

        if (_socket is not null)
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
            }
            catch
            {
                // ignore shutdown errors
            }

            _socket.Dispose();
            _socket = null;
        }

        _subscribedSymbols.Clear();
        _activeWebSocketUrl = null;

        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask;
            }
            catch
            {
                // ignore receive errors on shutdown
            }
            finally
            {
                _receiveTask = null;
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if (_socket is null)
        {
            return;
        }

        var buffer = new byte[4096];
        var segment = new ArraySegment<byte>(buffer);
        var builder = new ArrayBufferWriter<byte>();

        while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            builder.Clear();
            WebSocketReceiveResult? result = null;

            do
            {
                result = await _socket.ReceiveAsync(segment, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                builder.Write(buffer.AsSpan(0, result.Count));
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            var payload = Encoding.UTF8.GetString(builder.WrittenSpan);
            await TryHandleTickerPayloadAsync(payload);
        }
    }

    private async Task TryHandleTickerPayloadAsync(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (!root.TryGetProperty("topic", out var topicElement))
            {
                return;
            }

            var topic = topicElement.GetString();
            if (string.IsNullOrWhiteSpace(topic) || !topic.StartsWith("tickers.", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var topicSymbol = topic.Substring("tickers.".Length);
            if (string.IsNullOrWhiteSpace(topicSymbol))
            {
                return;
            }

            if (!root.TryGetProperty("data", out var dataElement))
            {
                return;
            }

            if (!TryExtractPrices(dataElement, out var markPrice, out var indexPrice, out var symbol))
            {
                return;
            }

            var resolvedSymbol = string.IsNullOrWhiteSpace(symbol) ? topicSymbol : symbol;
            await HandlePriceUpdatedAsync(new ExchangePriceUpdate("Bybit", resolvedSymbol, markPrice, indexPrice, DateTime.UtcNow));
        }
        catch
        {
            // ignore malformed messages
        }
    }

    private static bool TryExtractPrices(JsonElement dataElement, out decimal? markPrice, out decimal? indexPrice, out string? symbol)
    {
        markPrice = null;
        indexPrice = null;
        symbol = null;

        if (dataElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in dataElement.EnumerateArray())
            {
                if (TryExtractPricesFromEntry(entry, out markPrice, out indexPrice, out symbol))
                {
                    return true;
                }
            }

            return false;
        }

        return dataElement.ValueKind == JsonValueKind.Object && TryExtractPricesFromEntry(dataElement, out markPrice, out indexPrice, out symbol);
    }

    private static bool TryExtractPricesFromEntry(JsonElement entry, out decimal? markPrice, out decimal? indexPrice, out string? symbol)
    {
        markPrice = null;
        indexPrice = null;
        symbol = null;

        if (entry.TryReadString("symbol", out var parsedSymbol))
        {
            symbol = parsedSymbol;
        }

        if (entry.TryReadDecimal("markPrice", out var parsedMarkPrice) && parsedMarkPrice > 0)
        {
            markPrice = parsedMarkPrice;
        }

        if (entry.TryReadDecimal("indexPrice", out var parsedIndexPrice) && parsedIndexPrice > 0)
        {
            indexPrice = parsedIndexPrice;
        }

        if (!markPrice.HasValue && entry.TryReadDecimal("lastPrice", out var parsedLastPrice) && parsedLastPrice > 0)
        {
            markPrice = parsedLastPrice;
        }

        return markPrice.HasValue || indexPrice.HasValue;
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
