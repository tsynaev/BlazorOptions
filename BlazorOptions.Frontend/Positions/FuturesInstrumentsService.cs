using System.Globalization;
using System.Linq;
using System.Text.Json;
using BlazorOptions.ViewModels;
using Microsoft.Extensions.Options;

namespace BlazorOptions.Services;

public sealed class FuturesInstrumentsService : IFuturesInstrumentsService
{
    private const int PageSize = 500;
    private readonly HttpClient _httpClient;
    private readonly IOptions<BybitSettings> _bybitSettingsOptions;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, List<DateTime?>> _cachedExpirations = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ExchangeTradingPair>? _cachedTradingPairs;

    public FuturesInstrumentsService(HttpClient httpClient, IOptions<BybitSettings> bybitSettingsOptions)
    {
        _httpClient = httpClient;
        _bybitSettingsOptions = bybitSettingsOptions;
    }

    public IReadOnlyList<DateTime?> GetCachedExpirations(string baseAsset, string? quoteAsset)
    {
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            return Array.Empty<DateTime?>();
        }

        var key = BuildCacheKey(baseAsset, quoteAsset);

        lock (_cacheLock)
        {
            return _cachedExpirations.TryGetValue(key, out var cached)
                ? cached
                : Array.Empty<DateTime?>();
        }
    }

    public async Task EnsureExpirationsAsync(string baseAsset, string? quoteAsset, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            return;
        }

        var key = BuildCacheKey(baseAsset, quoteAsset);
        lock (_cacheLock)
        {
            if (_cachedExpirations.ContainsKey(key))
            {
                return;
            }
        }

        if (!await _refreshLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            var expirations = await FetchExpirationsAsync(baseAsset, quoteAsset, cancellationToken);

            // Cache per asset pair to avoid repeated network calls while users edit multiple legs.
            lock (_cacheLock)
            {
                _cachedExpirations[key] = expirations;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<IReadOnlyList<ExchangeTradingPair>> GetTradingPairsAsync(CancellationToken cancellationToken = default)
    {
        lock (_cacheLock)
        {
            if (_cachedTradingPairs is not null)
            {
                return _cachedTradingPairs;
            }
        }

        if (!await _refreshLock.WaitAsync(0, cancellationToken))
        {
            lock (_cacheLock)
            {
                return _cachedTradingPairs ?? Array.Empty<ExchangeTradingPair>();
            }
        }

        try
        {
            var pairs = await FetchTradingPairsAsync(cancellationToken);
            lock (_cacheLock)
            {
                _cachedTradingPairs = pairs;
                return _cachedTradingPairs;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static string BuildCacheKey(string baseAsset, string? quoteAsset)
    {
        var normalizedBase = baseAsset.Trim().ToUpperInvariant();
        var normalizedQuote = string.IsNullOrWhiteSpace(quoteAsset) ? string.Empty : quoteAsset.Trim().ToUpperInvariant();
        return $"{normalizedBase}|{normalizedQuote}";
    }

    private async Task<List<DateTime?>> FetchExpirationsAsync(string baseAsset, string? quoteAsset, CancellationToken cancellationToken)
    {
        var results = new HashSet<DateTime>();
        var normalizedBase = baseAsset.Trim().ToUpperInvariant();
        var normalizedQuote = string.IsNullOrWhiteSpace(quoteAsset) ? string.Empty : quoteAsset.Trim().ToUpperInvariant();
        var applyQuoteFilter = !string.IsNullOrWhiteSpace(normalizedQuote);
        string? cursor = null;

        do
        {
            var url = BuildInstrumentsUrl(_bybitSettingsOptions.Value.InstrumentsInfoUri, normalizedBase, cursor);
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Bybit instruments request failed ({(int)response.StatusCode}).");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            ThrowIfRetCodeError(root);

            if (!root.TryGetProperty("result", out var resultElement))
            {
                break;
            }

            if (resultElement.TryGetProperty("list", out var listElement) && listElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in listElement.EnumerateArray())
                {
                    if (!entry.TryReadString("baseCoin", out var entryBase)
                        || !string.Equals(entryBase, normalizedBase, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (applyQuoteFilter
                        && !MatchesQuote(entry, normalizedQuote))
                    {
                        continue;
                    }

                    if (!entry.TryReadString("contractType", out var contractType)
                        || !contractType.Contains("Futures", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!entry.TryReadLong("deliveryTime", out var deliveryTime) || deliveryTime <= 0)
                    {
                        continue;
                    }

                    var expiration = ResolveDeliveryDate(deliveryTime);
                    if (expiration >= DateTime.UtcNow.Date)
                    {
                        results.Add(expiration);
                    }
                }
            }

            cursor = resultElement.TryReadString("nextPageCursor", out var nextCursor) && !string.IsNullOrWhiteSpace(nextCursor)
                ? nextCursor
                : null;
        } while (!string.IsNullOrWhiteSpace(cursor));

        if (results.Count == 0 && applyQuoteFilter)
        {
            return await FetchExpirationsAsync(baseAsset, null, cancellationToken);
        }

        return results
            .OrderBy(date => date)
            .Select(date => (DateTime?)date)
            .ToList();
    }

    private async Task<IReadOnlyList<ExchangeTradingPair>> FetchTradingPairsAsync(CancellationToken cancellationToken)
    {
        var pairs = new HashSet<ExchangeTradingPair>();
        string? cursor = null;

        do
        {
            var url = BuildInstrumentsUrl(_bybitSettingsOptions.Value.InstrumentsInfoUri, baseAsset: string.Empty, cursor: cursor, includeBase: false);
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Bybit instruments request failed ({(int)response.StatusCode}).");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            ThrowIfRetCodeError(root);

            if (!root.TryGetProperty("result", out var resultElement))
            {
                break;
            }

            if (resultElement.TryGetProperty("list", out var listElement) && listElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in listElement.EnumerateArray())
                {
                    if (!entry.TryReadString("baseCoin", out var baseCoin) || string.IsNullOrWhiteSpace(baseCoin))
                    {
                        continue;
                    }

                    if (!entry.TryReadString("settleCoin", out var settleCoin) || string.IsNullOrWhiteSpace(settleCoin))
                    {
                        continue;
                    }

                    if (!entry.TryReadString("status", out var status)
                        || !string.Equals(status, "Trading", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    pairs.Add(new ExchangeTradingPair(
                        baseCoin.Trim().ToUpperInvariant(),
                        settleCoin.Trim().ToUpperInvariant()));
                }
            }

            cursor = resultElement.TryReadString("nextPageCursor", out var nextCursor) && !string.IsNullOrWhiteSpace(nextCursor)
                ? nextCursor
                : null;
        } while (!string.IsNullOrWhiteSpace(cursor));

        return pairs
            .OrderBy(p => p.BaseAsset, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.QuoteAsset, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Uri BuildInstrumentsUrl(Uri instrumentsInfoUrl, string baseAsset, string? cursor, bool includeBase = true)
    {
        var builder = new List<string>
        {
            $"limit={PageSize.ToString(CultureInfo.InvariantCulture)}"
        };

        if (includeBase && !string.IsNullOrWhiteSpace(baseAsset))
        {
            builder.Add($"baseCoin={Uri.EscapeDataString(baseAsset)}");
        }

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            builder.Add($"cursor={Uri.EscapeDataString(cursor)}");
        }

        return new Uri($"{instrumentsInfoUrl}&{string.Join("&", builder)}");
    }

    private static bool MatchesQuote(JsonElement entry, string normalizedQuote)
    {
        if (entry.TryReadString("quoteCoin", out var quoteCoin)
            && string.Equals(quoteCoin, normalizedQuote, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return entry.TryReadString("settleCoin", out var settleCoin)
            && string.Equals(settleCoin, normalizedQuote, StringComparison.OrdinalIgnoreCase);
    }

    private static void ThrowIfRetCodeError(JsonElement root)
    {
        if (!root.TryReadInt("retCode", out var retCode) || retCode == 0)
        {
            return;
        }

        var message = root.TryReadString("retMsg", out var retMsg) ? retMsg : "Unknown error";
        throw new InvalidOperationException($"Bybit API error {retCode}: {message}");
    }

    private static DateTime ResolveDeliveryDate(long deliveryTime)
    {
        // Bybit uses milliseconds, but some responses may return seconds for deliveryTime.
        var timestamp = deliveryTime < 1_000_000_000_000L
            ? deliveryTime * 1000
            : deliveryTime;

        return DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime;
    }

}
