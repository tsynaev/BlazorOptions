using System.Text.Json;
using BlazorOptions.ViewModels;
using Microsoft.Extensions.Options;

namespace BlazorOptions.Services;

public sealed class BybitWalletService : BybitApiService
{
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
                settings.WalletBalanceUri,
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
        var accountType = entry.TryReadString("accountType", out var parsedAccountType)
            ? parsedAccountType
            : fallbackAccountType;
        var coins = new List<ExchangeWalletCoin>();

        if (entry.TryGetProperty("coin", out var coinsElement) && coinsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var coinEntry in coinsElement.EnumerateArray())
            {
                var coin = coinEntry.ReadString("coin");
                if (string.IsNullOrWhiteSpace(coin))
                {
                    continue;
                }

                var mapped = new ExchangeWalletCoin(
                    coin.ToUpperInvariant(),
                    coinEntry.ReadNullableDecimal("equity"),
                    coinEntry.ReadNullableDecimal("walletBalance"),
                    coinEntry.ReadNullableDecimal("availableToWithdraw"),
                    coinEntry.ReadNullableDecimal("usdValue"));
                coins.Add(mapped);
            }
        }

        return new ExchangeWalletSnapshot(
            DateTime.UtcNow,
            accountType.ToUpperInvariant(),
            entry.ReadNullableDecimal("totalEquity"),
            entry.ReadNullableDecimal("totalWalletBalance"),
            entry.ReadNullableDecimal("totalMarginBalance"),
            entry.ReadNullableDecimal("totalInitialMargin"),
            entry.ReadNullableDecimal("totalMaintenanceMargin"),
            entry.ReadNullableDecimal("totalAvailableBalance"),
            entry.ReadNullableDecimal("totalPerpUPL"),
            coins);
    }
}
