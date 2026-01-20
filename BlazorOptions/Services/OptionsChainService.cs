using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using BlazorOptions.ViewModels;

namespace BlazorOptions.Services;

public class OptionsChainService
{
    private const string BybitOptionsTickerUrl = "https://api.bybit.com/v5/market/tickers?category=option";
    private static readonly Uri BybitOptionsWebSocketUrl = new("wss://stream.bybit.com/v5/public/option");
    private const string BybitOptionsDocsUrl = "https://bybit-exchange.github.io/docs/v5/market/tickers";
    private static readonly string[] ExpirationFormats = { "ddMMMyy", "ddMMMyyyy" };
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly SemaphoreSlim _socketLock = new(1, 1);
    private readonly object _cacheLock = new();
    private List<OptionChainTicker> _cachedTickers = new();
    private HashSet<string> _trackedSymbols = new(StringComparer.OrdinalIgnoreCase);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _socketCts;
    private Task? _socketTask;
    private Task? _heartbeatTask;
    private readonly HashSet<string> _subscribedSymbols = new(StringComparer.OrdinalIgnoreCase);
    private int _reconnectState;
    private static readonly TimeSpan ReconnectDelayMin = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReconnectDelayMax = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    public OptionsChainService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public DateTime? LastUpdatedUtc { get; private set; }

    public bool IsRefreshing { get; private set; }

    public event Action? ChainUpdated;
    public event Action<OptionChainTicker>? TickerUpdated;

    public IReadOnlyList<OptionChainTicker> GetSnapshot()
    {
        lock (_cacheLock)
        {
            return _cachedTickers.ToList();
        }
    }

    public IReadOnlyCollection<string> GetTrackedSymbols()
    {
        lock (_cacheLock)
        {
            return _trackedSymbols.ToList();
        }
    }

    public async Task RefreshAsync(string? baseAsset = null, CancellationToken cancellationToken = default)
    {
        if (!await _refreshLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            IsRefreshing = true;
            var updated = await FetchTickersAsync(baseAsset, cancellationToken);

            if (updated.Count > 0)
            {
                lock (_cacheLock)
                {
                    _cachedTickers = updated;
                }

                LastUpdatedUtc = DateTime.UtcNow;
                ChainUpdated?.Invoke();
            }
        }
        finally
        {
            IsRefreshing = false;
            _refreshLock.Release();
        }
    }

    public OptionChainTicker? FindTickerForLeg(LegModel leg, string? baseAsset)
    {
        var snapshot = GetSnapshot();

        if (string.IsNullOrWhiteSpace(baseAsset) || !leg.ExpirationDate.HasValue || !leg.Strike.HasValue)
        {
            return null;
        }

        var expiration = leg.ExpirationDate.Value.Date;
        var strike = leg.Strike.Value;

        return snapshot.FirstOrDefault(ticker =>
            string.Equals(ticker.BaseAsset, baseAsset, StringComparison.OrdinalIgnoreCase)
            && ticker.Type == leg.Type
            && ticker.ExpirationDate.Date == expiration
            && Math.Abs(ticker.Strike - strike) < 0.01);
    }

    public void TrackLegs(IEnumerable<LegModel> legs, string? baseAsset)
    {
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

    private static List<OptionChainTicker> ParseTickersFromDocument(JsonDocument? document)
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
            if (!TryReadString(entry, "symbol", out var symbol))
            {
                continue;
            }

            if (!TryParseSymbol(symbol, out var baseAsset, out var expirationDate, out var strike, out var type))
            {
                continue;
            }

            var markPrice = ReadDouble(entry, "markPrice");
            var lastPrice = ReadDouble(entry, "lastPrice");
            var markIv = ReadDouble(entry, "markIv");
            var bidPrice = ReadDouble(entry, "bid1Price");
            var askPrice = ReadDouble(entry, "ask1Price");
            var bidIv = ReadDouble(entry, "bid1Iv");
            var askIv = ReadDouble(entry, "ask1Iv");
            var delta = ReadNullableDouble(entry, "delta");
            var gamma = ReadNullableDouble(entry, "gamma");
            var vega = ReadNullableDouble(entry, "vega");
            var theta = ReadNullableDouble(entry, "theta");

            tickers.Add(new OptionChainTicker(
                symbol,
                baseAsset,
                expirationDate,
                strike,
                type,
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
                theta));
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

    private static bool TryParseSymbol(string symbol, out string baseAsset, out DateTime expiration, out double strike, out LegType type)
    {
        baseAsset = string.Empty;
        expiration = default;
        strike = 0;
        type = LegType.Call;

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

        if (!double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out strike))
        {
            return false;
        }

        var typeToken = parts[3].Trim();
        type = typeToken.Equals("P", StringComparison.OrdinalIgnoreCase) ? LegType.Put : LegType.Call;
        return true;
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

    private static double ReadDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        var raw = property.ValueKind == JsonValueKind.String ? property.GetString() : property.GetRawText();

        return double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private async Task EnsureWebSocketSubscriptionsAsync(IEnumerable<string> symbols)
    {
        var symbolList = symbols
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
            .Distinct(StringComparer.OrdinalIgnoreCase)
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
            TryHandleTickerPayload(payload);
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

    private void TryHandleTickerPayload(string payload)
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
                    var updatedTicker = UpdateTickerFromPayload(entry, topicSymbol);
                    if (updatedTicker is not null)
                    {
                        TickerUpdated?.Invoke(updatedTicker);
                    }
                }

                return;
            }

            if (dataElement.ValueKind == JsonValueKind.Object)
            {
                var updatedTicker = UpdateTickerFromPayload(dataElement, topicSymbol);
                if (updatedTicker is not null)
                {
                    TickerUpdated?.Invoke(updatedTicker);
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

    private OptionChainTicker? UpdateTickerFromPayload(JsonElement entry, string? topicSymbol)
    {
        var symbol = TryReadString(entry, "symbol", out var parsedSymbol)
            ? parsedSymbol
            : topicSymbol ?? string.Empty;

        if (string.IsNullOrWhiteSpace(symbol))
        {
            return null;
        }

        var markPrice = ReadDouble(entry, "markPrice");
        var lastPrice = ReadDouble(entry, "lastPrice");
        var markIv = ReadDouble(entry, "markIv");
        var bidPrice = ReadDouble(entry, "bidPrice");
        var askPrice = ReadDouble(entry, "askPrice");
        var bidIv = ReadDouble(entry, "bidIv");
        var askIv = ReadDouble(entry, "askIv");
        var delta = ReadNullableDouble(entry, "delta");
        var gamma = ReadNullableDouble(entry, "gamma");
        var vega = ReadNullableDouble(entry, "vega");
        var theta = ReadNullableDouble(entry, "theta");

        lock (_cacheLock)
        {
            var index = _cachedTickers.FindIndex(ticker => string.Equals(ticker.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                var existing = _cachedTickers[index];
                var updated = new OptionChainTicker(
                    existing.Symbol,
                    existing.BaseAsset,
                    existing.ExpirationDate,
                    existing.Strike,
                    existing.Type,
                    markPrice > 0 ? markPrice : existing.MarkPrice,
                    lastPrice > 0 ? lastPrice : existing.LastPrice,
                    markIv > 0 ? markIv : existing.MarkIv,
                    bidPrice > 0 ? bidPrice : existing.BidPrice,
                    askPrice > 0 ? askPrice : existing.AskPrice,
                    bidIv > 0 ? bidIv : existing.BidIv,
                    askIv > 0 ? askIv : existing.AskIv,
                    delta ?? existing.Delta,
                    gamma ?? existing.Gamma,
                    vega ?? existing.Vega,
                    theta ?? existing.Theta);
                _cachedTickers[index] = updated;
                return updated;
            }

            if (!TryParseSymbol(symbol, out var baseAsset, out var expirationDate, out var strike, out var type))
            {
                return null;
            }

            var created = new OptionChainTicker(
                symbol,
                baseAsset,
                expirationDate,
                strike,
                type,
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
            _cachedTickers.Add(created);
            return created;
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

    private static double? ReadNullableDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        var raw = property.ValueKind == JsonValueKind.String ? property.GetString() : property.GetRawText();
        if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return null;
    }
}






