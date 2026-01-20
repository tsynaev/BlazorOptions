using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlazorOptions.Sync;
using BlazorOptions.ViewModels;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace BlazorOptions.Services;

public class PositionSyncService : IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly AuthSessionService _sessionService;
    private readonly AuthApiService _authApiService;
    private readonly DeviceIdentityService _deviceIdentityService;
    private readonly PositionSyncOutboxService _outboxService;
    private readonly ILogger<PositionSyncService> _logger;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private HubConnection? _hubConnection;
    private string? _connectedToken;
    private Func<PositionSnapshotPayload, DateTime, Task>? _applySnapshotAsync;
    private Func<PositionItemSnapshotPayload, DateTime, Task>? _applyItemAsync;
    private bool _initialized;
    private DateTime _nextConnectAttemptUtc = DateTime.MinValue;
    private int _connectBackoffSeconds = 1;
    private bool _authFailed;

    public PositionSyncService(
        HttpClient httpClient,
        AuthSessionService sessionService,
        AuthApiService authApiService,
        DeviceIdentityService deviceIdentityService,
        PositionSyncOutboxService outboxService,
        ILogger<PositionSyncService> logger)
    {
        _httpClient = httpClient;
        _sessionService = sessionService;
        _authApiService = authApiService;
        _deviceIdentityService = deviceIdentityService;
        _outboxService = outboxService;
        _logger = logger;
    }

    public async Task InitializeAsync(
        Func<PositionSnapshotPayload, DateTime, Task> applySnapshotAsync,
        Func<PositionItemSnapshotPayload, DateTime, Task> applyItemAsync)
    {
        if (_initialized)
        {
            return;
        }

        _applySnapshotAsync = applySnapshotAsync;
        _applyItemAsync = applyItemAsync;
        _initialized = true;
        _sessionService.OnChange += HandleSessionChanged;
        try
        {
            await EnsureConnectedAsync();
        }
        catch
        {
            _logger.LogWarning("Position sync initialization failed.");
        }
    }

    public async Task QueueLocalSnapshotAsync(
        IReadOnlyList<PositionModel> positions,
        IReadOnlyList<Guid> deletedPositionIds)
    {
        if (!_sessionService.IsAuthenticated)
        {
            return;
        }

        var existing = await _outboxService.LoadAsync();
        var pendingUpserts = new HashSet<Guid>();
        var pendingDeletes = new HashSet<Guid>();

        foreach (var envelope in existing)
        {
            if (!string.Equals(envelope.Kind, EventKinds.PositionSnapshot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            PositionItemSnapshotPayload? payload;
            try
            {
                payload = envelope.Payload.Deserialize<PositionItemSnapshotPayload>(SyncJson.SerializerOptions);
            }
            catch
            {
                continue;
            }

            if (payload is null)
            {
                continue;
            }

            if (payload.IsDeleted)
            {
                pendingDeletes.Add(payload.PositionId);
            }
            else
            {
                pendingUpserts.Add(payload.PositionId);
            }
        }

        var deviceId = await _deviceIdentityService.GetDeviceIdAsync();
        var now = DateTime.UtcNow;
        var toQueue = new List<EventEnvelope>();

        foreach (var position in positions)
        {
            if (pendingUpserts.Contains(position.Id))
            {
                continue;
            }

            var payload = new PositionItemSnapshotPayload
            {
                PositionId = position.Id,
                Position = position,
                IsDeleted = false
            };

            toQueue.Add(BuildEnvelope(payload, deviceId, now));
        }

        foreach (var positionId in deletedPositionIds)
        {
            if (pendingDeletes.Contains(positionId))
            {
                continue;
            }

            var payload = new PositionItemSnapshotPayload
            {
                PositionId = positionId,
                Position = null,
                IsDeleted = true
            };

            toQueue.Add(BuildEnvelope(payload, deviceId, now));
        }

        await _outboxService.AddRangeAsync(toQueue);
    }

    public async Task NotifyLocalChangeAsync(PositionModel position, bool isDeleted = false)
    {
        var occurredUtc = DateTime.UtcNow;

        if (!_sessionService.IsAuthenticated)
        {
            return;
        }

        var deviceId = await _deviceIdentityService.GetDeviceIdAsync();
        var payload = new PositionItemSnapshotPayload
        {
            PositionId = position.Id,
            Position = isDeleted ? null : position,
            IsDeleted = isDeleted
        };
        var envelope = BuildEnvelope(payload, deviceId, occurredUtc);

        await _outboxService.AddAsync(envelope);
        await TrySendPendingAsync();
    }

    private async void HandleSessionChanged()
    {
        try
        {
            if (!_sessionService.IsAuthenticated)
            {
                _authFailed = false;
            }

            await EnsureConnectedAsync();
        }
        catch
        {
            _logger.LogWarning("Position sync reconnect failed after session change.");
        }
    }

    private async Task EnsureConnectedAsync()
    {
        if (_authFailed)
        {
            return;
        }

        if (!_sessionService.IsAuthenticated || string.IsNullOrWhiteSpace(_sessionService.Token))
        {
            await DisconnectAsync();
            return;
        }

        var now = DateTime.UtcNow;
        if (now < _nextConnectAttemptUtc)
        {
            return;
        }

        await _syncLock.WaitAsync();
        try
        {
            var sessionValid = await _authApiService.ValidateSessionAsync();
            if (!sessionValid)
            {
                _authFailed = true;
                await DisconnectAsync();
                return;
            }

            if (_hubConnection is not null && string.Equals(_connectedToken, _sessionService.Token, StringComparison.Ordinal))
            {
                if (_hubConnection.State == HubConnectionState.Disconnected)
                {
                    await _hubConnection.StartAsync();
                }
            }
            else
            {
                await DisposeHubAsync();
                var deviceId = await _deviceIdentityService.GetDeviceIdAsync();
                _hubConnection = BuildHubConnection(_sessionService.Token, deviceId);
                _connectedToken = _sessionService.Token;
                await _hubConnection.StartAsync();
            }

            _connectBackoffSeconds = 1;
            _nextConnectAttemptUtc = DateTime.MinValue;
            await SendPendingAsync();
            await PullSnapshotAsync();
        }
        catch (Exception ex)
        {
            _nextConnectAttemptUtc = now.AddSeconds(_connectBackoffSeconds);
            _connectBackoffSeconds = Math.Min(_connectBackoffSeconds * 2, 30);
            _logger.LogWarning(ex, "Position sync connection failed. Next attempt in {DelaySeconds}s.", _connectBackoffSeconds);
            throw;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private static EventEnvelope BuildEnvelope(PositionItemSnapshotPayload payload, string deviceId, DateTime occurredUtc)
    {
        var payloadElement = JsonSerializer.SerializeToElement(payload, SyncJson.SerializerOptions);
        return new EventEnvelope
        {
            EventId = Guid.NewGuid(),
            DeviceId = deviceId,
            OccurredUtc = occurredUtc,
            Kind = EventKinds.PositionSnapshot,
            Payload = payloadElement
        };
    }

    private HubConnection BuildHubConnection(string token, string deviceId)
    {
        var hubUri = new Uri(_httpClient.BaseAddress!, "syncHub");
        var hubUrl = new UriBuilder(hubUri)
        {
            Query = $"token={Uri.EscapeDataString(token)}&deviceId={Uri.EscapeDataString(deviceId)}"
        }.Uri;

        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        connection.On<List<EventEnvelope>>("ReceiveEvents", async events =>
        {
            await HandleIncomingEventsAsync(events);
        });

        connection.Reconnected += _ =>
        {
            _logger.LogInformation("Position sync reconnected.");
            return SendPendingAsync();
        };
        connection.Reconnecting += async _ =>
        {
            var sessionValid = await _authApiService.ValidateSessionAsync();
            if (!sessionValid)
            {
                _authFailed = true;
                _logger.LogWarning("Position sync reconnect blocked due to invalid session.");
                if (_hubConnection is not null)
                {
                    await _hubConnection.StopAsync();
                }
                return;
            }

            _logger.LogWarning("Position sync reconnecting.");
        };
        connection.Closed += _ =>
        {
            _logger.LogWarning("Position sync connection closed.");
            return Task.CompletedTask;
        };
        return connection;
    }

    private async Task HandleIncomingEventsAsync(IReadOnlyList<EventEnvelope> events)
    {
        if (_applyItemAsync is null || events.Count == 0)
        {
            return;
        }

        foreach (var envelope in events)
        {
            if (!string.Equals(envelope.Kind, EventKinds.PositionSnapshot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            PositionItemSnapshotPayload? payload;
            try
            {
                payload = envelope.Payload.Deserialize<PositionItemSnapshotPayload>(SyncJson.SerializerOptions);
            }
            catch
            {
                continue;
            }

            if (payload is null)
            {
                continue;
            }

            await _outboxService.ClearAsync();
            await _applyItemAsync(payload, envelope.OccurredUtc);
        }
    }

    private async Task TrySendPendingAsync()
    {
        if (_hubConnection is null || _hubConnection.State != HubConnectionState.Connected)
        {
            return;
        }

        await SendPendingAsync();
    }

    private async Task SendPendingAsync()
    {
        if (_hubConnection is null || _hubConnection.State != HubConnectionState.Connected)
        {
            return;
        }

        var pending = await _outboxService.LoadAsync();
        if (pending.Count == 0)
        {
            return;
        }

        IReadOnlyList<Guid>? accepted;
        try
        {
            accepted = await _hubConnection.InvokeAsync<IReadOnlyList<Guid>>("SendEvents", pending);
        }
        catch
        {
            return;
        }

        if (accepted is null || accepted.Count == 0)
        {
            return;
        }

        await _outboxService.RemoveAsync(accepted);
    }

    private async Task PullSnapshotAsync()
    {
        if (_applySnapshotAsync is null)
        {
            return;
        }

        var snapshot = await FetchSnapshotAsync();
        if (snapshot is null)
        {
            return;
        }

        await _outboxService.ClearAsync();
        await _applySnapshotAsync(snapshot.Payload, snapshot.OccurredUtc);
    }

    private async Task<PositionSnapshotResponse?> FetchSnapshotAsync()
    {
        if (!_sessionService.IsAuthenticated || string.IsNullOrWhiteSpace(_sessionService.Token))
        {
            return null;
        }

        var request = new HttpRequestMessage(HttpMethod.Get, "api/positions/snapshot");
        request.Headers.Add("X-User-Token", _sessionService.Token);
        var response = await _httpClient.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<PositionSnapshotResponse>(SyncJson.SerializerOptions);
    }

    private async Task DisconnectAsync()
    {
        if (_hubConnection is null)
        {
            return;
        }

        try
        {
            await _hubConnection.StopAsync();
        }
        catch
        {
            _logger.LogWarning("Position sync stop failed.");
        }

        await DisposeHubAsync();
    }

    private async Task DisposeHubAsync()
    {
        if (_hubConnection is null)
        {
            return;
        }

        try
        {
            await _hubConnection.DisposeAsync();
        }
        catch
        {
            _logger.LogWarning("Position sync dispose failed.");
        }

        _hubConnection = null;
        _connectedToken = null;
    }

    public async ValueTask DisposeAsync()
    {
        _sessionService.OnChange -= HandleSessionChanged;
        await DisconnectAsync();
        _syncLock.Dispose();
    }
}

