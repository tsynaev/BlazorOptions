using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlazorOptions.ViewModels;
using Microsoft.Extensions.Options;

namespace BlazorOptions.Services;

public interface IBybitPrivateStreamService : IAsyncDisposable
{
    ValueTask<IDisposable> SubscribeTopicAsync(
        string topic,
        Func<IReadOnlyList<JsonElement>, Task> handler,
        CancellationToken cancellationToken = default);
}

public sealed class BybitPrivateStreamService : IBybitPrivateStreamService
{
    private static readonly Uri BybitPrivateWebSocketUrl = new("wss://stream.bybit.com/v5/private?max_active_time=10m");
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    private readonly IOptions<BybitSettings> _bybitSettingsOptions;
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);
    private readonly object _handlersLock = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Dictionary<string, List<Func<IReadOnlyList<JsonElement>, Task>>> _handlersByTopic = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _topics = new(StringComparer.OrdinalIgnoreCase);

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _socketCts;
    private Task? _socketTask;
    private bool _isInitialized;

    public BybitPrivateStreamService(IOptions<BybitSettings> bybitSettingsOptions)
    {
        _bybitSettingsOptions = bybitSettingsOptions;
    }

    public async ValueTask<IDisposable> SubscribeTopicAsync(
        string topic,
        Func<IReadOnlyList<JsonElement>, Task> handler,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(topic) || handler is null)
        {
            return new SubscriptionRegistration(() => { });
        }

        var normalized = topic.Trim().ToLowerInvariant();
        lock (_handlersLock)
        {
            if (!_handlersByTopic.TryGetValue(normalized, out var handlers))
            {
                handlers = new List<Func<IReadOnlyList<JsonElement>, Task>>();
                _handlersByTopic[normalized] = handlers;
            }

            handlers.Add(handler);
            _topics.Add(normalized);
        }

        await EnsureInitializedAsync();
        await SubscribeTopicIfConnectedAsync(normalized, cancellationToken);

        return new SubscriptionRegistration(() => Unsubscribe(normalized, handler));
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
            return;
        }

        _socketCts = new CancellationTokenSource();
        _socketTask = RunSocketLoopAsync(_socketCts.Token);
        await Task.CompletedTask;
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
        await SubscribeAllTopicsAsync(cancellationToken);
        _ = SendHeartbeatLoopAsync(cancellationToken);
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

    private async Task SubscribeAllTopicsAsync(CancellationToken cancellationToken)
    {
        string[] topics;
        lock (_handlersLock)
        {
            topics = _topics.ToArray();
        }

        if (topics.Length == 0)
        {
            return;
        }

        await SendAsync(new { op = "subscribe", args = topics }, cancellationToken);
    }

    private async Task SubscribeTopicIfConnectedAsync(string topic, CancellationToken cancellationToken)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            return;
        }

        await SendAsync(new { op = "subscribe", args = new[] { topic } }, cancellationToken);
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

            await TryDispatchAsync(builder.ToString());
        }
    }

    private async Task TryDispatchAsync(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.TryGetProperty("op", out var opElement)
                && string.Equals(opElement.GetString(), "pong", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!root.TryGetProperty("topic", out var topicElement))
            {
                return;
            }

            var topic = topicElement.GetString();
            if (string.IsNullOrWhiteSpace(topic))
            {
                return;
            }

            if (!root.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var data = dataElement
                .EnumerateArray()
                .Select(item => item.Clone())
                .ToArray();

            Func<IReadOnlyList<JsonElement>, Task>[] handlers;
            lock (_handlersLock)
            {
                if (!_handlersByTopic.TryGetValue(topic.Trim().ToLowerInvariant(), out var existing))
                {
                    return;
                }

                handlers = existing.ToArray();
            }

            foreach (var handler in handlers)
            {
                await handler.Invoke(data);
            }
        }
        catch
        {
            // ignore malformed payload
        }
    }

    private async Task SendHeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, cancellationToken);
                if (_socket is null || _socket.State != WebSocketState.Open)
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

    private async Task SendAsync(object payload, CancellationToken cancellationToken)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            return;
        }

        var json = JsonSerializer.Serialize(payload, _serializerOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (_socket is null || _socket.State != WebSocketState.Open)
            {
                return;
            }

            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
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
            // ignore close failures
        }
        finally
        {
            _socket.Dispose();
            _socket = null;
        }
    }

    private void Unsubscribe(string topic, Func<IReadOnlyList<JsonElement>, Task> handler)
    {
        lock (_handlersLock)
        {
            if (!_handlersByTopic.TryGetValue(topic, out var handlers))
            {
                return;
            }

            handlers.Remove(handler);
            if (handlers.Count == 0)
            {
                _handlersByTopic.Remove(topic);
                _topics.Remove(topic);
            }
        }
    }

    private bool HasApiCredentials()
    {
        var settings = _bybitSettingsOptions.Value;
        return !string.IsNullOrWhiteSpace(settings.ApiKey) && !string.IsNullOrWhiteSpace(settings.ApiSecret);
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
        await CloseSocketAsync();
        _sendLock.Dispose();
    }
}
