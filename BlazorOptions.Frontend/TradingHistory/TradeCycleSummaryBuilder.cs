using System.Globalization;

namespace BlazorOptions.ViewModels;

public static class TradeCycleSummaryBuilder
{
    public static IReadOnlyList<TradeCycleSummary> BuildTradeCycleSummaries(IEnumerable<TradeRow> trades)
    {
        ArgumentNullException.ThrowIfNull(trades);

        var orderedTrades = trades
            .Where(row => row is not null)
            .OrderBy(row => row.Timestamp ?? 0)
            .ThenBy(row => row.Trade, StringComparer.Ordinal)
            .ToList();
        if (orderedTrades.Count == 0)
        {
            return Array.Empty<TradeCycleSummary>();
        }

        var summaries = new List<TradeCycleSummary>();
        CycleAccumulator? currentCycle = null;
        var previousSizeAfter = 0m;

        foreach (var row in orderedTrades)
        {
            if (!TryParseTrade(row.Trade, out var sideSign, out var quantity))
            {
                previousSizeAfter = row.SizeAfter;
                continue;
            }

            var currentSizeAfter = row.SizeAfter;
            if (currentCycle is null)
            {
                // Starting from a flat position avoids stitching together unrelated history windows.
                if (previousSizeAfter == 0m
                    && currentSizeAfter != 0m
                    && Math.Sign(currentSizeAfter) == sideSign)
                {
                    currentCycle = new CycleAccumulator(
                        directionSign: Math.Sign(currentSizeAfter),
                        startTimestamp: row.Timestamp ?? 0);
                    currentCycle.AddOpeningFill(row.Timestamp ?? 0, row.Price, quantity, row.Fee);
                }

                previousSizeAfter = currentSizeAfter;
                continue;
            }

            if (Math.Sign(currentSizeAfter) == currentCycle.DirectionSign || currentSizeAfter == 0m)
            {
                if (sideSign == currentCycle.DirectionSign)
                {
                    currentCycle.AddOpeningFill(row.Timestamp ?? 0, row.Price, quantity, row.Fee);
                }
                else if (sideSign == -currentCycle.DirectionSign)
                {
                    currentCycle.AddClosingFill(row.Timestamp ?? 0, row.Price, quantity, row.Fee);
                }
            }

            if (currentSizeAfter == 0m)
            {
                if (currentCycle.TryBuild(out var summary))
                {
                    summaries.Add(summary);
                }

                currentCycle = null;
            }

            previousSizeAfter = currentSizeAfter;
        }

        if (currentCycle is not null && currentCycle.TryBuild(out var openSummary))
        {
            summaries.Add(openSummary);
        }

        return summaries;
    }

    private static bool TryParseTrade(string? trade, out int sideSign, out decimal quantity)
    {
        sideSign = 0;
        quantity = 0m;

        if (string.IsNullOrWhiteSpace(trade))
        {
            return false;
        }

        var parts = trade.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        sideSign = parts[0].Equals("BUY", StringComparison.OrdinalIgnoreCase)
            ? 1
            : parts[0].Equals("SELL", StringComparison.OrdinalIgnoreCase)
                ? -1
                : 0;
        if (sideSign == 0)
        {
            return false;
        }

        if (!decimal.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out quantity))
        {
            return false;
        }

        quantity = Math.Abs(quantity);
        return quantity > 0m;
    }

    private sealed class CycleAccumulator
    {
        private decimal _openNotional;
        private decimal _openQuantity;
        private decimal _closeNotional;
        private decimal _closeQuantity;
        private decimal _fee;

        public CycleAccumulator(int directionSign, long startTimestamp)
        {
            DirectionSign = directionSign;
            StartTimestamp = startTimestamp;
        }

        public int DirectionSign { get; }

        public long StartTimestamp { get; }

        public long OpenEndTimestamp { get; private set; }

        public long? CloseStartTimestamp { get; private set; }

        public long? CloseEndTimestamp { get; private set; }

        public void AddOpeningFill(long timestamp, decimal price, decimal quantity, decimal fee)
        {
            _openNotional += price * quantity;
            _openQuantity += quantity;
            _fee += fee;
            OpenEndTimestamp = timestamp;
        }

        public void AddClosingFill(long timestamp, decimal price, decimal quantity, decimal fee)
        {
            _closeNotional += price * quantity;
            _closeQuantity += quantity;
            _fee += fee;

            if (!CloseStartTimestamp.HasValue)
            {
                CloseStartTimestamp = timestamp;
            }

            CloseEndTimestamp = timestamp;
        }

        public bool TryBuild(out TradeCycleSummary summary)
        {
            summary = default!;

            if (_openQuantity <= 0m)
            {
                return false;
            }

            var entryPrice = _openNotional / _openQuantity;
            var hasClose = _closeQuantity > 0m && CloseStartTimestamp.HasValue && CloseEndTimestamp.HasValue;
            decimal? closePrice = null;
            decimal? pnl = null;

            if (hasClose)
            {
                closePrice = _closeNotional / _closeQuantity;
                var grossPnl = DirectionSign > 0
                    ? (closePrice.Value - entryPrice) * _openQuantity
                    : (entryPrice - closePrice.Value) * _openQuantity;
                pnl = grossPnl - _fee;
            }
            else
            {
                // Open cycles only have realized drag from fees until a closing fill exists.
                pnl = -_fee;
            }

            summary = new TradeCycleSummary
            {
                EntryStartTimestamp = StartTimestamp,
                EntryEndTimestamp = OpenEndTimestamp,
                CloseStartTimestamp = CloseStartTimestamp,
                CloseEndTimestamp = CloseEndTimestamp,
                Direction = hasClose
                    ? DirectionSign > 0 ? "Open/Close Long" : "Open/Close Short"
                    : DirectionSign > 0 ? "Open Long" : "Open Short",
                EntryPrice = entryPrice,
                Size = _openQuantity,
                ClosePrice = closePrice,
                Fee = _fee,
                Pnl = pnl
            };
            return true;
        }
    }
}
