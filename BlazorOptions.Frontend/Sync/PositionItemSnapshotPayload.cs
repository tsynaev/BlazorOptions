using BlazorOptions.API.Positions;

namespace BlazorOptions.Sync;

public sealed record PositionItemSnapshotPayload
{
    public Guid PositionId { get; init; }

    public PositionModel? Position { get; init; }

    public bool IsDeleted { get; init; }
}
