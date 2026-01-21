using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlazorOptions.ViewModels;

namespace BlazorOptions.Services;

public sealed class ActivePositionsService : IAsyncDisposable
{
    private const string StorageKey = "blazor-options-bybit-active-positions";
    private static readonly Uri BybitPrivateWebSocketUrl = new("wss://stream.bybit.com/v5/private");

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    private readonly LocalStorageService _localStorageService;
    private readonly BybitPositionService _bybitPositionService;
    private readonly ExchangeSettingsService _exchangeSettingsService;
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);
    private readonly List<BybitPosition> _positions = new();
    private readonly BybitPositionComparer _comparer = new();
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _socketCts;
    private Task? _socketTask;
    private Task? _heartbeatTask;
    private bool _isInitialized;
    private Task _snapshotTask = Task.CompletedTask;

 

    public ActivePositionsService(
        LocalStorageService localStorageService,
        BybitPositionService bybitPositionService,
        ExchangeSettingsService exchangeSettingsService)
    {
        _localStorageService = localStorageService;
        _bybitPositionService = bybitPositionService;
        _exchangeSettingsService = exchangeSettingsService;
    }


    public string? LastError { get; private set; }

    public event Func<IReadOnlyList<BybitPosition>, Task>? PositionsUpdated;

    public event Action<BybitPosition>? PositionUpdated;

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        await LoadFromStorageAsync();

        if (!await HasApiCredentialsAsync())
        {
            _snapshotTask = Task.CompletedTask;
            return;
        }

        _socketCts = new CancellationTokenSource();
        _socketTask = RunSocketLoopAsync(_socketCts.Token);
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

    public async Task ReloadFromExchangeAsync()
    {
        try
        {
            if (!await HasApiCredentialsAsync())
            {
                LastError = "Bybit API credentials are missing.";
                return;
            }

            var allPositions = new List<BybitPosition>();


            var positions = await _bybitPositionService.GetPositionsAsync();
            allPositions.AddRange(positions);

  
            await UpdateSnapshotAsync(allPositions);
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private async Task LoadFromStorageAsync()
    {
        try
        {
            var stored = await _localStorageService.GetItemAsync(StorageKey);
            if (string.IsNullOrWhiteSpace(stored))
            {
                return;
            }

            var positions = JsonSerializer.Deserialize<List<BybitPosition>>(stored, _serializerOptions);
            if (positions is null)
            {
                return;
            }

            await UpdateSnapshotAsync(positions);
        }
        catch
        {
        }
    }

    private async Task PersistAsync()
    {
        var payload = JsonSerializer.Serialize(_positions, _serializerOptions);
        await _localStorageService.SetItemAsync(StorageKey, payload);
    }

    private async Task UpdateSnapshotAsync(IReadOnlyList<BybitPosition> positions)
    {
        await _sync.WaitAsync();
        try
        {
            var incoming = positions.Where(position => Math.Abs(position.Size) >= 0.0001).ToList();
            var incomingSet = new HashSet<BybitPosition>(incoming, _comparer);

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


            await PersistAsync();

        }
        finally
        {
            _sync.Release();
        }

        var handler = PositionsUpdated;
        if (handler != null)
        {
            await handler.Invoke(_positions);
        }
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
                LastError = ex.Message;
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
        var settings = await _exchangeSettingsService.LoadBybitSettingsAsync();

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
                if (!TryReadString(entry, "symbol", out var symbol))
                {
                    continue;
                }

                TryReadString(entry, "side", out var side);
                TryReadString(entry, "category", out var category);
                var size = ReadDouble(entry, "size");
                var avgPrice = ReadDouble(entry, "avgPrice");
                if (avgPrice <= 0)
                {
                    avgPrice = ReadDouble(entry, "entryPrice");
                }

                if (string.IsNullOrWhiteSpace(category))
                {
                    category = "linear";
                }

                var update = new BybitPosition(symbol, side, category, size, avgPrice);
                _ = ApplyUpdateAsync(update);
            }
        }
        catch
        {
        }
    }

    private async Task ApplyUpdateAsync(BybitPosition update)
    {
        await _sync.WaitAsync();
        try
        {
            var index = _positions.FindIndex(existing => _comparer.Equals(existing, update));
            if (Math.Abs(update.Size) < 0.0001)
            {
                if (index >= 0)
                {
                    _positions.RemoveAt(index);
                }
            }
            else if (index >= 0)
            {
                _positions[index] = update;
            }
            else
            {
                _positions.Add(update);
            }

            await PersistAsync();
        }
        finally
        {
            _sync.Release();
        }

        PositionUpdated?.Invoke(update);
        PositionsUpdated?.Invoke(_positions);
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

    private async Task<bool> HasApiCredentialsAsync()
    {
        var settings = await _exchangeSettingsService.LoadBybitSettingsAsync();
        return !string.IsNullOrWhiteSpace(settings.ApiKey) && !string.IsNullOrWhiteSpace(settings.ApiSecret);
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

    private static double ReadDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        switch (property.ValueKind)
        {
            case JsonValueKind.Number when property.TryGetDouble(out var value):
                return value;
            case JsonValueKind.String:
                var raw = property.GetString();
                if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }

                break;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return 0;
        }

        var trimmed = property.GetRawText();
        return double.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out var fallback)
            ? fallback
            : 0;
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
        await CloseSocketAsync();
    }

    private sealed class BybitPositionComparer : IEqualityComparer<BybitPosition>
    {
        public bool Equals(BybitPosition? x, BybitPosition? y)
        {
            if (x is null || y is null)
            {
                return false;
            }

            return string.Equals(x.Symbol, y.Symbol, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(x.Category, y.Category, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(x.Side, y.Side, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(BybitPosition obj)
        {
            return HashCode.Combine(
                obj.Symbol?.ToUpperInvariant(),
                obj.Category?.ToUpperInvariant(),
                obj.Side?.ToUpperInvariant());
        }
    }

    public async Task<IEnumerable<BybitPosition>> GetPositionsAsync()
    {
        await _snapshotTask;

        return _positions;
    }
}
