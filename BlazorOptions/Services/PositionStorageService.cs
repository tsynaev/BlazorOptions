using BlazorOptions.Pages;
using BlazorOptions.ViewModels;
using System.Text.Json;

namespace BlazorOptions.Services;

public class PositionStorageService
{
    private const string StorageKey = "blazor-options-positions";
    private const string DeletedKey = "blazor-options-positions-deleted";
    private readonly LocalStorageService _localStorageService;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public PositionStorageService(LocalStorageService localStorageService)
    {
        _localStorageService = localStorageService;
    }

    public async Task<List<PositionModel>> LoadPositionsAsync()
    {
        var stored = await _localStorageService.GetItemAsync(StorageKey);

        if (string.IsNullOrWhiteSpace(stored))
        {
            return new List<PositionModel>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<PositionModel>>(stored, _serializerOptions) ?? new List<PositionModel>();
        }
        catch
        {
            return new List<PositionModel>();
        }
    }

    public Task SavePositionsAsync(IEnumerable<PositionModel> positions)
    {
        var payload = JsonSerializer.Serialize(positions, _serializerOptions);
        return _localStorageService.SetItemAsync(StorageKey, payload).AsTask();
    }

    public async Task SavePositionAsync(PositionModel position)
    {
        var positions = await LoadPositionsAsync();

        var index = positions.FindIndex(x => x.Id == position.Id);

        if (index >= 0)
        {
            positions[index] = position;
        }

        var payload = JsonSerializer.Serialize(positions, _serializerOptions);

        await _localStorageService.SetItemAsync(StorageKey, payload);
    }

    public async Task<IReadOnlyList<Guid>> LoadDeletedPositionsAsync()
    {
        var stored = await _localStorageService.GetItemAsync(DeletedKey);
        if (string.IsNullOrWhiteSpace(stored))
        {
            return Array.Empty<Guid>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(stored) ?? new List<Guid>();
        }
        catch
        {
            return Array.Empty<Guid>();
        }
    }

    public async Task MarkDeletedPositionAsync(Guid positionId)
    {
        var existing = (await LoadDeletedPositionsAsync()).ToList();
        if (!existing.Contains(positionId))
        {
            existing.Add(positionId);
            await SaveDeletedPositionsAsync(existing);
        }
    }

    public async Task RemoveDeletedPositionsAsync(IEnumerable<Guid> positionIds)
    {
        var idSet = new HashSet<Guid>(positionIds);
        if (idSet.Count == 0)
        {
            return;
        }

        var existing = (await LoadDeletedPositionsAsync()).Where(id => !idSet.Contains(id)).ToList();
        await SaveDeletedPositionsAsync(existing);
    }


    private Task SaveDeletedPositionsAsync(IEnumerable<Guid> positionIds)
    {
        var payload = JsonSerializer.Serialize(positionIds);
        return _localStorageService.SetItemAsync(DeletedKey, payload).AsTask();
    }
}

