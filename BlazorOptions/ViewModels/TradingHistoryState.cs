using System;
using System.Collections.Generic;

namespace BlazorOptions.ViewModels;

public record TradingHistoryState
{
    public List<TradingTransactionRecord> Transactions { get; set; } = new();
    public Dictionary<string, string?> OldestCursorByCategory { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public long? RegistrationTimeMs { get; set; }
    public Dictionary<string, long> LatestSyncedTimeMsByCategory { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
