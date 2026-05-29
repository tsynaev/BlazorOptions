using System.Globalization;
using System.Text.Json;
using BlazorOptions.API.TradingHistory;

namespace BlazorOptions.ViewModels;

public static class TradeSummaryBuilder
{
    public static IReadOnlyList<TradeSummary> BuildTradeSummaries(IEnumerable<TradingHistoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var orderedEntries = entries
            .Where(entry => entry is not null)
            .Where(entry =>
                string.Equals(entry.TransactionType, "TRADE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.TransactionType, "DELIVERY", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Timestamp ?? 0)
            .ToList();
        if (orderedEntries.Count == 0)
        {
            return Array.Empty<TradeSummary>();
        }

        foreach (var entry in orderedEntries)
        {
            NormalizeDeliveryPrice(entry);
        }

        var summaries = new List<TradeSummary>();
        foreach (var group in orderedEntries.GroupBy(CreateGroupKey, StringComparer.OrdinalIgnoreCase))
        {
            var groupedEntries = group.ToList();
            if (groupedEntries.Count == 0)
            {
                continue;
            }

            var symbol = groupedEntries[0].Symbol?.Trim() ?? string.Empty;
            var category = groupedEntries[0].Category?.Trim() ?? string.Empty;
            var groupedSummaries = BuildTradeSummariesForSingleSymbol(groupedEntries);
            summaries.AddRange(groupedSummaries.Select(summary => summary with
            {
                Symbol = symbol,
                Category = category
            }));
        }

        return summaries
            .OrderBy(summary => summary.EntryStartTimestamp)
            .ThenBy(summary => summary.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<TradeSummary> BuildTradeSummariesForSingleSymbol(IReadOnlyList<TradingHistoryEntry> orderedEntries)
    {
        var summaries = new List<TradeSummary>();
        PositionAccumulator? openPosition = null;
        CloseAccumulator? pendingClose = null;

        foreach (var entry in orderedEntries)
        {
            if (!TryGetSignedQuantity(entry, out var signedQuantity, out var quantity))
            {
                if (pendingClose is not null)
                {
                    summaries.Add(pendingClose.Build());
                    pendingClose = null;
                }

                continue;
            }

            var sizeAfter = entry.Calculated?.SizeAfter ?? 0m;
            var sizeBefore = sizeAfter - signedQuantity;
            var change = TradingPositionChange.Resolve(sizeBefore, sizeAfter);
            var beforeSign = Math.Sign(sizeBefore);
            var afterSign = Math.Sign(sizeAfter);
            var closeQuantity = change.CloseQuantity;
            var openQuantity = Math.Abs(change.OpenQuantitySigned);

            if (pendingClose is not null && closeQuantity <= 0m)
            {
                summaries.Add(pendingClose.Build());
                pendingClose = null;
            }

            if (closeQuantity > 0m && openPosition is not null && beforeSign == openPosition.DirectionSign)
            {
                var closeFee = AllocateFee(entry.Fee, closeQuantity, quantity);
                pendingClose ??= CloseAccumulator.StartFrom(openPosition, entry.Timestamp ?? 0);
                pendingClose.AddCloseFill(entry.Timestamp ?? 0, entry.Price, closeQuantity, closeFee);

                openPosition.AllocateClose(closeQuantity, out var openingFeeShare);
                pendingClose.AddOpeningFee(openingFeeShare);

                if (openPosition.Quantity <= 0m)
                {
                    openPosition = null;
                }
            }

            if (openQuantity > 0m)
            {
                if (pendingClose is not null)
                {
                    summaries.Add(pendingClose.Build());
                    pendingClose = null;
                }

                var openFee = AllocateFee(entry.Fee, openQuantity, quantity);
                if (openPosition is null || openPosition.DirectionSign != afterSign)
                {
                    if (afterSign != 0)
                    {
                        openPosition = PositionAccumulator.Start(afterSign, entry.Timestamp ?? 0, entry.Price, openQuantity, openFee);
                    }
                }
                else
                {
                    openPosition.AddOpenFill(entry.Timestamp ?? 0, entry.Price, openQuantity, openFee);
                }
            }
        }

        if (pendingClose is not null)
        {
            summaries.Add(pendingClose.Build());
        }

        if (openPosition is not null)
        {
            summaries.Add(openPosition.BuildOpenSummary());
        }

        return summaries;
    }

    private static string CreateGroupKey(TradingHistoryEntry entry)
    {
        var symbol = entry.Symbol?.Trim().ToUpperInvariant() ?? string.Empty;
        var category = entry.Category?.Trim().ToUpperInvariant() ?? string.Empty;
        return $"{symbol}|{category}";
    }

    private static bool TryGetSignedQuantity(TradingHistoryEntry entry, out decimal signedQuantity, out decimal quantity)
    {
        signedQuantity = 0m;
        quantity = 0m;

        if (string.IsNullOrWhiteSpace(entry.Side))
        {
            return false;
        }

        quantity = Math.Abs(entry.Size);
        if (quantity <= 0m)
        {
            return false;
        }

        signedQuantity = string.Equals(entry.Side, "SELL", StringComparison.OrdinalIgnoreCase)
            ? -quantity
            : string.Equals(entry.Side, "BUY", StringComparison.OrdinalIgnoreCase)
                ? quantity
                : 0m;
        return signedQuantity != 0m;
    }

    private static decimal AllocateFee(decimal totalFee, decimal partialQuantity, decimal totalQuantity)
    {
        if (totalFee == 0m || partialQuantity <= 0m || totalQuantity <= 0m)
        {
            return 0m;
        }

        return totalFee * (partialQuantity / totalQuantity);
    }

    private static void NormalizeDeliveryPrice(TradingHistoryEntry entry)
    {
        if (!string.Equals(entry.TransactionType, "DELIVERY", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(entry.RawJson))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(entry.RawJson);
            var primary = doc.RootElement;
            if (primary.ValueKind == JsonValueKind.Array)
            {
                primary = primary.EnumerateArray().FirstOrDefault();
            }

            if (primary.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var delivery = ReadDecimal(primary, "deliveryPrice", "tradePrice", "price", "execPrice");
            var strike = ReadDecimal(primary, "strike");
            var optType = '\0';

            if (TryGetOptionDetails(entry.Symbol, out var symbolOptType, out var symbolStrike))
            {
                optType = symbolOptType;
                if (strike == 0m)
                {
                    strike = symbolStrike;
                }
            }

            if (optType == 'C')
            {
                entry.Price = Math.Max(delivery - strike, 0m);
            }
            else if (optType == 'P')
            {
                entry.Price = Math.Max(strike - delivery, 0m);
            }
        }
        catch
        {
        }
    }

    private static decimal ReadDecimal(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var raw = value.GetString();
                if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
            }
        }

        return 0m;
    }

    private static bool TryGetOptionDetails(string? symbol, out char optType, out decimal strike)
    {
        optType = '\0';
        strike = 0m;

        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var parts = symbol.Trim().ToUpperInvariant()
            .Split('-', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i] == "C" || parts[i] == "P")
            {
                optType = parts[i][0];
                if (i > 0)
                {
                    decimal.TryParse(parts[i - 1], NumberStyles.Any, CultureInfo.InvariantCulture, out strike);
                }

                return true;
            }
        }

        return false;
    }

    private sealed class PositionAccumulator
    {
        private PositionAccumulator(int directionSign, long timestamp, decimal price, decimal quantity, decimal openingFee)
        {
            DirectionSign = directionSign;
            EntryStartTimestamp = timestamp;
            EntryEndTimestamp = timestamp;
            Quantity = quantity;
            EntryPrice = price;
            OpeningFee = openingFee;
        }

        public int DirectionSign { get; }

        public long EntryStartTimestamp { get; private set; }

        public long EntryEndTimestamp { get; private set; }

        public decimal Quantity { get; private set; }

        public decimal EntryPrice { get; private set; }

        public decimal OpeningFee { get; private set; }

        public static PositionAccumulator Start(int directionSign, long timestamp, decimal price, decimal quantity, decimal openingFee)
        {
            return new PositionAccumulator(directionSign, timestamp, price, quantity, openingFee);
        }

        public void AddOpenFill(long timestamp, decimal price, decimal quantity, decimal openingFee)
        {
            if (quantity <= 0m)
            {
                return;
            }

            var totalQuantity = Quantity + quantity;
            EntryPrice = totalQuantity > 0m
                ? ((EntryPrice * Quantity) + (price * quantity)) / totalQuantity
                : 0m;
            Quantity = totalQuantity;
            OpeningFee += openingFee;
            EntryEndTimestamp = timestamp;
        }

        public void AllocateClose(decimal closeQuantity, out decimal openingFeeShare)
        {
            if (closeQuantity <= 0m || Quantity <= 0m)
            {
                openingFeeShare = 0m;
                return;
            }

            openingFeeShare = OpeningFee * (closeQuantity / Quantity);
            OpeningFee -= openingFeeShare;
            Quantity -= closeQuantity;
        }

        public TradeSummary BuildOpenSummary()
        {
            return new TradeSummary
            {
                EntryStartTimestamp = EntryStartTimestamp,
                EntryEndTimestamp = EntryEndTimestamp,
                Direction = DirectionSign > 0 ? "Open Long" : "Open Short",
                EntryPrice = EntryPrice,
                Size = Quantity,
                Fee = OpeningFee,
                Pnl = -OpeningFee
            };
        }
    }

    private sealed class CloseAccumulator
    {
        private decimal _closeNotional;
        private decimal _closingFee;
        private decimal _openingFee;

        private CloseAccumulator(
            int directionSign,
            long entryStartTimestamp,
            long entryEndTimestamp,
            decimal entryPrice,
            long closeStartTimestamp)
        {
            DirectionSign = directionSign;
            EntryStartTimestamp = entryStartTimestamp;
            EntryEndTimestamp = entryEndTimestamp;
            EntryPrice = entryPrice;
            CloseStartTimestamp = closeStartTimestamp;
            CloseEndTimestamp = closeStartTimestamp;
        }

        public int DirectionSign { get; }

        public long EntryStartTimestamp { get; }

        public long EntryEndTimestamp { get; }

        public decimal EntryPrice { get; }

        public long CloseStartTimestamp { get; }

        public long CloseEndTimestamp { get; private set; }

        public decimal ClosedQuantity { get; private set; }

        public static CloseAccumulator StartFrom(PositionAccumulator position, long closeTimestamp)
        {
            return new CloseAccumulator(
                position.DirectionSign,
                position.EntryStartTimestamp,
                position.EntryEndTimestamp,
                position.EntryPrice,
                closeTimestamp);
        }

        public void AddCloseFill(long timestamp, decimal price, decimal quantity, decimal closingFee)
        {
            _closeNotional += price * quantity;
            _closingFee += closingFee;
            ClosedQuantity += quantity;
            CloseEndTimestamp = timestamp;
        }

        public void AddOpeningFee(decimal openingFeeShare)
        {
            _openingFee += openingFeeShare;
        }

        public TradeSummary Build()
        {
            var closePrice = ClosedQuantity > 0m ? _closeNotional / ClosedQuantity : 0m;
            var grossPnl = DirectionSign > 0
                ? (closePrice - EntryPrice) * ClosedQuantity
                : (EntryPrice - closePrice) * ClosedQuantity;
            var totalFee = _openingFee + _closingFee;

            return new TradeSummary
            {
                EntryStartTimestamp = EntryStartTimestamp,
                EntryEndTimestamp = EntryEndTimestamp,
                CloseStartTimestamp = CloseStartTimestamp,
                CloseEndTimestamp = CloseEndTimestamp,
                Direction = DirectionSign > 0 ? "Open/Close Long" : "Open/Close Short",
                EntryPrice = EntryPrice,
                Size = ClosedQuantity,
                ClosePrice = closePrice,
                Fee = totalFee,
                Pnl = grossPnl - totalFee
            };
        }
    }
}
