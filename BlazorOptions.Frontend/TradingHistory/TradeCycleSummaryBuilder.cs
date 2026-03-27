using System.Globalization;
using BlazorOptions.API.TradingHistory;

namespace BlazorOptions.ViewModels;

public static class TradeCycleSummaryBuilder
{
    public static IReadOnlyList<TradeCycleSummary> BuildTradeCycleSummaries(IEnumerable<TradeRow> trades)
    {
        ArgumentNullException.ThrowIfNull(trades);

        var orderedTrades = trades
            .Where(row => row is not null)
            .OrderBy(row => row.Timestamp ?? 0)
            .ThenBy(row => row.Sequence)
            .ToList();
        if (orderedTrades.Count == 0)
        {
            return Array.Empty<TradeCycleSummary>();
        }

        var summaries = new List<TradeCycleSummary>();
        PositionAccumulator? openPosition = null;
        CloseAccumulator? pendingClose = null;

        foreach (var row in orderedTrades)
        {
            if (!TryParseTrade(row.Trade, out _, out var quantity))
            {
                if (pendingClose is not null)
                {
                    summaries.Add(pendingClose.Build());
                    pendingClose = null;
                }

                continue;
            }

            var sizeAfter = row.SizeAfter;
            var sizeBefore = sizeAfter - GetSignedQuantity(row.Trade, quantity);
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
                var closeFee = AllocateFee(row.Fee, closeQuantity, quantity);
                pendingClose ??= CloseAccumulator.StartFrom(openPosition, row.Timestamp ?? 0);
                pendingClose.AddCloseFill(row.Timestamp ?? 0, row.Price, closeQuantity, closeFee);

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

                var openFee = AllocateFee(row.Fee, openQuantity, quantity);
                if (openPosition is null || openPosition.DirectionSign != afterSign)
                {
                    if (afterSign != 0)
                    {
                        openPosition = PositionAccumulator.Start(afterSign, row.Timestamp ?? 0, row.Price, openQuantity, openFee);
                    }
                }
                else
                {
                    openPosition.AddOpenFill(row.Timestamp ?? 0, row.Price, openQuantity, openFee);
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

    private static decimal GetSignedQuantity(string trade, decimal quantity)
    {
        return trade.StartsWith("SELL", StringComparison.OrdinalIgnoreCase)
            ? -quantity
            : quantity;
    }

    private static decimal AllocateFee(decimal totalFee, decimal partialQuantity, decimal totalQuantity)
    {
        if (totalFee == 0m || partialQuantity <= 0m || totalQuantity <= 0m)
        {
            return 0m;
        }

        return totalFee * (partialQuantity / totalQuantity);
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

        public TradeCycleSummary BuildOpenSummary()
        {
            return new TradeCycleSummary
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

        public TradeCycleSummary Build()
        {
            var closePrice = ClosedQuantity > 0m ? _closeNotional / ClosedQuantity : 0m;
            var grossPnl = DirectionSign > 0
                ? (closePrice - EntryPrice) * ClosedQuantity
                : (EntryPrice - closePrice) * ClosedQuantity;
            var totalFee = _openingFee + _closingFee;

            return new TradeCycleSummary
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
