using System.Globalization;
using System.Text.Json;
using BlazorOptions.API.TradingHistory;

namespace BlazorOptions.Server.Services;

public static class TradingHistoryCalculator
{
    public static void ApplyCalculatedFields(
        IReadOnlyList<TradingHistoryEntry> entries,
        CalculationState state,
        bool updateEntries = true)
    {
        foreach (var entry in entries)
        {
            var qty = Round10(entry.Size);
            var price = entry.Price;
            var fee = entry.Fee;
            var side = (entry.Side ?? string.Empty).Trim();
            var type = (entry.TransactionType ?? string.Empty).Trim();

            if (string.Equals(type, "SETTLEMENT", StringComparison.OrdinalIgnoreCase))
            {
                qty = 0m;
                price = 0m;
            }
            else if (string.Equals(type, "DELIVERY", StringComparison.OrdinalIgnoreCase))
            {
                ApplyDeliveryDetails(entry, ref qty, ref price);
            }
            else if (!string.Equals(type, "TRADE", StringComparison.OrdinalIgnoreCase))
            {
                qty = 0m;
            }

            var qtySigned = Round10(string.Equals(side, "SELL", StringComparison.OrdinalIgnoreCase) ? -qty : qty);

            state.SizeBySymbol.TryGetValue(entry.Symbol, out var posBefore);
            state.AvgPriceBySymbol.TryGetValue(entry.Symbol, out var avgBefore);
            posBefore = Round10(posBefore);

            var closeQty = Math.Sign(qtySigned) == -Math.Sign(posBefore)
                ? Round10(Math.Min(Math.Abs(qtySigned), Math.Abs(posBefore)))
                : 0m;
            var openQty = Round10(qtySigned - Math.Sign(qtySigned) * closeQty);

            var cashBefore = -avgBefore * posBefore;
            var cashAfter = cashBefore + (-avgBefore * closeQty * Math.Sign(qtySigned)) + (-price * openQty);
            var posAfter = Round10(posBefore + qtySigned);
            var avgAfter = Math.Abs(posAfter) < 0.000000001m ? 0m : -cashAfter / posAfter;

            var realized = 0m;
            if (closeQty != 0m)
            {
                realized = posBefore > 0m
                    ? (price - avgBefore) * closeQty
                    : (avgBefore - price) * closeQty;
            }

            var settleCoin = GetSettleCoin(entry.Currency);
            state.CumulativeBySettleCoin.TryGetValue(settleCoin, out var cumulativeBefore);
            var cumulativeAfter = realized + cumulativeBefore - fee;

            state.SizeBySymbol[entry.Symbol] = posAfter;
            state.AvgPriceBySymbol[entry.Symbol] = avgAfter;
            state.CumulativeBySettleCoin[settleCoin] = cumulativeAfter;

            if (updateEntries)
            {
                entry.Calculated = new TradingTransactionCalculated
                {
                    SizeAfter = posAfter,
                    AvgPriceAfter = avgAfter,
                    RealizedPnl = realized,
                    CumulativePnl = cumulativeAfter
                };
                entry.ChangedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
        }
    }

    public static string GetSettleCoin(string? currency)
    {
        var settleCoin = string.IsNullOrWhiteSpace(currency)
            ? "_unknown"
            : currency;

        return string.IsNullOrWhiteSpace(settleCoin) ? "_unknown" : settleCoin;
    }

    public static string FormatDecimal(decimal value)
    {
        return value.ToString("0.##########", CultureInfo.InvariantCulture);
    }

    public static decimal Round10(decimal value)
    {
        return Math.Round(value, 10, MidpointRounding.AwayFromZero);
    }

    public sealed class CalculationState
    {
        public Dictionary<string, decimal> SizeBySymbol { get; }
        public Dictionary<string, decimal> AvgPriceBySymbol { get; }
        public Dictionary<string, decimal> CumulativeBySettleCoin { get; }

        public CalculationState()
        {
            SizeBySymbol = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            AvgPriceBySymbol = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            CumulativeBySettleCoin = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        }

        public CalculationState(TradingHistoryMeta meta)
        {
            SizeBySymbol = new Dictionary<string, decimal>(meta.SizeBySymbol, StringComparer.OrdinalIgnoreCase);
            AvgPriceBySymbol = new Dictionary<string, decimal>(meta.AvgPriceBySymbol, StringComparer.OrdinalIgnoreCase);
            CumulativeBySettleCoin = new Dictionary<string, decimal>(meta.CumulativeBySettleCoin, StringComparer.OrdinalIgnoreCase);
        }

        public void ApplyToMeta(TradingHistoryMeta meta)
        {
            meta.SizeBySymbol = new Dictionary<string, decimal>(SizeBySymbol, StringComparer.OrdinalIgnoreCase);
            meta.AvgPriceBySymbol = new Dictionary<string, decimal>(AvgPriceBySymbol, StringComparer.OrdinalIgnoreCase);
            meta.CumulativeBySettleCoin = new Dictionary<string, decimal>(CumulativeBySettleCoin, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void ApplyDeliveryDetails(TradingHistoryEntry entry, ref decimal qty, ref decimal price)
    {
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

            var position = ReadDecimal(primary, "position");
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

            if (position != 0m)
            {
                qty = Math.Abs(position);
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
}
