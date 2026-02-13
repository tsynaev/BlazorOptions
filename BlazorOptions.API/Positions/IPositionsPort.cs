namespace BlazorOptions.API.Positions;

public interface IPositionsPort
{
    Task<IReadOnlyList<PositionModel>> LoadPositionsAsync();
    Task SavePositionsAsync(IReadOnlyList<PositionModel> positions);
    Task SavePositionAsync(PositionModel position);
    Task DeletePositionAsync(Guid positionId);
}
