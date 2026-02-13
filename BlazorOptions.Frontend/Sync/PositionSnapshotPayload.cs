using BlazorOptions.API.Positions;

namespace BlazorOptions.Sync;

public sealed record PositionSnapshotPayload
{
    public List<PositionModel> Positions { get; init; } = new();
}
