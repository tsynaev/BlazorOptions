namespace BlazorOptions.ViewModels;

public sealed record PositionTradeCycleSummaryRow
{
    public string Key { get; init; } = Guid.NewGuid().ToString("N");

    public string Symbol { get; init; } = string.Empty;

    public DateTime? SinceDate { get; init; }

    public TradeCycleSummary Summary { get; init; } = new();
}
