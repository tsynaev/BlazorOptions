using BlazorOptions.ViewModels;

namespace BlazorOptions.Services;

public sealed record ExchangeTransactionQuery
{
    public string Category { get; init; } = "linear";
    public int Limit { get; init; } = 50;
    public string? Cursor { get; init; }
    public long? StartTime { get; init; }
    public long? EndTime { get; init; }
}

public sealed record ExchangeTransactionPage(IReadOnlyList<TradingTransactionRaw> Items, string? NextCursor);

public interface ITransactionHistoryService
{
    Task<ExchangeTransactionPage> GetTransactionsPageAsync(
        ExchangeTransactionQuery query,
        CancellationToken cancellationToken = default);
}
