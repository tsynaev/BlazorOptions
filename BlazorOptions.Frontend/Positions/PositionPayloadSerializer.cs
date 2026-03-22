using System.Collections.ObjectModel;
using System.Text.Json;

namespace BlazorOptions.Services;

internal static class PositionPayloadSerializer
{
    public const int CurrentVersion = 1;

    public static PositionModel? Deserialize(string payload, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.TryGetProperty("version", out var versionElement))
        {
            return DeserializeVersioned(document.RootElement, options);
        }

        return DeserializeLegacy(document.RootElement.GetRawText(), options);
    }

    public static IReadOnlyList<PositionModel> DeserializeMany(string payload, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Array.Empty<PositionModel>();
        }

        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            var item = Deserialize(payload, options);
            return item is null ? Array.Empty<PositionModel>() : new[] { item };
        }

        var items = new List<PositionModel>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var item = Deserialize(element.GetRawText(), options);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items;
    }

    public static string Serialize(PositionModel position, JsonSerializerOptions options)
    {
        return JsonSerializer.Serialize(new VersionedPositionPayload(CurrentVersion, position), options);
    }

    public static string SerializeMany(IEnumerable<PositionModel> positions, JsonSerializerOptions options)
    {
        var payloads = positions.Select(position => new VersionedPositionPayload(CurrentVersion, position)).ToArray();
        return JsonSerializer.Serialize(payloads, options);
    }

    private static PositionModel? DeserializeVersioned(JsonElement root, JsonSerializerOptions options)
    {
        if (!root.TryGetProperty("version", out var versionElement)
            || versionElement.ValueKind != JsonValueKind.Number
            || !versionElement.TryGetInt32(out var version))
        {
            return null;
        }

        if (version != CurrentVersion)
        {
            throw new JsonException($"Unsupported position payload version '{version}'.");
        }

        if (!root.TryGetProperty("position", out var positionElement))
        {
            return null;
        }

        return JsonSerializer.Deserialize<PositionModel>(positionElement.GetRawText(), options);
    }

    private static PositionModel? DeserializeLegacy(string payload, JsonSerializerOptions options)
    {
        var position = JsonSerializer.Deserialize<PositionModel>(payload, options);
        if (position is null || position.Legs.Count > 0 || !LooksLikeLegacyCollectionsPayload(payload))
        {
            return position;
        }

        var legacy = JsonSerializer.Deserialize<LegacyPositionPayload>(payload, options);
        if (legacy?.Collections is null || legacy.Collections.Count == 0)
        {
            return position;
        }

        var primaryCollection = legacy.Collections[0];
        if (!string.IsNullOrWhiteSpace(primaryCollection.Color))
        {
            position.Color = primaryCollection.Color;
        }

        position.Legs = new ObservableCollection<LegModel>(
            legacy.Collections.SelectMany(collection => (IEnumerable<LegModel>?)collection.Legs ?? Array.Empty<LegModel>()));

        return position;
    }

    private static bool LooksLikeLegacyCollectionsPayload(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.TryGetProperty("collections", out var collections)
            && collections.ValueKind == JsonValueKind.Array;
    }

    private sealed class LegacyPositionPayload
    {
        public List<LegacyLegsCollectionPayload>? Collections { get; set; }
    }

    private sealed class LegacyLegsCollectionPayload
    {
        public string? Color { get; set; }

        public ObservableCollection<LegModel>? Legs { get; set; }
    }

    private sealed record VersionedPositionPayload(int Version, PositionModel Position);
}
