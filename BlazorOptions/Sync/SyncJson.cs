using System.Text.Json;

namespace BlazorOptions.Sync;

public static class SyncJson
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
