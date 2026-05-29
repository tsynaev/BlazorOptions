namespace BlazorOptions.ViewModels;

public sealed record PositionTradeSummaryRow
{
    public string Key { get; init; } = Guid.NewGuid().ToString("N");

    public string Symbol { get; init; } = string.Empty;

    public DateTime? SinceDate { get; init; }

    public TradeSummary Summary { get; init; } = new();
}
