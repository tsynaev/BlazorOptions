using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class ExchangeConnectionsService
{
    private readonly ILocalStorageService _localStorageService;

    public ExchangeConnectionsService(ILocalStorageService localStorageService)
    {
        _localStorageService = localStorageService;
    }

    public IReadOnlyList<ExchangeConnectionModel> GetConnections()
    {
        var payload = _localStorageService.GetItem(ExchangeConnectionsStorage.StorageKey);
        var legacyPayload = _localStorageService.GetItem(BybitSettingsStorage.StorageKey);
        return ExchangeConnectionsStorage.Parse(payload, legacyPayload);
    }

    public ExchangeConnectionModel GetConnectionOrDefault(string? connectionId)
    {
        var connections = GetConnections();
        if (!string.IsNullOrWhiteSpace(connectionId))
        {
            var exact = connections.FirstOrDefault(connection =>
                string.Equals(connection.Id, connectionId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact.Clone();
            }
        }

        return connections[0].Clone();
    }

    public string GetDisplayName(string? connectionId)
    {
        var connection = GetConnectionOrDefault(connectionId);
        return string.IsNullOrWhiteSpace(connection.Name) ? connection.Id : connection.Name;
    }

    public Task SaveConnectionsAsync(IReadOnlyList<ExchangeConnectionModel> connections)
    {
        var payload = ExchangeConnectionsStorage.Serialize(connections);
        return _localStorageService.SetItemAsync(ExchangeConnectionsStorage.StorageKey, payload).AsTask();
    }
}
