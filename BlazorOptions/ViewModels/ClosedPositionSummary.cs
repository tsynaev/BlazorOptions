using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace BlazorOptions.ViewModels;

public sealed record ClosedPositionSummary(
    string Symbol,
    DateTime? Since,
    double Size,
    double AvgEntryPrice,
    double AvgClosePrice,
    double ClosePnl,
    double Fee);

public static class ClosedPositionCalculator
{
    public static IReadOnlyList<ClosedPositionSummary> BuildSummaries(
        IEnumerable<ClosedPositionModel> positions,
        IReadOnlyList<TradingHistoryEntry> entries)
    {
        var summaries = new List<ClosedPositionSummary>();
        foreach (var position in positions)
        {
            if (position is null || string.IsNullOrWhiteSpace(position.Symbol))
            {
                continue;
            }

            summaries.Add(BuildSummary(position, entries));
        }

        return summaries
            .OrderBy(summary => summary.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static double GetNetTotal(
        IEnumerable<ClosedPositionModel> positions,
        IReadOnlyList<TradingHistoryEntry> entries)
    {
        var total = 0m;
        foreach (var summary in BuildSummaries(positions, entries))
        {
            total += (decimal)summary.ClosePnl + (decimal)summary.Fee;
        }

        return (double)total;
    }

    private static ClosedPositionSummary BuildSummary(ClosedPositionModel position, IReadOnlyList<TradingHistoryEntry> entries)
    {
        var normalizedSymbol = position.Symbol.Trim();
        var trades = entries
            .Select(entry => MapTradeEntry(entry))
            .Where(entry => string.Equals(entry.Raw.Symbol, normalizedSymbol, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Timestamp ?? long.MinValue)
            .ThenBy(entry => entry.Raw.UniqueKey, StringComparer.Ordinal)
            .ToList();

        var sinceDate = position.SinceDate;
        if (sinceDate.HasValue)
        {
            trades = trades
                .Where(entry => IsOnOrAfter(entry.Timestamp, sinceDate.Value))
                .ToList();
        }

        var positionSize = 0m;
        var avgPrice = 0m;
        var entryQty = 0m;
        var entryValue = 0m;
        var closeQty = 0m;
        var closeValue = 0m;
        var realized = 0m;
        var feeTotal = 0m;

        var firstTimestamp = trades.Count == 0
            ? long.MaxValue
            : trades
                .Select(entry => entry.Timestamp ?? long.MaxValue)
                .Min();

        foreach (var entry in trades)
        {
            var raw = entry.Raw;
            var qty = Round10(ParseDecimal(raw.Qty) ?? 0m);
            var price = ParseDecimal(raw.TradePrice) ?? 0m;
            var fee = ParseDecimal(raw.Fee) ?? 0m;
            var side = (raw.Side ?? string.Empty).Trim();
            var type = (raw.TransactionType ?? string.Empty).Trim();

            if (string.Equals(type, "SETTLEMENT", StringComparison.OrdinalIgnoreCase))
            {
                qty = 0m;
                price = 0m;
            }
            else if (string.Equals(type, "DELIVERY", StringComparison.OrdinalIgnoreCase))
            {
                var delivery = 0m;
                var strike = 0m;
                var positionValue = 0m;
                if (entry.Data.Count > 0 && entry.Data[0].ValueKind == JsonValueKind.Object)
                {
                    var primary = entry.Data[0];
                    positionValue = ReadDecimal(primary, "position");
                    delivery = ReadDecimal(primary, "deliveryPrice", "tradePrice", "price", "execPrice");
                    strike = ReadDecimal(primary, "strike");
                }

                var optType = '\0';
                if (TryGetOptionDetails(raw.Symbol, out var symbolOptType, out var symbolStrike))
                {
                    optType = symbolOptType;
                    if (strike == 0m)
                    {
                        strike = symbolStrike;
                    }
                }

                var intrinsicCall = Math.Max(delivery - strike, 0m);
                var intrinsicPut = Math.Max(strike - delivery, 0m);

                if (optType == 'C')
                {
                    price = intrinsicCall;
                }
                else if (optType == 'P')
                {
                    price = intrinsicPut;
                }

                if (positionValue != 0m)
                {
                    qty = Math.Abs(positionValue);
                }
            }
            else if (!string.Equals(type, "TRADE", StringComparison.OrdinalIgnoreCase))
            {
                qty = 0m;
            }

            var qtySigned = Round10(string.Equals(side, "SELL", StringComparison.OrdinalIgnoreCase) ? -qty : qty);
            var closeTradeQty = Math.Sign(qtySigned) == -Math.Sign(positionSize)
                ? Round10(Math.Min(Math.Abs(qtySigned), Math.Abs(positionSize)))
                : 0m;
            var openTradeQty = Round10(qtySigned - Math.Sign(qtySigned) * closeTradeQty);

            var cashBefore = -avgPrice * positionSize;
            var cashAfter = cashBefore + (-avgPrice * closeTradeQty * Math.Sign(qtySigned)) + (-price * openTradeQty);
            var positionAfter = Round10(positionSize + qtySigned);
            var avgAfter = Math.Abs(positionAfter) < 0.000000001m ? 0m : -cashAfter / positionAfter;

            if (closeTradeQty != 0m)
            {
                realized += positionSize > 0m
                    ? (price - avgPrice) * closeTradeQty
                    : (avgPrice - price) * closeTradeQty;
            }

            if (openTradeQty != 0m)
            {
                var openQtyAbs = Math.Abs(openTradeQty);
                entryQty += openQtyAbs;
                entryValue += price * openQtyAbs;
            }

            if (closeTradeQty != 0m)
            {
                closeQty += closeTradeQty;
                closeValue += price * closeTradeQty;
            }

            feeTotal += fee;
            positionSize = positionAfter;
            avgPrice = avgAfter;
        }

        var avgEntry = entryQty > 0m ? entryValue / entryQty : 0m;
        var avgClose = closeQty > 0m ? closeValue / closeQty : 0m;
        var computedSinceDate = firstTimestamp == long.MaxValue
            ? (DateTime?)null
            : DateTimeOffset.FromUnixTimeMilliseconds(firstTimestamp).ToLocalTime().DateTime;

        return new ClosedPositionSummary(
            normalizedSymbol,
            sinceDate ?? computedSinceDate,
            (double)closeQty,
            (double)avgEntry,
            (double)avgClose,
            (double)realized,
            (double)feeTotal);
    }

    private static decimal? ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static decimal Round10(decimal value)
    {
        return Math.Round(value, 10, MidpointRounding.AwayFromZero);
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

    private static bool IsOnOrAfter(long? timestamp, DateTime sinceDate)
    {
        if (!timestamp.HasValue)
        {
            return false;
        }

        var tradeDate = DateTimeOffset.FromUnixTimeMilliseconds(timestamp.Value).ToLocalTime().DateTime;
        return tradeDate >= sinceDate;
    }

    private static TradeEntry MapTradeEntry(TradingHistoryEntry entry)
    {
        var raw = new TradingTransactionRaw
        {
            UniqueKey = entry.Id ?? Guid.NewGuid().ToString("N"),
            Symbol = entry.Symbol,
            TransactionType = entry.TransactionType,
            Side = entry.Side,
            Qty = entry.Size,
            TradePrice = entry.Price,
            Fee = entry.Fee,
            Timestamp = entry.Timestamp
        };

        var data = ParseRawData(entry.RawJson);
        return new TradeEntry(raw, data, entry.Timestamp);
    }

    private static IReadOnlyList<JsonElement> ParseRawData(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return Array.Empty<JsonElement>();
        }

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                var data = new List<JsonElement>();
                foreach (var element in root.EnumerateArray())
                {
                    data.Add(element.Clone());
                }
                return data;
            }

            return new[] { root.Clone() };
        }
        catch (JsonException)
        {
            return Array.Empty<JsonElement>();
        }
    }

    private readonly record struct TradeEntry(TradingTransactionRaw Raw, IReadOnlyList<JsonElement> Data, long? Timestamp);
}


