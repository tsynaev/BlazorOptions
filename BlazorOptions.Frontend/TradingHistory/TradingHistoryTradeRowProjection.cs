using System.Globalization;
using System.Text.Json;
using BlazorOptions.API.TradingHistory;

namespace BlazorOptions.ViewModels;

public static class TradingHistoryTradeRowProjection
{
    public static IReadOnlyList<TradeRow> BuildTradeRows(IReadOnlyList<TradingHistoryEntry> entries)
    {
        if (entries.Count == 0)
        {
            return Array.Empty<TradeRow>();
        }

        foreach (var entry in entries)
        {
            NormalizeDeliveryDisplay(entry);
        }

        return entries
            .Where(entry =>
                string.Equals(entry.TransactionType, "TRADE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.TransactionType, "DELIVERY", StringComparison.OrdinalIgnoreCase))
            .Select((entry, index) => new TradeRow
            {
                Sequence = index,
                Timestamp = entry.Timestamp,
                Trade = $"{entry.Side} {FormatNumber(entry.Size)} {entry.Symbol}".Trim(),
                Price = entry.Price,
                Fee = entry.Fee,
                SizeAfter = entry.Calculated?.SizeAfter ?? 0m
            })
            .ToList();
    }

    private static void NormalizeDeliveryDisplay(TradingHistoryEntry entry)
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

    private static string FormatNumber(decimal value)
    {
        return value.ToString("0.##########", CultureInfo.InvariantCulture);
    }
}
