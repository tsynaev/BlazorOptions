using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using BlazorOptions.ViewModels;

namespace BlazorOptions.Services;

public sealed class BybitOptionMarketDataService : IOptionMarketDataService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReconnectDelayMin = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReconnectDelayMax = TimeSpan.FromSeconds(30);
    private readonly HttpClient _httpClient;
    private readonly IOptions<BybitSettings> _bybitSettingsOptions;
    private readonly SemaphoreSlim _socketLock = new(1, 1);
    private readonly object _subscriberLock = new();
    private readonly Dictionary<string, List<Func<OptionChainTicker, Task>>> _subscriberHandlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _subscribedSymbols = new(StringComparer.OrdinalIgnoreCase);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _socketCts;
    private Task? _socketTask;
    private Task? _heartbeatTask;
    private int _reconnectState;
    private bool _disposed;

    public BybitOptionMarketDataService(
        HttpClient httpClient,
        IOptions<BybitSettings> bybitSettingsOptions)
    {
        _httpClient = httpClient;
        _bybitSettingsOptions = bybitSettingsOptions;
    }

    public async Task<IReadOnlyList<OptionChainTicker>> GetTickersAsync(string? baseAsset = null, CancellationToken cancellationToken = default)
    {
        var requestUrl = BuildTickerUrl(baseAsset);
        using var response = await _httpClient.GetAsync(requestUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return ParseTickersFromDocument(document);
    }

    public IReadOnlyList<ExchangeTradingPair> GetConfiguredTradingPairs()
    {
        var settings = _bybitSettingsOptions.Value;
        var bases = ParseAssetList(settings.OptionBaseCoins, ["BTC", "ETH", "SOL"]);
        var quotes = ParseAssetList(settings.OptionQuoteCoins, ["USDT"]);
        var pairs = new List<ExchangeTradingPair>(bases.Count * quotes.Count);

        foreach (var baseAsset in bases)
        {
            foreach (var quoteAsset in quotes)
            {
                pairs.Add(new ExchangeTradingPair(baseAsset, quoteAsset));
            }
        }

        return pairs;
    }

    public async ValueTask<IDisposable> SubscribeAsync(
        string symbol,
        Func<OptionChainTicker, Task> handler,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol) || handler is null)
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

            handlers.Add(handler);
        }

        if (shouldSubscribe)
        {
            await EnsureWebSocketSubscriptionsAsync(new[] { symbol }, cancellationToken);
        }

        return new SubscriptionRegistration(() => _ = UnsubscribeAsync(symbol, handler));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopSocketAsync();
        _socketLock.Dispose();
    }

    private string BuildTickerUrl(string? baseAsset)
    {
        var baseUrl = _bybitSettingsOptions.Value.OptionTickersUri;
        return string.IsNullOrWhiteSpace(baseAsset)
            ? baseUrl.ToString()
            : $"{baseUrl}&baseCoin={Uri.EscapeDataString(baseAsset.Trim())}";
    }

    private async Task EnsureWebSocketSubscriptionsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken)
    {
        var symbolList = symbols
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
            .Select(symbol => symbol.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (symbolList.Count == 0)
        {
            return;
        }

        await _socketLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureSocketAsync(cancellationToken);
            if (_socket is null || _socket.State != WebSocketState.Open)
            {
                return;
            }

            var newSymbols = symbolList
                .Where(symbol => !_subscribedSymbols.Contains(symbol))
                .ToList();

            if (newSymbols.Count == 0)
            {
                return;
            }

            var subscribePayload = JsonSerializer.Serialize(new
            {
                op = "subscribe",
                args = newSymbols.Select(symbol => $"tickers.{symbol}")
            });

            await _socket.SendAsync(
                Encoding.UTF8.GetBytes(subscribePayload),
                WebSocketMessageType.Text,
                true,
                _socketCts?.Token ?? cancellationToken);

            foreach (var symbol in newSymbols)
            {
                _subscribedSymbols.Add(symbol);
            }
        }
        catch
        {
            // Live option-chain transport is best effort; cached REST data remains usable.
        }
        finally
        {
            _socketLock.Release();
        }
    }

    private async Task EnsureSocketAsync(CancellationToken cancellationToken)
    {
        if (_socket is not null && _socket.State == WebSocketState.Open)
        {
            return;
        }

        await StopSocketAsync();

        _socketCts = new CancellationTokenSource();
        _socket = new ClientWebSocket();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_socketCts.Token, cancellationToken);
        await _socket.ConnectAsync(ResolveOptionsWebSocketUrl(), linkedCts.Token);
        _socketTask = ReceiveLoopAsync(_socket, _socketCts.Token);
        _heartbeatTask = SendHeartbeatLoopAsync(_socket, _socketCts.Token);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var segment = new ArraySegment<byte>(buffer);
        var builder = new ArrayBufferWriter<byte>();

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            builder.Clear();
            WebSocketReceiveResult? result;

            do
            {
                result = await socket.ReceiveAsync(segment, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        TriggerReconnect();
                    }

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
            await TryHandleTickerPayload(payload);
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            TriggerReconnect();
        }
    }

    private async Task SendHeartbeatLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var heartbeatPayload = Encoding.UTF8.GetBytes("{\"op\":\"ping\"}");

        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, cancellationToken);
                if (cancellationToken.IsCancellationRequested || socket.State != WebSocketState.Open)
                {
                    break;
                }

                await socket.SendAsync(heartbeatPayload, WebSocketMessageType.Text, true, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    TriggerReconnect();
                }

                break;
            }
        }
    }

    private async Task TryHandleTickerPayload(string payload)
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

            if (!root.TryGetProperty("data", out var dataElement))
            {
                return;
            }

            if (dataElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in dataElement.EnumerateArray())
                {
                    await DispatchTickerAsync(entry);
                }

                return;
            }

            if (dataElement.ValueKind == JsonValueKind.Object)
            {
                await DispatchTickerAsync(dataElement);
            }
        }
        catch
        {
            // Ignore malformed or unsupported websocket messages.
        }
    }

    private async Task DispatchTickerAsync(JsonElement entry)
    {
        var ticker = ReadTickerFromPayload(entry);
        if (ticker is null)
        {
            return;
        }

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
                // One subscriber must not block the shared exchange stream.
            }
        }
    }

    private void TriggerReconnect()
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _reconnectState, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var delay = ReconnectDelayMin;

            while (!_disposed)
            {
                try
                {
                    await Task.Delay(delay);
                    var symbols = GetSubscribedHandlerSymbols();
                    if (symbols.Count == 0)
                    {
                        break;
                    }

                    await EnsureWebSocketSubscriptionsAsync(symbols, CancellationToken.None);
                    break;
                }
                catch
                {
                    var nextSeconds = Math.Min(delay.TotalSeconds * 2, ReconnectDelayMax.TotalSeconds);
                    delay = TimeSpan.FromSeconds(nextSeconds);
                }
            }

            Interlocked.Exchange(ref _reconnectState, 0);
        });
    }

    private IReadOnlyList<string> GetSubscribedHandlerSymbols()
    {
        lock (_subscriberLock)
        {
            return _subscriberHandlers.Keys.ToArray();
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

        if (!shouldUnsubscribe)
        {
            return;
        }

        await _socketLock.WaitAsync();
        try
        {
            if (_socket is null || _socket.State != WebSocketState.Open)
            {
                _subscribedSymbols.Remove(symbol);
                return;
            }

            if (!_subscribedSymbols.Contains(symbol))
            {
                return;
            }

            var unsubscribePayload = JsonSerializer.Serialize(new
            {
                op = "unsubscribe",
                args = new[] { $"tickers.{symbol}" }
            });

            await _socket.SendAsync(
                Encoding.UTF8.GetBytes(unsubscribePayload),
                WebSocketMessageType.Text,
                true,
                _socketCts?.Token ?? CancellationToken.None);
            _subscribedSymbols.Remove(symbol);
        }
        catch
        {
            // Ignore shutdown/unsubscribe transport failures.
        }
        finally
        {
            _socketLock.Release();
        }
    }

    private async Task StopSocketAsync()
    {
        if (_socketCts is not null)
        {
            _socketCts.Cancel();
            _socketCts.Dispose();
            _socketCts = null;
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
                // Ignore socket shutdown errors.
            }

            _socket.Dispose();
            _socket = null;
        }

        if (_heartbeatTask is not null)
        {
            try
            {
                await _heartbeatTask;
            }
            catch
            {
                // Ignore heartbeat errors on shutdown.
            }
            finally
            {
                _heartbeatTask = null;
            }
        }

        if (_socketTask is not null)
        {
            try
            {
                await _socketTask;
            }
            catch
            {
                // Ignore receive errors on shutdown.
            }
            finally
            {
                _socketTask = null;
            }
        }

        _subscribedSymbols.Clear();
    }

    private Uri ResolveOptionsWebSocketUrl()
    {
        return _bybitSettingsOptions.Value.OptionPublicWebSocketUrl;
    }

    private List<OptionChainTicker> ParseTickersFromDocument(JsonDocument? document)
    {
        if (document is null)
        {
            return [];
        }

        ThrowIfRetCodeError(document.RootElement);

        if (!document.RootElement.TryGetProperty("result", out var resultElement))
        {
            return [];
        }

        if (!resultElement.TryGetProperty("list", out var listElement) || listElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var tickers = new List<OptionChainTicker>();

        foreach (var entry in listElement.EnumerateArray())
        {
            var ticker = ReadTickerFromPayload(entry);
            if (ticker is not null)
            {
                tickers.Add(ticker);
            }
        }

        return tickers;
    }

    private OptionChainTicker? ReadTickerFromPayload(JsonElement entry)
    {
        if (!entry.TryReadString("symbol", out var symbol) || string.IsNullOrWhiteSpace(symbol))
        {
            return null;
        }

        if (!BybitSymbolMapper.TryParseSymbol(symbol, out var baseAsset, out var expirationDate, out var strike, out var legType))
        {
            return null;
        }

        return new OptionChainTicker(
            symbol,
            baseAsset,
            expirationDate,
            strike,
            legType,
            entry.ReadDecimal("underlyingPrice"),
            entry.ReadDecimal("markPrice"),
            entry.ReadDecimal("lastPrice"),
            entry.ReadDecimal("markIv"),
            entry.ReadDecimal("bidPrice", "bid1Price"),
            entry.ReadDecimal("askPrice", "ask1Price"),
            entry.ReadDecimal("bidIv", "bid1Iv"),
            entry.ReadDecimal("askIv", "ask1Iv"),
            entry.ReadNullableDecimal("delta"),
            entry.ReadNullableDecimal("gamma"),
            entry.ReadNullableDecimal("vega"),
            entry.ReadNullableDecimal("theta"),
            entry.ReadNullableDecimal("openInterest"));
    }

    private static void ThrowIfRetCodeError(JsonElement rootElement)
    {
        if (!rootElement.TryGetProperty("retCode", out var retCodeElement))
        {
            return;
        }

        if (!retCodeElement.TryReadInt(out var retCode) || retCode == 0)
        {
            return;
        }

        var message = rootElement.TryGetProperty("retMsg", out var retMsgElement)
            ? retMsgElement.GetString()
            : null;
        throw new InvalidOperationException($"Bybit API error {retCode}: {message ?? "Unknown error"}");
    }

    private static IReadOnlyList<string> ParseAssetList(string? raw, IReadOnlyList<string> fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        var parsed = Regex.Split(raw, "[^a-zA-Z0-9]+")
            .Select(x => x.Trim().ToUpperInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return parsed.Length > 0 ? parsed : fallback;
    }

}
