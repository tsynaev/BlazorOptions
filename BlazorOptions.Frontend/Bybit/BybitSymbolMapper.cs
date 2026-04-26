using System.Globalization;

namespace BlazorOptions.Services;

public static class BybitSymbolMapper
{
    private static readonly string[] ExpirationFormats = { "dMMMyy", "ddMMMyy", "ddMMMyyyy" };
    private static readonly TimeSpan DefaultDatedContractExpiryTimeUtc = TimeSpan.FromHours(8);

    public static string? FormatSymbol(LegModel leg, string? baseAsset = null, string? settleAsset = null)
    {
        if (leg is null)
        {
            return null;
        }

        var normalizedBase = string.IsNullOrWhiteSpace(baseAsset)
            ? null
            : baseAsset.Trim().ToUpperInvariant();
        var normalizedSettle = string.IsNullOrWhiteSpace(settleAsset)
            ? "USDT"
            : settleAsset.Trim().ToUpperInvariant();
        var settleSuffix = string.Equals(normalizedSettle, "USDC", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : normalizedSettle;

        if (leg.Type == LegType.Spot)
        {
            return normalizedBase;
        }

        if (leg.Type == LegType.Future)
        {
            if (string.IsNullOrWhiteSpace(normalizedBase))
            {
                return null;
            }

            return leg.ExpirationDate.HasValue
                ? $"{normalizedBase}{settleSuffix}-{leg.ExpirationDate.Value.ToString("ddMMMyy", CultureInfo.InvariantCulture)}".ToUpperInvariant()
                : $"{normalizedBase}{settleSuffix}";
        }

        if (!leg.Strike.HasValue || !leg.ExpirationDate.HasValue || string.IsNullOrWhiteSpace(normalizedBase))
        {
            return null;
        }

        var typeToken = leg.Type == LegType.Put ? "P" : "C";
        var settleToken = string.IsNullOrWhiteSpace(settleSuffix) ? string.Empty : $"-{settleSuffix}";

        return $"{normalizedBase}-{leg.ExpirationDate.Value.ToString("dMMMyy", CultureInfo.InvariantCulture)}-{leg.Strike.Value:0.##}-{typeToken}{settleToken}".ToUpperInvariant();
    }

    public static bool TryParseSymbol(string symbol, out string baseAsset, out DateTime expiration, out decimal strike, out LegType type)
    {
        baseAsset = string.Empty;
        expiration = default;
        strike = 0;
        type = LegType.Call;

        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var parts = symbol.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 4)
        {
            return false;
        }

        baseAsset = parts[0];
        if (!DateTime.TryParseExact(parts[1], ExpirationFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedExpiration))
        {
            return false;
        }

        expiration = ResolveDatedContractExpirationUtc(parsedExpiration);
        if (!decimal.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out strike))
        {
            return false;
        }

        type = string.Equals(parts[3].Trim(), "P", StringComparison.OrdinalIgnoreCase)
            ? LegType.Put
            : LegType.Call;
        return true;
    }

    public static bool TryCreateLeg(string symbol, decimal size, out LegModel leg)
    {
        return TryCreateLeg(symbol, size, null, null, out leg);
    }

    public static bool TryCreateLeg(string symbol, decimal size, string? baseAsset, string? category, out LegModel leg)
    {
        leg = new LegModel();

        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var normalizedBase = string.IsNullOrWhiteSpace(baseAsset) ? null : baseAsset.Trim().ToUpperInvariant();
        var normalizedCategory = string.IsNullOrWhiteSpace(category) ? null : category.Trim().ToUpperInvariant();

        if (TryParseSymbol(symbol, out var parsedBase, out var expiration, out var strike, out var type))
        {
            if (!string.IsNullOrWhiteSpace(normalizedBase)
                && !string.Equals(parsedBase, normalizedBase, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            leg.ExpirationDate = expiration;
            leg.Strike = strike;
            leg.Type = type;
            leg.Size = size;
            leg.Symbol = symbol;
            return true;
        }

        if (normalizedCategory is "OPTION")
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(normalizedBase)
            && !symbol.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        leg.Type = LegType.Future;
        leg.Size = size;
        leg.Symbol = symbol;

        var tokens = symbol.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length >= 2
            && DateTime.TryParseExact(tokens[1], ExpirationFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedFutureExpiration))
        {
            leg.ExpirationDate = ResolveDatedContractExpirationUtc(parsedFutureExpiration);
        }

        return true;
    }

    private static DateTime ResolveDatedContractExpirationUtc(DateTime parsedExpiration)
    {
        var utcDate = parsedExpiration.Kind == DateTimeKind.Utc
            ? parsedExpiration.Date
            : DateTime.SpecifyKind(parsedExpiration.Date, DateTimeKind.Utc);
        return utcDate.Add(DefaultDatedContractExpiryTimeUtc);
    }
}
