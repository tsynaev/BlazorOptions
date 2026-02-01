using System.Text.Json;
using BlazorOptions.Sync;

namespace BlazorOptions.Services;

public class PositionSyncOutboxService
{
    private const string OutboxKey = "blazor-options-position-outbox";
    private const string LastUpdatedKey = "blazor-options-position-last-updated";
    private readonly ILocalStorageService _localStorageService;

    public PositionSyncOutboxService(ILocalStorageService localStorageService)
    {
        _localStorageService = localStorageService;
    }

    public async Task<IReadOnlyList<EventEnvelope>> LoadAsync()
    {
        var stored = await _localStorageService.GetItemAsync(OutboxKey);
        if (string.IsNullOrWhiteSpace(stored))
        {
            return Array.Empty<EventEnvelope>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<EventEnvelope>>(stored, SyncJson.SerializerOptions) ?? new List<EventEnvelope>();
        }
        catch
        {
            return Array.Empty<EventEnvelope>();
        }
    }

    public async Task AddAsync(EventEnvelope envelope)
    {
        var items = (await LoadAsync()).ToList();
        items.Add(envelope);
        await SaveAsync(items);
    }

    public async Task AddRangeAsync(IEnumerable<EventEnvelope> envelopes)
    {
        var pending = envelopes.ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var items = (await LoadAsync()).ToList();
        items.AddRange(pending);
        await SaveAsync(items);
    }

    public async Task RemoveAsync(IEnumerable<Guid> eventIds)
    {
        var idSet = new HashSet<Guid>(eventIds);
        if (idSet.Count == 0)
        {
            return;
        }

        var items = (await LoadAsync()).Where(item => !idSet.Contains(item.EventId)).ToList();
        await SaveAsync(items);
    }

    public Task ClearAsync()
    {
        return _localStorageService.SetItemAsync(OutboxKey, string.Empty).AsTask();
    }

    public async Task<DateTime?> GetLastLocalUpdateUtcAsync()
    {
        var stored = await _localStorageService.GetItemAsync(LastUpdatedKey);
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        return DateTime.TryParse(stored, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    public Task SetLastLocalUpdateUtcAsync(DateTime utc)
    {
        return _localStorageService.SetItemAsync(LastUpdatedKey, utc.ToString("O")).AsTask();
    }

    private Task SaveAsync(IReadOnlyList<EventEnvelope> events)
    {
        var payload = JsonSerializer.Serialize(events, SyncJson.SerializerOptions);
        return _localStorageService.SetItemAsync(OutboxKey, payload).AsTask();
    }
}

