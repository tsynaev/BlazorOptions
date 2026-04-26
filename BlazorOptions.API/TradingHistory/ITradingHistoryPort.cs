namespace BlazorOptions.API.TradingHistory;

public interface ITradingHistoryPort
{
    Task SaveTradesAsync(IReadOnlyList<TradingHistoryEntry> entries, string? exchangeConnectionId = null);
    Task<TradingHistoryResult> LoadEntriesAsync(string? baseAsset, int startIndex, int limit, string? exchangeConnectionId = null);
    Task<IReadOnlyList<TradingHistoryEntry>> LoadBySymbolAsync(string symbol, string? category, long? sinceTimestamp, string? exchangeConnectionId = null);
    Task<IReadOnlyList<TradingSummaryBySymbolRow>> LoadSummaryBySymbolAsync(string? exchangeConnectionId = null);
    Task<IReadOnlyList<TradingPnlByCoinRow>> LoadPnlBySettleCoinAsync(string? exchangeConnectionId = null);
    Task<IReadOnlyList<TradingDailyPnlRow>> LoadDailyPnlAsync(long fromTimestamp, long toTimestamp, string? exchangeConnectionId = null);
    Task<TradingHistoryLatestInfo> LoadLatestBySymbolMetaAsync(string symbol, string? category, string? exchangeConnectionId = null);

    Task<TradingHistoryMeta> LoadMetaAsync(string? exchangeConnectionId = null);
    Task SaveMetaAsync(TradingHistoryMeta meta, string? exchangeConnectionId = null);
    Task RecalculateAsync(long? fromTimestamp, string? exchangeConnectionId = null);
}


