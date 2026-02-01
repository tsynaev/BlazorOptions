namespace BlazorOptions.Sync;

public sealed record PositionSnapshotResponse(DateTime OccurredUtc, PositionSnapshotPayload Payload);
