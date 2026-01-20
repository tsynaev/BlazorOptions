using System.Text.Json;

namespace BlazorOptions.Sync;

public sealed record EventEnvelope
{
    public Guid EventId { get; init; }

    public string DeviceId { get; init; } = string.Empty;

    public DateTime OccurredUtc { get; init; }

    public string Kind { get; init; } = string.Empty;

    public JsonElement Payload { get; init; }
}
