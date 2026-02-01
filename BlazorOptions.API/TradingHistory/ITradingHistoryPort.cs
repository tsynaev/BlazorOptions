namespace BlazorOptions.API.TradingHistory;

public interface ITradingHistoryPort
{
    Task SaveTradesAsync(IReadOnlyList<TradingHistoryEntry> entries);
    Task<TradingHistoryResult> LoadEntriesAsync(string? baseAsset, int startIndex, int limit);
    Task<IReadOnlyList<TradingHistoryEntry>> LoadBySymbolAsync(string symbol, string? category, long? sinceTimestamp);
    Task<IReadOnlyList<TradingSummaryBySymbolRow>> LoadSummaryBySymbolAsync();
    Task<IReadOnlyList<TradingPnlByCoinRow>> LoadPnlBySettleCoinAsync();
    Task<IReadOnlyList<TradingDailyPnlRow>> LoadDailyPnlAsync(long fromTimestamp, long toTimestamp);
    Task<TradingHistoryLatestInfo> LoadLatestBySymbolMetaAsync(string symbol, string? category);

    Task<TradingHistoryMeta> LoadMetaAsync();
    Task SaveMetaAsync(TradingHistoryMeta meta);
    Task RecalculateAsync(long? fromTimestamp);
}


