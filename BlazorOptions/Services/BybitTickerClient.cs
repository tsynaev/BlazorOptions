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

    public string Exchange => "Bybit";

    public event EventHandler<ExchangePriceUpdate>? PriceUpdated;

    public async Task ConnectAsync(ExchangeTickerSubscription subscription, CancellationToken cancellationToken)
    {
        await DisconnectAsync();

        _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _connectionCts.Token;

        _socket = new ClientWebSocket();

        await _socket.ConnectAsync(subscription.WebSocketUrl, token);

        var subscribePayload = JsonSerializer.Serialize(new
        {
            op = "subscribe",
            args = new[] { $"tickers.{subscription.Symbol}" }
        });

        var subscribeBytes = Encoding.UTF8.GetBytes(subscribePayload);
        await _socket.SendAsync(subscribeBytes, WebSocketMessageType.Text, true, token);

        _receiveTask = ReceiveLoopAsync(subscription, token);
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

    private async Task ReceiveLoopAsync(ExchangeTickerSubscription subscription, CancellationToken cancellationToken)
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
            TryHandleTickerPayload(subscription, payload);
        }
    }

    private void TryHandleTickerPayload(ExchangeTickerSubscription subscription, string payload)
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

            if (TryExtractPrice(dataElement, out var price))
            {
                PriceUpdated?.Invoke(this, new ExchangePriceUpdate(subscription.Exchange, subscription.Symbol, price, DateTime.UtcNow));
            }
        }
        catch
        {
            // ignore malformed messages
        }
    }

    private static bool TryExtractPrice(JsonElement dataElement, out decimal price)
    {
        price = 0m;

        if (dataElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in dataElement.EnumerateArray())
            {
                if (TryExtractPriceFromEntry(entry, out price))
                {
                    return true;
                }
            }

            return false;
        }

        return dataElement.ValueKind == JsonValueKind.Object && TryExtractPriceFromEntry(dataElement, out price);
    }

    private static bool TryExtractPriceFromEntry(JsonElement entry, out decimal price)
    {
        price = 0m;

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
