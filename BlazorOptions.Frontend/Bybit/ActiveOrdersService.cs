using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlazorOptions.ViewModels;
using Microsoft.Extensions.Options;

namespace BlazorOptions.Services;

public sealed class ActiveOrdersService : IOrdersService, IAsyncDisposable
{
    private static readonly Uri BybitPrivateWebSocketUrl = new("wss://stream.bybit.com/v5/private");
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    private readonly BybitOrderService _bybitOrderService;
    private readonly IOptions<BybitSettings> _bybitSettingsOptions;
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);
    private readonly object _subscriberLock = new();
    private readonly List<Func<IReadOnlyList<ExchangeOrder>, Task>> _subscribers = new();
    private readonly List<ExchangeOrder> _orders = new();
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _socketCts;
    private Task? _socketTask;
    private Task? _heartbeatTask;
    private bool _isInitialized;
    private Task _snapshotTask = Task.CompletedTask;

    public ActiveOrdersService(
        BybitOrderService bybitOrderService,
        IOptions<BybitSettings> bybitSettingsOptions)
    {
        _bybitOrderService = bybitOrderService;
        _bybitSettingsOptions = bybitSettingsOptions;
    }

    public async Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync();
        await _snapshotTask;

        await _sync.WaitAsync(cancellationToken);
        try
        {
            return _orders.ToArray();
        }
        finally
        {
            _sync.Release();
        }
    }

    public async ValueTask<IDisposable> SubscribeAsync(
        Func<IReadOnlyList<ExchangeOrder>, Task> handler,
        CancellationToken cancellationToken = default)
    {
        if (handler is null)
        {
            return new SubscriptionRegistration(() => { });
        }

        lock (_subscriberLock)
        {
            _subscribers.Add(handler);
        }

        await EnsureInitializedAsync();
        await _snapshotTask;

        ExchangeOrder[] snapshot;
        await _sync.WaitAsync(cancellationToken);
        try
        {
            snapshot = _orders.ToArray();
        }
        finally
        {
            _sync.Release();
        }

        if (snapshot.Length > 0 && !cancellationToken.IsCancellationRequested)
        {
            await handler.Invoke(snapshot);
        }

        return new SubscriptionRegistration(() => Unsubscribe(handler));
    }

    private async Task EnsureInitializedAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        if (!HasApiCredentials())
        {
            _snapshotTask = Task.CompletedTask;
            return;
        }

        _snapshotTask = LoadSnapshotOnceAsync();
        _socketCts = new CancellationTokenSource();
        _socketTask = RunSocketLoopAsync(_socketCts.Token);
    }

    private async Task LoadSnapshotOnceAsync()
    {
        await _snapshotLock.WaitAsync();
        try
        {
            if (!HasApiCredentials())
            {
                return;
            }

            var orders = await _bybitOrderService.GetOpenOrdersAsync();
            await UpdateSnapshotAsync(orders);
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    private async Task UpdateSnapshotAsync(IReadOnlyList<ExchangeOrder> orders)
    {
        await _sync.WaitAsync();
        try
        {
            _orders.Clear();
            _orders.AddRange(orders.Where(IsOpenOrderStatus));
        }
        finally
        {
            _sync.Release();
        }

        await NotifySubscribersAsync();
    }

    private async Task RunSocketLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAsync(cancellationToken);
                await ReceiveLoopAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // ignore and reconnect
            }
            finally
            {
                await CloseSocketAsync();
            }

            try
            {
                await Task.Delay(ReconnectDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await CloseSocketAsync();
        var settings = _bybitSettingsOptions.Value;

        if (string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.ApiSecret))
        {
            return;
        }

        _socket = new ClientWebSocket();
        await _socket.ConnectAsync(BybitPrivateWebSocketUrl, cancellationToken);
        await AuthenticateAsync(settings, cancellationToken);
        await SubscribeOrdersAsync(cancellationToken);
        _heartbeatTask = SendHeartbeatLoopAsync(cancellationToken);
    }

    private async Task AuthenticateAsync(BybitSettings settings, CancellationToken cancellationToken)
    {
        var expires = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10000;
        var signature = SignWebSocketAuth(settings.ApiSecret, expires);
        var payload = new
        {
            op = "auth",
            args = new object[] { settings.ApiKey, expires, signature }
        };

        await SendAsync(payload, cancellationToken);
    }

    private async Task SubscribeOrdersAsync(CancellationToken cancellationToken)
    {
        var payload = new
        {
            op = "subscribe",
            args = new[] { "order" }
        };

        await SendAsync(payload, cancellationToken);
    }

    private async Task SendAsync(object payload, CancellationToken cancellationToken)
    {
        if (_socket is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(payload, _serializerOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if (_socket is null)
        {
            return;
        }

        var buffer = new byte[4096];

        while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            WebSocketReceiveResult? result = null;
            var builder = new StringBuilder();

            do
            {
                result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (result is not null && !result.EndOfMessage);

            if (result?.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            TryHandlePayload(builder.ToString());
        }
    }

    private void TryHandlePayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.TryGetProperty("op", out var opElement))
            {
                var op = opElement.GetString();
                if (string.Equals(op, "pong", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            if (!root.TryGetProperty("topic", out var topicElement))
            {
                return;
            }

            var topic = topicElement.GetString();
            if (!string.Equals(topic, "order", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!root.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var entry in dataElement.EnumerateArray())
            {
                var update = ReadOrder(entry);
                if (update is null)
                {
                    continue;
                }

                _ = ApplyUpdateAsync(update);
            }
        }
        catch
        {
            // ignore malformed payloads
        }
    }

    private static ExchangeOrder? ReadOrder(JsonElement entry)
    {
        if (!TryReadString(entry, "symbol", out var symbol))
        {
            return null;
        }

        TryReadString(entry, "orderId", out var orderId);
        TryReadString(entry, "side", out var side);
        TryReadString(entry, "category", out var category);
        TryReadString(entry, "orderType", out var orderType);
        TryReadString(entry, "stopOrderType", out var stopOrderType);
        TryReadString(entry, "orderStatus", out var orderStatus);

        var qty = ReadDecimal(entry, "qty");
        if (qty == 0)
        {
            qty = ReadDecimal(entry, "leavesQty");
        }

        var price = ResolveOrderPrice(entry);

        return new ExchangeOrder(orderId, symbol, side, category, orderType, orderStatus, qty, price, stopOrderType);
    }

    private async Task ApplyUpdateAsync(ExchangeOrder update)
    {
        await _sync.WaitAsync();
        try
        {
            var index = _orders.FindIndex(order => string.Equals(order.OrderId, update.OrderId, StringComparison.OrdinalIgnoreCase));
            if (!IsOpenOrderStatus(update))
            {
                if (index >= 0)
                {
                    _orders.RemoveAt(index);
                }
            }
            else if (index >= 0)
            {
                _orders[index] = update;
            }
            else
            {
                _orders.Add(update);
            }
        }
        finally
        {
            _sync.Release();
        }

        await NotifySubscribersAsync();
    }

    private async Task SendHeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, cancellationToken);
                if (cancellationToken.IsCancellationRequested || _socket is null || _socket.State != WebSocketState.Open)
                {
                    return;
                }

                await SendAsync(new { op = "ping" }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task CloseSocketAsync()
    {
        if (_socket is null)
        {
            return;
        }

        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
        }
        catch
        {
            // ignore socket close failures
        }
        finally
        {
            _socket.Dispose();
            _socket = null;
        }
    }

    private static string SignWebSocketAuth(string secret, long expires)
    {
        var payload = $"GET/realtime{expires}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var builder = new StringBuilder(hash.Length * 2);

        foreach (var value in hash)
        {
            builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private bool HasApiCredentials()
    {
        var settings = _bybitSettingsOptions.Value;
        return !string.IsNullOrWhiteSpace(settings.ApiKey) && !string.IsNullOrWhiteSpace(settings.ApiSecret);
    }

    private static bool IsOpenOrderStatus(ExchangeOrder order)
    {
        return IsOpenOrderStatus(order.OrderStatus);
    }

    private static bool IsOpenOrderStatus(string status)
    {
        return status.Equals("New", StringComparison.OrdinalIgnoreCase)
            || status.Equals("PartiallyFilled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Untriggered", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        value = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString()?.Trim() ?? string.Empty,
            _ => property.GetRawText().Trim()
        };

        return !string.IsNullOrWhiteSpace(value);
    }

    private static decimal ReadDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0m;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var numeric))
        {
            return numeric;
        }

        var raw = property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.GetRawText();

        return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;
    }

    private static decimal? ReadNullableDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var numeric))
        {
            return numeric;
        }

        var raw = property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.GetRawText();

        return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static decimal? ResolveOrderPrice(JsonElement element)
    {
        return FirstPositive(
            ReadNullableDecimal(element, "price"),
            ReadNullableDecimal(element, "avgPrice"),
            ReadNullableDecimal(element, "triggerPrice"),
            ReadNullableDecimal(element, "takeProfit"),
            ReadNullableDecimal(element, "stopLoss"),
            ReadNullableDecimal(element, "tpLimitPrice"),
            ReadNullableDecimal(element, "slLimitPrice"));
    }

    private static decimal? FirstPositive(params decimal?[] values)
    {
        foreach (var value in values)
        {
            if (value.HasValue && value.Value > 0)
            {
                return value.Value;
            }
        }

        return null;
    }

    private void Unsubscribe(Func<IReadOnlyList<ExchangeOrder>, Task> handler)
    {
        lock (_subscriberLock)
        {
            _subscribers.Remove(handler);
        }
    }

    private async Task NotifySubscribersAsync()
    {
        Func<IReadOnlyList<ExchangeOrder>, Task>[] handlers;
        lock (_subscriberLock)
        {
            handlers = _subscribers.ToArray();
        }

        if (handlers.Length == 0)
        {
            return;
        }

        ExchangeOrder[] snapshot;
        await _sync.WaitAsync();
        try
        {
            snapshot = _orders.ToArray();
        }
        finally
        {
            _sync.Release();
        }

        foreach (var handler in handlers)
        {
            await handler.Invoke(snapshot);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _socketCts?.Cancel();
        if (_socketTask is not null)
        {
            try
            {
                await _socketTask;
            }
            catch
            {
                // ignore
            }
        }

        _socketCts?.Dispose();
        _socketCts = null;
        _socketTask = null;
        _heartbeatTask = null;
        await CloseSocketAsync();
    }
}
