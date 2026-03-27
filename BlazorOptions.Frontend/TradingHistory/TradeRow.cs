namespace BlazorOptions.ViewModels;

public sealed record TradeRow
{
    public int Sequence { get; init; }

    public long? Timestamp { get; init; }

    public string Trade { get; init; } = string.Empty;

    public decimal Price { get; init; }

    public decimal Fee { get; init; }

    public decimal SizeAfter { get; init; }
}
