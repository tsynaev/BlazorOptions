using System;
using System.Collections.Generic;

namespace BlazorOptions.ViewModels;

public sealed class TradingHistoryMeta
{
    public long? RegistrationTimeMs { get; set; }

    public Dictionary<string, string?> OldestCursorByCategory { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, long> LatestSyncedTimeMsByCategory { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, long> OldestSyncedTimeMsByCategory { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, decimal> SizeBySymbol { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, decimal> AvgPriceBySymbol { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, decimal> CumulativeBySettleCoin { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public long? CalculatedThroughTimestamp { get; set; }

    public bool RequiresRecalculation { get; set; }
}

