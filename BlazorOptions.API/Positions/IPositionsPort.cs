namespace BlazorOptions.API.Positions;

public interface IPositionsPort
{
    Task<IReadOnlyList<PositionModel>> LoadPositionsAsync();

    Task<PositionModel?> LoadPositionAsync(Guid positionId);
    Task SavePositionsAsync(IReadOnlyList<PositionModel> positions);
    Task SavePositionAsync(PositionModel position);
    Task CompletePositionAsync(Guid positionId);
    Task DeletePositionAsync(Guid positionId);
}
