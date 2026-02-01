namespace BlazorOptions.API.Positions;

public interface IPositionsPort
{
    Task<IReadOnlyList<PositionDto>> LoadPositionsAsync();
    Task SavePositionsAsync(IReadOnlyList<PositionDto> positions);
    Task SavePositionAsync(PositionDto position);
    Task DeletePositionAsync(Guid positionId);
}
