namespace BlazorOptions.API.Positions;

public sealed class ClosedPositionDto
{
    public Guid Id { get; set; }

    public string Symbol { get; set; } = string.Empty;

    public DateTime? SinceDate { get; set; }

    public long? FirstTradeTimestamp { get; set; }

    public long? LastProcessedTimestamp { get; set; }

    public List<string> LastProcessedIdsAtTimestamp { get; set; } = new();

    public decimal PositionSize { get; set; }

    public decimal AvgPrice { get; set; }

    public decimal EntryQty { get; set; }

    public decimal EntryValue { get; set; }

    public decimal CloseQty { get; set; }

    public decimal CloseValue { get; set; }

    public decimal Realized { get; set; }

    public decimal FeeTotal { get; set; }
}
