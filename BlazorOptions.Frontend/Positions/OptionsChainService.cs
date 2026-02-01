using BlazorOptions.ViewModels;
using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlazorOptions.Services;

public class OptionsChainService
{
    private const string BybitOptionsTickerUrl = "https://api.bybit.com/v5/market/tickers?category=option";
    private static readonly Uri BybitOptionsWebSocketUrl = new("wss://stream.bybit.com/v5/public/option");
    private const string BybitOptionsDocsUrl = "https://bybit-exchange.github.io/docs/v5/market/tickers";
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly SemaphoreSlim _socketLock = new(1, 1);
    private readonly object _cacheLock = new();
    private readonly object _subscriberLock = new();
    private Dictionary<string, List<OptionChainTicker>> _cachedTickers = new();
    private HashSet<string> _trackedSymbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Func<OptionChainTicker, Task>>>
        _subscriberHandlers = new(StringComparer.OrdinalIgnoreCase);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _socketCts;
    private Task? _socketTask;
    private Task? _heartbeatTask;
    private readonly HashSet<string> _subscribedSymbols = new(StringComparer.OrdinalIgnoreCase);
    private int _reconnectState;
    private static readonly TimeSpan ReconnectDelayMin = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReconnectDelayMax = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);
  
    private readonly IExchangeService _exchangeService;
    private ITelemetryService _telemetryService;

    public OptionsChainService(HttpClient httpClient, IExchangeService exchangeService, ITelemetryService telemetryService)
    {
        _httpClient = httpClient;
        _exchangeService = exchangeService;
        _telemetryService = telemetryService;
    }

    public DateTime? LastUpdatedUtc { get; private set; }

    public bool IsRefreshing { get; private set; }

  



    public List<OptionChainTicker> GetTickersByBaseAsset(string baseAsset, LegType? legType = null)
    {
        using var activity = _telemetryService.StartActivity("OptionsChainService.GetTickersByBaseAsset");

        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            return [];
        }

        var normalizedBase = baseAsset.Trim().ToUpperInvariant();


        if (!_cachedTickers.TryGetValue(normalizedBase, out List<OptionChainTicker>? tickers))
        {
            return new List<OptionChainTicker>();
        }

        if (legType.HasValue)
        {

            return tickers.Where(x => x.Type == legType).ToList();
        }

        return tickers;

    }



    public IReadOnlyCollection<string> GetTrackedSymbols()
    {
        lock (_cacheLock)
        {
            return _trackedSymbols.ToList();
        }
    }

    public async Task EnsureBaseAssetAsync(string baseAsset)
    {
        if (!_cachedTickers.TryGetValue(baseAsset, out _))
        {
            await RefreshAsync(baseAsset);
        }
    }

    public async Task RefreshAsync(string? baseAsset = null, CancellationToken cancellationToken = default)
    {
        
        if (!await _refreshLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        using var activity = _telemetryService.StartActivity("OptionsChainService.RefreshAsync");

        try
        {
            IsRefreshing = true;

            string[] assets;
            if (string.IsNullOrEmpty(baseAsset))
            {
                assets = _cachedTickers.Keys.ToArray();
            }
            else
            {
                assets = [baseAsset];
            }


            foreach (var asset in assets)
            {
                var updated = await FetchTickersAsync(asset, cancellationToken);

                if (updated.Count > 0)
                {
                    lock (_cacheLock)
                    {
                        _cachedTickers.Remove(asset);
                        _cachedTickers.Add(asset, updated);
                    }

                    LastUpdatedUtc = DateTime.UtcNow;
                }
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
        if (string.IsNullOrEmpty(leg.Symbol)) return null;

        if (string.IsNullOrEmpty(baseAsset))
        {
            _exchangeService.TryParseSymbol(leg.Symbol, out baseAsset ,out _,out _, out _ );
        }

        var tickers = GetTickersByBaseAsset(baseAsset);

        return tickers.FirstOrDefault(ticker => string.Equals(ticker.Symbol, leg.Symbol, StringComparison.OrdinalIgnoreCase));
    }

    public void TrackLegs(IEnumerable<LegModel> legs, string? baseAsset = null)
    {
        var symbols = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        foreach (var leg in legs)
        {
            var ticker = FindTickerForLeg(leg, baseAsset);
            if (ticker is not null)
            {
                symbols.Add(ticker.Symbol);
            }
        }

        lock (_cacheLock)
        {
            _trackedSymbols = symbols;
        }

        _ = EnsureWebSocketSubscriptionsAsync(symbols);
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

        if (shouldSubscribe)
        {
            await EnsureWebSocketSubscriptionsAsync(new[] { symbol });
        }

        return new SubscriptionRegistration(() => _ = UnsubscribeAsync(symbol, when));
    }

    private async Task<List<OptionChainTicker>> FetchTickersAsync(string? baseAsset, CancellationToken cancellationToken)
    {
        try
        {
            var requestUrl = BuildTickerUrl(baseAsset);
            using var response = await _httpClient.GetAsync(requestUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return await FetchTickersFromDocumentationAsync(cancellationToken);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            return ParseTickersFromDocument(document);
        }
        catch
        {
            return await FetchTickersFromDocumentationAsync(cancellationToken);
        }
    }

    private static string BuildTickerUrl(string? baseAsset)
    {
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            return BybitOptionsTickerUrl;
        }

        return $"{BybitOptionsTickerUrl}&baseCoin={Uri.EscapeDataString(baseAsset.Trim())}";
    }

    private List<OptionChainTicker> ParseTickersFromDocument(JsonDocument? document)
    {
        if (document is null)
        {
            return new List<OptionChainTicker>();
        }

        ThrowIfRetCodeError(document.RootElement);

        if (!document.RootElement.TryGetProperty("result", out var resultElement))
        {
            return new List<OptionChainTicker>();
        }

        if (!resultElement.TryGetProperty("list", out var listElement) || listElement.ValueKind != JsonValueKind.Array)
        {
            return new List<OptionChainTicker>();
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

    private static void ThrowIfRetCodeError(JsonElement rootElement)
    {
        if (!rootElement.TryGetProperty("retCode", out var retCodeElement))
        {
            return;
        }

        if (!TryReadInt(retCodeElement, out var retCode) || retCode == 0)
        {
            return;
        }

        var message = rootElement.TryGetProperty("retMsg", out var retMsgElement)
            ? retMsgElement.GetString()
            : null;
        throw new InvalidOperationException($"Bybit API error {retCode}: {message ?? "Unknown error"}");
    }

    private static bool TryReadInt(JsonElement element, out int value)
    {
        value = 0;

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var raw = element.GetString();
        return int.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private async Task<List<OptionChainTicker>> FetchTickersFromDocumentationAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(BybitOptionsDocsUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new List<OptionChainTicker>();
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var codeBlocks = Regex.Matches(html, "<code[^>]*>(.*?)</code>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

            foreach (Match match in codeBlocks)
            {
                var rawBlock = match.Groups[1].Value;
                var withoutTags = Regex.Replace(rawBlock, "<[^>]+>", string.Empty);
                var decoded = WebUtility.HtmlDecode(withoutTags);

                if (!decoded.Contains("\"category\"", StringComparison.OrdinalIgnoreCase) || !decoded.Contains("\"option\"", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var document = TryParseJson(decoded);
                if (document is null)
                {
                    continue;
                }

                using (document)
                {
                    var tickers = ParseTickersFromDocument(document);
                    if (tickers.Count > 0)
                    {
                        return tickers;
                    }
                }
            }

            return new List<OptionChainTicker>();
        }
        catch
        {
            return new List<OptionChainTicker>();
        }
    }

    private static JsonDocument? TryParseJson(string content)
    {
        try
        {
            return JsonDocument.Parse(content);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryReadString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;

        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        value = property.ValueKind == JsonValueKind.String ? property.GetString() ?? string.Empty : property.GetRawText();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static decimal ReadDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        var raw = property.ValueKind == JsonValueKind.String ? property.GetString() : property.GetRawText();

        return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private async Task EnsureWebSocketSubscriptionsAsync(IEnumerable<string> symbols)
    {
        var symbolList = symbols
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (symbolList.Count == 0)
        {
            return;
        }

        await _socketLock.WaitAsync();
        try
        {
            await EnsureSocketAsync();

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

            var subscribeBytes = Encoding.UTF8.GetBytes(subscribePayload);
            await _socket.SendAsync(subscribeBytes, WebSocketMessageType.Text, true, _socketCts?.Token ?? CancellationToken.None);

            foreach (var symbol in newSymbols)
            {
                _subscribedSymbols.Add(symbol);
            }
        }
        catch
        {
            // ignore websocket subscription errors
        }
        finally
        {
            _socketLock.Release();
        }
    }

    private async Task EnsureSocketAsync()
    {
        if (_socket is not null && _socket.State == WebSocketState.Open)
        {
            return;
        }

        await StopSocketAsync();

        _socketCts = new CancellationTokenSource();
        _socket = new ClientWebSocket();
        await _socket.ConnectAsync(BybitOptionsWebSocketUrl, _socketCts.Token);
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
            WebSocketReceiveResult? result = null;

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

            var topicSymbol = topic["tickers.".Length..];

            if (dataElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in dataElement.EnumerateArray())
                {
                    var updatedTicker = UpdateTickerFromPayload(entry);
                    if (updatedTicker is not null)
                    {
                        await DispatchSubscriberHandlers(updatedTicker);
                    }
                }

                return;
            }

            if (dataElement.ValueKind == JsonValueKind.Object)
            {
                var updatedTicker = UpdateTickerFromPayload(dataElement);
                if (updatedTicker is not null)
                {
                    await DispatchSubscriberHandlers(updatedTicker);
                }
            }
        }
        catch
        {
            // ignore malformed websocket messages
        }
    }

    private void TriggerReconnect()
    {
        if (Interlocked.Exchange(ref _reconnectState, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var delay = ReconnectDelayMin;

            while (true)
            {
                try
                {
                    await Task.Delay(delay);
                    var symbols = GetTrackedSymbols();
                    if (symbols.Count == 0)
                    {
                        delay = ReconnectDelayMin;
                        continue;
                    }

                    await EnsureWebSocketSubscriptionsAsync(symbols);
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

    private OptionChainTicker? UpdateTickerFromPayload(JsonElement entry)
    {
        var ticker = ReadTickerFromPayload(entry);
        if (ticker is null)
        {
            return null;
        }

        if (!_cachedTickers.TryGetValue(ticker.BaseAsset, out var tickers))
        {
            tickers = new List<OptionChainTicker>();
            _cachedTickers.Add(ticker.BaseAsset, tickers);
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

        return ticker;
    }


    private OptionChainTicker? ReadTickerFromPayload(JsonElement entry)
    {
        if (!TryReadString(entry, "symbol", out var symbol) || string.IsNullOrWhiteSpace(symbol))
        {
            return null;
        }

        if (!_exchangeService.TryParseSymbol(symbol, out var baseAsset, out var expirationDate, out var strike,
                out var legType))
        {
            return null;
        }


        var underlyingPrice = ReadDecimal(entry, "underlyingPrice");
        var markPrice = ReadDecimal(entry, "markPrice");
        var lastPrice = ReadDecimal(entry, "lastPrice");
        var markIv = ReadDecimal(entry, "markIv");
        var bidPrice = ReadDecimal(entry, "bidPrice");
        var askPrice = ReadDecimal(entry, "askPrice");
        var bidIv = ReadDecimal(entry, "bidIv");
        var askIv = ReadDecimal(entry, "askIv");
        var delta = ReadNullableDecimal(entry, "delta");
        var gamma = ReadNullableDecimal(entry, "gamma");
        var vega = ReadNullableDecimal(entry, "vega");
        var theta = ReadNullableDecimal(entry, "theta");


        var created = new OptionChainTicker(
            symbol,
            baseAsset,
            expirationDate,
            strike,
            legType,
            underlyingPrice,
            markPrice,
            lastPrice,
            markIv,
            bidPrice,
            askPrice,
            bidIv,
            askIv,
            delta,
            gamma,
            vega,
            theta);


        return created;

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
                // ignore shutdown errors
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
                // ignore heartbeat errors on shutdown
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
                // ignore receive errors on shutdown
            }
            finally
            {
                _socketTask = null;
            }
        }

        _subscribedSymbols.Clear();
    }

    private static decimal? ReadNullableDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        var raw = property.ValueKind == JsonValueKind.String ? property.GetString() : property.GetRawText();
        if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return null;
    }

    private async Task DispatchSubscriberHandlers(OptionChainTicker ticker)
    {
        List<Func<OptionChainTicker, Task>>? handlers;
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
                // ignore subscriber errors
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

        if (!shouldUnsubscribe)
        {
            return;
        }

        if (IsSymbolTracked(symbol))
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

            var unsubscribeBytes = Encoding.UTF8.GetBytes(unsubscribePayload);
            await _socket.SendAsync(unsubscribeBytes, WebSocketMessageType.Text, true, _socketCts?.Token ?? CancellationToken.None);
            _subscribedSymbols.Remove(symbol);
        }
        catch
        {
            // ignore websocket unsubscribe errors
        }
        finally
        {
            _socketLock.Release();
        }
    }

    private bool IsSymbolTracked(string symbol)
    {
        lock (_cacheLock)
        {
            return _trackedSymbols.Contains(symbol);
        }
    }


  

}
