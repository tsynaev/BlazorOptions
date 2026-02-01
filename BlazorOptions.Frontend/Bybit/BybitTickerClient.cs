using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace BlazorOptions.Services;

public class BybitTickerClient : IExchangeTickerClient
{
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _connectionCts;
    private Task? _receiveTask;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly HashSet<string> _subscribedSymbols = new(StringComparer.OrdinalIgnoreCase);
    private Uri? _activeUrl;

    public string Exchange => "Bybit";

    public event Func<ExchangePriceUpdate, Task>? PriceUpdated;

    public async Task EnsureConnectedAsync(Uri webSocketUrl, CancellationToken cancellationToken)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_socket is not null && _socket.State == WebSocketState.Open && _activeUrl == webSocketUrl)
            {
                return;
            }

            await DisconnectAsync();

            _activeUrl = webSocketUrl;
            _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _connectionCts.Token;

            _socket = new ClientWebSocket();
            await _socket.ConnectAsync(webSocketUrl, token);
            _receiveTask = ReceiveLoopAsync(token);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task SubscribeAsync(string symbol, CancellationToken cancellationToken)
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

    public async Task UnsubscribeAsync(string symbol, CancellationToken cancellationToken)
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

    public async Task DisconnectAsync()
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
        _activeUrl = null;

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
            await TryHandleTickerPayload(payload);
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

            var topicSymbol = topic.Substring("tickers.".Length);
            if (string.IsNullOrWhiteSpace(topicSymbol))
            {
                return;
            }

            if (!root.TryGetProperty("data", out var dataElement))
            {
                return;
            }

            if (TryExtractPrice(dataElement, out var price, out var symbol))
            {
                var resolvedSymbol = string.IsNullOrWhiteSpace(symbol) ? topicSymbol : symbol;
                var handler = PriceUpdated;
                if (handler is not null)
                {
                    await handler.Invoke(new ExchangePriceUpdate(Exchange, resolvedSymbol, price, DateTime.UtcNow));
                }
            }
        }
        catch
        {
            // ignore malformed messages
        }
    }

    private static bool TryExtractPrice(JsonElement dataElement, out decimal price, out string? symbol)
    {
        price = 0m;
        symbol = null;

        if (dataElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in dataElement.EnumerateArray())
            {
                if (TryExtractPriceFromEntry(entry, out price, out symbol))
                {
                    return true;
                }
            }

            return false;
        }

        return dataElement.ValueKind == JsonValueKind.Object && TryExtractPriceFromEntry(dataElement, out price, out symbol);
    }

    private static bool TryExtractPriceFromEntry(JsonElement entry, out decimal price, out string? symbol)
    {
        price = 0m;
        symbol = null;

        if (TryReadString(entry, "symbol", out var parsedSymbol))
        {
            symbol = parsedSymbol;
        }

        if (TryReadDecimal(entry, "indexPrice", out price))
        {
            return true;
        }

        if (TryReadDecimal(entry, "lastPrice", out price))
        {
            return true;
        }

        return false;
    }

    private static bool TryReadString(JsonElement entry, string propertyName, out string? value)
    {
        value = null;
        if (!entry.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        value = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            _ => property.GetRawText()
        };

        value = value?.Trim();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryReadDecimal(JsonElement entry, string propertyName, out decimal value)
    {
        value = 0m;

        if (!entry.TryGetProperty(propertyName, out var element))
        {
            return false;
        }

        var raw = element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();

        return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }
}
