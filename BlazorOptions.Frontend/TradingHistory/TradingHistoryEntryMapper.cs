using BlazorOptions.API.TradingHistory;
using BlazorOptions.ViewModels;

namespace BlazorOptions.Services;

public static class TradingHistoryEntryMapper
{
    public static TradingHistoryEntry MapRecordToEntry(TradingTransactionRaw raw)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return new TradingHistoryEntry
        {
            Id = raw.UniqueKey,
            Timestamp = raw.Timestamp ?? 0,
            Symbol = raw.Symbol,
            Category = raw.Category,
            TransactionType = raw.TransactionType,
            Side = raw.Side,
            Size = raw.Qty ?? raw.Size ?? 0m,
            Price = raw.TradePrice ?? 0m,
            Fee = raw.Fee ?? 0m,
            Currency = raw.Currency,
            Change = raw.Change ?? 0m,
            CashFlow = raw.CashFlow ?? 0m,
            OrderId = raw.OrderId,
            OrderLinkId = raw.OrderLinkId,
            TradeId = raw.TradeId,
            RawJson = raw.RawJson,
            ChangedAt = now,
            Calculated = new TradingTransactionCalculated()
        };
    }
}
