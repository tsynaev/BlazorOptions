namespace BlazorOptions.API.Positions;

public sealed class ClosedPositionsDto
{
    public bool Include { get; set; }

    public decimal TotalClosePnl { get; set; }

    public decimal TotalFee { get; set; }

    public List<ClosedPositionDto> Positions { get; set; } = new();
}
