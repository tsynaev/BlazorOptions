using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlazorOptions.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlazorOptions.Services;

public sealed class ActiveWalletService : IWalletService
{
    private const string StorageKey = "blazor-options-bybit-wallet";
    private static readonly Uri BybitPrivateWebSocketUrl = new("wss://stream.bybit.com/v5/private");
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    private readonly ILocalStorageService _localStorageService;
    private readonly BybitWalletService _bybitWalletService;
    private readonly IBybitPrivateStreamService _privateStreamService;
    private readonly IOptions<BybitSettings> _bybitSettingsOptions;
    private readonly ILogger<ActiveWalletService> _logger;
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);
    private readonly object _subscriberLock = new();
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly List<Func<ExchangeWalletSnapshot, Task>> _subscribers = new();
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _socketCts;
    private Task? _socketTask;
    private bool _isInitialized;
    private Task _snapshotTask = Task.CompletedTask;
    private ExchangeWalletSnapshot? _snapshot;
    private IDisposable? _topicSubscription;
    private CancellationTokenSource? _fallbackRefreshCts;
    private Task? _fallbackRefreshTask;
    private DateTime _lastWalletEventUtc = DateTime.MinValue;

    public ActiveWalletService(
        ILocalStorageService localStorageService,
        BybitWalletService bybitWalletService,
        IBybitPrivateStreamService privateStreamService,
        IOptions<BybitSettings> bybitSettingsOptions,
        ILogger<ActiveWalletService> logger)
    {
        _localStorageService = localStorageService;
        _bybitWalletService = bybitWalletService;
        _privateStreamService = privateStreamService;
        _bybitSettingsOptions = bybitSettingsOptions;
        _logger = logger;
    }

    public async Task<ExchangeWalletSnapshot?> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync();
        await _snapshotTask;

        await _sync.WaitAsync(cancellationToken);
        try
        {
            return _snapshot;
        }
        finally
        {
            _sync.Release();
        }
    }

    public async ValueTask<IDisposable> SubscribeAsync(
        Func<ExchangeWalletSnapshot, Task> handler,
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

        ExchangeWalletSnapshot? snapshot;
        await _sync.WaitAsync(cancellationToken);
        try
        {
            snapshot = _snapshot;
        }
        finally
        {
            _sync.Release();
        }

        if (snapshot is not null && !cancellationToken.IsCancellationRequested)
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
        await LoadFromStorageAsync();

        if (!HasApiCredentials())
        {
            _snapshotTask = Task.CompletedTask;
            return;
        }

        _snapshotTask = LoadSnapshotOnceAsync();
        _topicSubscription = await _privateStreamService.SubscribeTopicAsync("wallet", HandleWalletTopicAsync);
        _fallbackRefreshCts = new CancellationTokenSource();
        _fallbackRefreshTask = RunFallbackRefreshLoopAsync(_fallbackRefreshCts.Token);
    }

    private async Task HandleWalletTopicAsync(IReadOnlyList<JsonElement> entries)
    {
        _lastWalletEventUtc = DateTime.UtcNow;
        foreach (var entry in entries)
        {
            var mapped = MapWalletEntry(entry);
            if (mapped is null)
            {
                continue;
            }

            await UpdateSnapshotAsync(mapped, notify: true);
        }
    }

    private async Task LoadFromStorageAsync()
    {
        try
        {
            var payload = await _localStorageService.GetItemAsync(StorageKey);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return;
            }

            var snapshot = JsonSerializer.Deserialize<ExchangeWalletSnapshot>(payload, _serializerOptions);
            if (snapshot is null)
            {
                return;
            }

            await UpdateSnapshotAsync(snapshot, notify: false);
        }
        catch
        {
            // Ignore storage corruption and continue with live fetch.
        }
    }

    private async Task LoadSnapshotOnceAsync()
    {
        try
        {
            var snapshot = await _bybitWalletService.GetWalletSnapshotAsync();
            if (snapshot is null)
            {
                return;
            }

            await UpdateSnapshotAsync(snapshot, notify: true);
        }
        catch
        {
            // Keep stream alive even if REST snapshot failed.
            _logger.LogWarning("Initial wallet snapshot fetch failed.");
        }
    }

    private async Task RunFallbackRefreshLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                // If wallet topic is quiet for a while, refresh from REST so UI remains current.
                if (_lastWalletEventUtc != DateTime.MinValue
                    && DateTime.UtcNow - _lastWalletEventUtc < TimeSpan.FromMinutes(2))
                {
                    continue;
                }

                var snapshot = await _bybitWalletService.GetWalletSnapshotAsync(cancellationToken);
                if (snapshot is null)
                {
                    continue;
                }

                _logger.LogInformation("Wallet fallback refresh applied.");
                await UpdateSnapshotAsync(snapshot, notify: true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                _logger.LogDebug("Wallet fallback refresh failed.");
            }
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
        await SubscribeWalletAsync(cancellationToken);
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

    private async Task SubscribeWalletAsync(CancellationToken cancellationToken)
    {
        var payload = new
        {
            op = "subscribe",
            args = new[] { "wallet" }
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

            _ = TryHandlePayloadAsync(builder.ToString());
        }
    }

    private async Task TryHandlePayloadAsync(string payload)
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

            if (!root.TryGetProperty("topic", out var topicElement)
                || !string.Equals(topicElement.GetString(), "wallet", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!root.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var entry in dataElement.EnumerateArray())
            {
                var mapped = MapWalletEntry(entry);
                if (mapped is null)
                {
                    continue;
                }

                await UpdateSnapshotAsync(mapped, notify: true);
            }
        }
        catch
        {
            // ignore malformed payloads
        }
    }

    private static ExchangeWalletSnapshot? MapWalletEntry(JsonElement entry)
    {
        var accountType = ReadString(entry, "accountType") ?? "UNIFIED";
        var coins = new List<ExchangeWalletCoin>();
        if (entry.TryGetProperty("coin", out var coinsElement) && coinsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var coinEntry in coinsElement.EnumerateArray())
            {
                var coin = ReadString(coinEntry, "coin");
                if (string.IsNullOrWhiteSpace(coin))
                {
                    continue;
                }

                coins.Add(new ExchangeWalletCoin(
                    coin.ToUpperInvariant(),
                    ReadDecimal(coinEntry, "equity"),
                    ReadDecimal(coinEntry, "walletBalance"),
                    ReadDecimal(coinEntry, "availableToWithdraw"),
                    ReadDecimal(coinEntry, "usdValue")));
            }
        }

        return new ExchangeWalletSnapshot(
            DateTime.UtcNow,
            accountType.ToUpperInvariant(),
            ReadDecimal(entry, "totalEquity"),
            ReadDecimal(entry, "totalWalletBalance"),
            ReadDecimal(entry, "totalMarginBalance"),
            ReadDecimal(entry, "totalInitialMargin"),
            ReadDecimal(entry, "totalMaintenanceMargin"),
            ReadDecimal(entry, "totalAvailableBalance"),
            ReadDecimal(entry, "totalPerpUPL"),
            coins);
    }

    private async Task UpdateSnapshotAsync(ExchangeWalletSnapshot snapshot, bool notify)
    {
        ExchangeWalletSnapshot mergedSnapshot;
        await _sync.WaitAsync();
        try
        {
            // Wallet topic payloads may omit some totals/coins, so keep the latest complete view by merging.
            mergedSnapshot = MergeWithCurrentSnapshot(snapshot, _snapshot);
            _snapshot = mergedSnapshot;
            var payload = JsonSerializer.Serialize(mergedSnapshot, _serializerOptions);
            await _localStorageService.SetItemAsync(StorageKey, payload);
        }
        finally
        {
            _sync.Release();
        }

        if (notify)
        {
            await NotifySubscribersAsync(mergedSnapshot);
        }
    }

    private static ExchangeWalletSnapshot MergeWithCurrentSnapshot(
        ExchangeWalletSnapshot incoming,
        ExchangeWalletSnapshot? current)
    {
        if (current is null)
        {
            return incoming;
        }

        return new ExchangeWalletSnapshot(
            incoming.UpdatedUtc,
            string.IsNullOrWhiteSpace(incoming.AccountType) ? current.AccountType : incoming.AccountType,
            incoming.TotalEquity ?? current.TotalEquity,
            incoming.TotalWalletBalance ?? current.TotalWalletBalance,
            incoming.TotalMarginBalance ?? current.TotalMarginBalance,
            incoming.TotalInitialMargin ?? current.TotalInitialMargin,
            incoming.TotalMaintenanceMargin ?? current.TotalMaintenanceMargin,
            incoming.TotalAvailableBalance ?? current.TotalAvailableBalance,
            incoming.TotalPerpUpl ?? current.TotalPerpUpl,
            MergeCoins(incoming.Coins, current.Coins));
    }

    private static IReadOnlyList<ExchangeWalletCoin> MergeCoins(
        IReadOnlyList<ExchangeWalletCoin> incoming,
        IReadOnlyList<ExchangeWalletCoin> current)
    {
        if (incoming.Count == 0)
        {
            return current;
        }

        var merged = current.ToDictionary(coin => coin.Coin, StringComparer.OrdinalIgnoreCase);
        foreach (var coin in incoming)
        {
            if (merged.TryGetValue(coin.Coin, out var existing))
            {
                merged[coin.Coin] = new ExchangeWalletCoin(
                    coin.Coin,
                    coin.Equity ?? existing.Equity,
                    coin.WalletBalance ?? existing.WalletBalance,
                    coin.AvailableToWithdraw ?? existing.AvailableToWithdraw,
                    coin.UsdValue ?? existing.UsdValue);
                continue;
            }

            merged[coin.Coin] = coin;
        }

        return merged.Values
            .OrderBy(coin => coin.Coin, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
            // ignore close errors
        }
        finally
        {
            _socket.Dispose();
            _socket = null;
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim()
            : property.GetRawText().Trim();
    }

    private static decimal? ReadDecimal(JsonElement element, string propertyName)
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

    private void Unsubscribe(Func<ExchangeWalletSnapshot, Task> handler)
    {
        lock (_subscriberLock)
        {
            _subscribers.Remove(handler);
        }
    }

    private async Task NotifySubscribersAsync(ExchangeWalletSnapshot snapshot)
    {
        Func<ExchangeWalletSnapshot, Task>[] handlers;
        lock (_subscriberLock)
        {
            handlers = _subscribers.ToArray();
        }

        foreach (var handler in handlers)
        {
            await handler.Invoke(snapshot);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _fallbackRefreshCts?.Cancel();
        if (_fallbackRefreshTask is not null)
        {
            try
            {
                await _fallbackRefreshTask;
            }
            catch
            {
                // ignore
            }
        }

        _fallbackRefreshCts?.Dispose();
        _fallbackRefreshCts = null;
        _fallbackRefreshTask = null;

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
        _topicSubscription?.Dispose();
        _topicSubscription = null;
        await CloseSocketAsync();
    }
}
