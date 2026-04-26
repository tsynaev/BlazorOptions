using System.Text.Json;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public static class ExchangeConnectionsStorage
{
    public const string StorageKey = "blazor-options-exchange-connections";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static IReadOnlyList<ExchangeConnectionModel> CreateDefaults()
    {
        return [ExchangeConnectionModel.CreateBybitMain()];
    }

    public static IReadOnlyList<ExchangeConnectionModel> Parse(string? payload, string? legacyBybitPayload = null)
    {
        var parsed = TryDeserialize(payload);
        if (parsed.Count > 0)
        {
            return Normalize(parsed);
        }

        if (!string.IsNullOrWhiteSpace(legacyBybitPayload))
        {
            var legacy = BybitSettingsStorage.TryDeserialize(legacyBybitPayload);
            if (legacy is not null)
            {
                var migrated = ExchangeConnectionModel.CreateBybitMain();
                migrated.ApiKey = legacy.ApiKey;
                migrated.ApiSecret = legacy.ApiSecret;
                migrated.LivePriceUpdateIntervalMilliseconds = legacy.LivePriceUpdateIntervalMilliseconds;
                migrated.OptionBaseCoins = legacy.OptionBaseCoins;
                migrated.OptionQuoteCoins = legacy.OptionQuoteCoins;
                return Normalize([migrated]);
            }
        }

        return Normalize(CreateDefaults());
    }

    public static string Serialize(IEnumerable<ExchangeConnectionModel> connections)
    {
        var normalized = Normalize(connections);
        return JsonSerializer.Serialize(normalized, SerializerOptions);
    }

    private static IReadOnlyList<ExchangeConnectionModel> TryDeserialize(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Array.Empty<ExchangeConnectionModel>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<ExchangeConnectionModel>>(payload, SerializerOptions)
                ?? new List<ExchangeConnectionModel>();
        }
        catch
        {
            return Array.Empty<ExchangeConnectionModel>();
        }
    }

    private static IReadOnlyList<ExchangeConnectionModel> Normalize(IEnumerable<ExchangeConnectionModel> connections)
    {
        var normalized = connections
            .Where(connection => connection is not null)
            .Select(connection => NormalizeConnection(connection))
            .GroupBy(connection => connection.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(connection => connection.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!normalized.Any(connection => string.Equals(connection.Id, ExchangeConnectionModel.BybitMainId, StringComparison.OrdinalIgnoreCase)))
        {
            normalized.Insert(0, ExchangeConnectionModel.CreateBybitMain());
        }

        return normalized;
    }

    private static ExchangeConnectionModel NormalizeConnection(ExchangeConnectionModel source)
    {
        var connection = source.Clone();
        connection.Id = string.IsNullOrWhiteSpace(connection.Id)
            ? Guid.NewGuid().ToString("N")
            : connection.Id.Trim();
        connection.Name = string.IsNullOrWhiteSpace(connection.Name) ? connection.Id : connection.Name.Trim();
        connection.Provider = string.IsNullOrWhiteSpace(connection.Provider)
            ? ExchangeConnectionModel.BybitProvider
            : connection.Provider.Trim().ToLowerInvariant();
        connection.LivePriceUpdateIntervalMilliseconds = Math.Max(100, connection.LivePriceUpdateIntervalMilliseconds);
        connection.OptionBaseCoins = string.IsNullOrWhiteSpace(connection.OptionBaseCoins) ? "BTC, ETH, SOL" : connection.OptionBaseCoins;
        connection.OptionQuoteCoins = string.IsNullOrWhiteSpace(connection.OptionQuoteCoins) ? "USDT" : connection.OptionQuoteCoins;
        return connection;
    }
}
