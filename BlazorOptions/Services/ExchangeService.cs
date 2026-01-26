using System.Globalization;
using BlazorOptions.ViewModels;

namespace BlazorOptions.Services;

public interface IExchangeService
{
    string? FormatSymbol(LegModel leg, string? baseAsset = null, string settleAsset = null);
    bool TryParseSymbol(string symbol, out string baseAsset, out DateTime expiration, out decimal strike, out LegType type);
    bool TryCreateLeg(string symbol, decimal size, out LegModel leg);
}

public sealed class ExchangeService : IExchangeService
{
    private static readonly string[] ExpirationFormats = { "dMMMyy", "ddMMMyy", "ddMMMyyyy" };

    public string? FormatSymbol(LegModel leg, string? baseAsset = null, string settleAsset = null)
    {
        if (leg is null)
        {
            return null;
        }

        if (leg.Type == LegType.Future)
        {
            if (leg.ExpirationDate.HasValue)
            {
                return $"{baseAsset}{settleAsset}-{leg.ExpirationDate.Value.ToString("ddMMMyy", CultureInfo.InvariantCulture)}".ToUpper();
            }

            return $"{baseAsset}{settleAsset}";
        }

        if (!leg.Strike.HasValue || !leg.ExpirationDate.HasValue)
        {
            return null;
        }

        var typeToken = leg.Type == LegType.Put ? "P" : "C";

        return $"{baseAsset}-{leg.ExpirationDate.Value.ToString("dMMMyy", CultureInfo.InvariantCulture)}-{leg.Strike.Value:0.##}-{typeToken}".ToUpper();
    }

    public bool TryParseSymbol(string symbol, out string baseAsset, out DateTime expiration, out decimal strike, out LegType type)
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

        expiration = parsedExpiration.Date;

        if (!decimal.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out strike))
        {
            return false;
        }

        var typeToken = parts[3].Trim();
        type = typeToken.Equals("P", StringComparison.OrdinalIgnoreCase) ? LegType.Put : LegType.Call;
        return true;
    }

    public bool TryCreateLeg(string symbol, decimal size, out LegModel leg)
    {
        leg = new LegModel();
        var parsed = TryParseSymbol(symbol, out var baseAsset, out var expiration, out var strike, out var type);
        if (!parsed)
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

    private static string? TryExtractBaseAssetFromSymbol(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return null;
        }

        var parts = symbol.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0].ToUpperInvariant() : null;
    }
}
