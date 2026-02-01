using System.Text.Json;
using BlazorOptions.ViewModels;

namespace BlazorOptions.Services;

public static class BybitSettingsStorage
{
    public const string StorageKey = "blazor-options-bybit-settings";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static BybitSettings? TryDeserialize(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BybitSettings>(payload, SerializerOptions);
        }
        catch
        {
            return null;
        }
    }

    public static string Serialize(BybitSettings settings)
    {
        return JsonSerializer.Serialize(settings, SerializerOptions);
    }
}
