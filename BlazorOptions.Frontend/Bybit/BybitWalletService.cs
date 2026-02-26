using System.Globalization;
using System.Text.Json;
using BlazorOptions.ViewModels;
using Microsoft.Extensions.Options;

namespace BlazorOptions.Services;

public sealed class BybitWalletService : BybitApiService
{
    private const string RequestPath = "/v5/account/wallet-balance";
    private static readonly string[] AccountTypes = ["UNIFIED", "CONTRACT"];
    private readonly IOptions<BybitSettings> _bybitSettingsOptions;

    public BybitWalletService(HttpClient httpClient, IOptions<BybitSettings> bybitSettingsOptions)
        : base(httpClient)
    {
        _bybitSettingsOptions = bybitSettingsOptions;
    }

    public async Task<ExchangeWalletSnapshot?> GetWalletSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var settings = _bybitSettingsOptions.Value;

        foreach (var accountType in AccountTypes)
        {
            var parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["accountType"] = accountType
            };
            var queryString = BuildQueryString(parameters);

            var payload = await SendSignedRequestAsync(
                HttpMethod.Get,
                RequestPath,
                settings,
                queryString,
                cancellationToken: cancellationToken);

            using var document = JsonDocument.Parse(payload);
            ThrowIfRetCodeError(document.RootElement);
            if (!document.RootElement.TryGetProperty("result", out var resultElement))
            {
                continue;
            }

            if (!resultElement.TryGetProperty("list", out var listElement)
                || listElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in listElement.EnumerateArray())
            {
                var snapshot = MapWalletEntry(entry, accountType);
                if (snapshot is not null)
                {
                    return snapshot;
                }
            }
        }

        return null;
    }

    private static ExchangeWalletSnapshot? MapWalletEntry(JsonElement entry, string fallbackAccountType)
    {
        var accountType = ReadString(entry, "accountType") ?? fallbackAccountType;
        var coins = new List<ExchangeWalletCoin>();

        if (entry.TryGetProperty("coin", out var coinsElement) && coinsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var coinEntry in coinsElement.EnumerateArray())
            {
                var coin = ReadString(coinEntry, "coin");
                if (string.IsNullOrWhiteSpace(coin))
                {
                    continue;
                }

                var mapped = new ExchangeWalletCoin(
                    coin.ToUpperInvariant(),
                    ReadDecimal(coinEntry, "equity"),
                    ReadDecimal(coinEntry, "walletBalance"),
                    ReadDecimal(coinEntry, "availableToWithdraw"),
                    ReadDecimal(coinEntry, "usdValue"));
                coins.Add(mapped);
            }
        }

        return new ExchangeWalletSnapshot(
            DateTime.UtcNow,
            accountType.ToUpperInvariant(),
            ReadDecimal(entry, "totalEquity"),
            ReadDecimal(entry, "totalWalletBalance"),
            ReadDecimal(entry, "totalMarginBalance"),
            ReadDecimal(entry, "totalInitialMargin"),
            ReadDecimal(entry, "totalMaintenanceMargin"),
            ReadDecimal(entry, "totalAvailableBalance"),
            ReadDecimal(entry, "totalPerpUPL"),
            coins);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim()
            : property.GetRawText().Trim();
    }

    private static decimal? ReadDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var numeric))
        {
            return numeric;
        }

        var raw = property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.GetRawText();

        return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
