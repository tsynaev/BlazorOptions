using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlazorOptions.ViewModels;
using Microsoft.Extensions.Options;

namespace BlazorOptions.Services;

public sealed class ActivePositionsService : IActivePositionsService
{
    private static readonly Uri BybitPrivateWebSocketUrl = new("wss://stream.bybit.com/v5/private");

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    private readonly BybitPositionService _bybitPositionService;
    private readonly IBybitPrivateStreamService _privateStreamService;
    private readonly IOptions<BybitSettings> _bybitSettingsOptions;
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);
    private readonly object _subscriberLock = new();
    private readonly List<Func<IReadOnlyList<ExchangePosition>, Task>> _subscribers = new();
    private readonly List<ExchangePosition> _positions = new();
    private readonly BybitPositionComparer _comparer = new();
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _socketCts;
    private Task? _socketTask;
    private Task? _heartbeatTask;
    private bool _snapshotInitialized;
    private bool _streamingInitialized;
    private Task _snapshotTask = Task.CompletedTask;
    private IDisposable? _topicSubscription;

 

    public ActivePositionsService(
        BybitPositionService bybitPositionService,
        IBybitPrivateStreamService privateStreamService,
        IOptions<BybitSettings> bybitSettingsOptions)
    {
        _bybitPositionService = bybitPositionService;
        _privateStreamService = privateStreamService;
        _bybitSettingsOptions = bybitSettingsOptions;
    }


    private async Task EnsureInitializedAsync(bool requireStreaming = false)
    {
        if (!_snapshotInitialized)
        {
            _snapshotInitialized = true;

            if (HasApiCredentials())
            {
                _snapshotTask = LoadSnapshotOnceAsync();
            }
            else
            {
                _snapshotTask = Task.CompletedTask;
            }
        }

        if (!requireStreaming || _streamingInitialized || !HasApiCredentials())
        {
            return;
        }

        _streamingInitialized = true;
        // Snapshot-only consumers should not keep a private stream alive.
        _topicSubscription = await _privateStreamService.SubscribeTopicAsync("position", HandlePositionTopicAsync);
    }

    private async Task HandlePositionTopicAsync(IReadOnlyList<JsonElement> entries)
    {
        foreach (var entry in entries)
        {
            if (!entry.TryReadString("symbol", out var symbol))
            {
                continue;
            }

            entry.TryReadString("side", out var side);
            entry.TryReadString("category", out var category);
            var size = entry.ReadDecimal("size");
            var avgPrice = entry.ReadDecimal("avgPrice");
            if (avgPrice <= 0)
            {
                avgPrice = entry.ReadDecimal("entryPrice");
            }

            if (string.IsNullOrWhiteSpace(category))
            {
                category = "linear";
            }

            var createdTimeUtc = ReadDateTimeUtc(entry, "createdTime");
            var update = new ExchangePosition(symbol, side, category, size, avgPrice, createdTimeUtc);
            await ApplyUpdateAsync(update);
        }
    }

 
    private Task EnsureSnapshotAsync()
    {
        if (!_snapshotTask.IsCompleted)
        {
            return _snapshotTask;
        }

        _snapshotTask = LoadSnapshotOnceAsync();
        return _snapshotTask;
    }

    private async Task LoadSnapshotOnceAsync()
    {
        await _snapshotLock.WaitAsync();
        try
        {
            await ReloadFromExchangeAsync();
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    private async Task ReloadFromExchangeAsync()
    {
        try
        {
            if (!HasApiCredentials())
            {
                return;
            }

            var allPositions = new List<ExchangePosition>();


            var positions = await _bybitPositionService.GetPositionsAsync();
            allPositions.AddRange(positions);

  
            await UpdateSnapshotAsync(allPositions);
        }
        catch (Exception ex)
        {
            _ = ex;
        }
    }

    private async Task UpdateSnapshotAsync(IReadOnlyList<ExchangePosition> positions)
    {
        await _sync.WaitAsync();
        try
        {
            var incoming = positions.Where(position => Math.Abs(position.Size) >= 0.0001m).ToList();
            var incomingSet = new HashSet<ExchangePosition>(incoming, _comparer);

            _positions.RemoveAll(existing => !incomingSet.Contains(existing));

            foreach (var position in incoming)
            {
                var index = _positions.FindIndex(existing => _comparer.Equals(existing, position));
                if (index >= 0)
                {
                    _positions[index] = position;
                }
                else
                {
                    _positions.Add(position);
                }
            }
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
            catch (Exception ex)
            {
                _ = ex;
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
        await SubscribePositionsAsync(cancellationToken);
        await EnsureSnapshotAsync();
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

    private async Task SubscribePositionsAsync(CancellationToken cancellationToken)
    {
        var payload = new
        {
            op = "subscribe",
            args = new[] { "position" }
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
            if (!string.Equals(topic, "position", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!root.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var entry in dataElement.EnumerateArray())
            {
                if (!entry.TryReadString("symbol", out var symbol))
                {
                    continue;
                }

                entry.TryReadString("side", out var side);
                entry.TryReadString("category", out var category);
                var size = entry.ReadDecimal("size");
                var avgPrice = entry.ReadDecimal("avgPrice");
                if (avgPrice <= 0)
                {
                    avgPrice = entry.ReadDecimal("entryPrice");
                }

                if (string.IsNullOrWhiteSpace(category))
                {
                    category = "linear";
                }

                var createdTimeUtc = ReadDateTimeUtc(entry, "createdTime");
                var update = new ExchangePosition(symbol, side, category, size, avgPrice, createdTimeUtc);
                _ = ApplyUpdateAsync(update);
            }
        }
        catch
        {
        }
    }

    private async Task ApplyUpdateAsync(ExchangePosition update)
    {
        await _sync.WaitAsync();
        try
        {
            var index = FindIndexForUpdate(update);
            if (Math.Abs(update.Size) < 0.0001m)
            {
                if (index >= 0)
                {
                    _positions.RemoveAt(index);
                }
                else if (IsUnknownSide(update.Side))
                {
                    // Some close events can omit/normalize side; remove all symbol/category matches.
                    _positions.RemoveAll(existing => IsSameSymbolCategory(existing, update));
                }
            }
            else if (index >= 0)
            {
                var existing = _positions[index];
                var merged = update.CreatedTimeUtc.HasValue
                    ? update
                    : update with { CreatedTimeUtc = existing.CreatedTimeUtc };
                _positions[index] = merged;
            }
            else
            {
                _positions.Add(update);
            }
        }
        finally
        {
            _sync.Release();
        }

        await NotifySubscribersAsync();
    }

    private int FindIndexForUpdate(ExchangePosition update)
    {
        var direct = _positions.FindIndex(existing => _comparer.Equals(existing, update));
        if (direct >= 0)
        {
            return direct;
        }

        if (!IsUnknownSide(update.Side))
        {
            return -1;
        }

        var sameSymbolCategory = _positions
            .Select((position, idx) => new { position, idx })
            .Where(item => IsSameSymbolCategory(item.position, update))
            .ToArray();
        if (sameSymbolCategory.Length == 1)
        {
            return sameSymbolCategory[0].idx;
        }

        return -1;
    }

    private static bool IsSameSymbolCategory(ExchangePosition left, ExchangePosition right)
    {
        return string.Equals(left.Symbol, right.Symbol, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.Category, right.Category, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnknownSide(string? side)
    {
        if (string.IsNullOrWhiteSpace(side))
        {
            return true;
        }

        return side.Equals("none", StringComparison.OrdinalIgnoreCase)
               || side.Equals("unknown", StringComparison.OrdinalIgnoreCase);
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

                var payload = new { op = "ping" };
                await SendAsync(payload, cancellationToken);
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

    private static DateTime? ReadDateTimeUtc(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        long timestampMs;
        switch (property.ValueKind)
        {
            case JsonValueKind.Number when property.TryGetInt64(out var asLong):
                timestampMs = asLong;
                break;
            case JsonValueKind.String:
                var raw = property.GetString();
                if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLong))
                {
                    return null;
                }

                timestampMs = parsedLong;
                break;
            default:
                return null;
        }

        if (timestampMs <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).UtcDateTime;
        }
        catch
        {
            return null;
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
            }
        }

        _socketCts?.Dispose();
        _socketCts = null;
        _socketTask = null;
        _heartbeatTask = null;
        _topicSubscription?.Dispose();
        _topicSubscription = null;
        await CloseSocketAsync();
    }

    public async ValueTask<IDisposable> SubscribeAsync(
        Func<IReadOnlyList<ExchangePosition>, Task> handler,
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

        await EnsureInitializedAsync(requireStreaming: true);
        await _snapshotTask;

        var snapshot = _positions.ToArray();
        if (snapshot.Length > 0 && !cancellationToken.IsCancellationRequested)
        {
            await handler.Invoke(snapshot);
        }

        return new SubscriptionRegistration(() => Unsubscribe(handler));
    }

    private sealed class BybitPositionComparer : IEqualityComparer<ExchangePosition>
    {
        public bool Equals(ExchangePosition? x, ExchangePosition? y)
        {
            if (x is null || y is null)
            {
                return false;
            }

            return string.Equals(x.Symbol, y.Symbol, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(x.Category, y.Category, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(x.Side, y.Side, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(ExchangePosition obj)
        {
            return HashCode.Combine(
                obj.Symbol?.ToUpperInvariant(),
                obj.Category?.ToUpperInvariant(),
                obj.Side?.ToUpperInvariant());
        }
    }

    public async Task<IEnumerable<ExchangePosition>> GetPositionsAsync()
    {
        await EnsureInitializedAsync();
        await _snapshotTask;

        return _positions;
    }

    private void Unsubscribe(Func<IReadOnlyList<ExchangePosition>, Task> handler)
    {
        lock (_subscriberLock)
        {
            _subscribers.Remove(handler);
        }
    }

    private async Task NotifySubscribersAsync()
    {
        Func<IReadOnlyList<ExchangePosition>, Task>[] handlers;
        lock (_subscriberLock)
        {
            handlers = _subscribers.ToArray();
        }

        if (handlers.Length == 0)
        {
            return;
        }

        var snapshot = _positions.ToArray();
        foreach (var handler in handlers)
        {
            await handler.Invoke(snapshot);
        }
    }
}
