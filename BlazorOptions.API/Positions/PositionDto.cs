namespace BlazorOptions.API.Positions;

public sealed class PositionDto
{
    public Guid Id { get; set; }

    public string BaseAsset { get; set; } = string.Empty;

    public string QuoteAsset { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;

    public List<LegsCollectionDto> Collections { get; set; } = new();

    public ClosedPositionsDto Closed { get; set; } = new();
}
