namespace BlazorOptions.ViewModels;

public sealed record TradeCycleSummary
{
    public long EntryStartTimestamp { get; init; }

    public long EntryEndTimestamp { get; init; }

    public long CloseStartTimestamp { get; init; }

    public long CloseEndTimestamp { get; init; }

    public string Direction { get; init; } = string.Empty;

    public decimal EntryPrice { get; init; }

    public decimal Size { get; init; }

    public decimal ClosePrice { get; init; }

    public decimal Fee { get; init; }

    public decimal Pnl { get; init; }
}
