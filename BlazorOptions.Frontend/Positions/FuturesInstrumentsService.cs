using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace BlazorOptions.Services;

public sealed class FuturesInstrumentsService : IFuturesInstrumentsService
{
    private const string BybitInstrumentsInfoUrl = "https://api.bybit.com/v5/market/instruments-info?category=linear";
    private const int PageSize = 500;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, List<DateTime?>> _cachedExpirations = new(StringComparer.OrdinalIgnoreCase);

    public FuturesInstrumentsService(HttpClient httpClient)
    {
        _httpClient = httpClient;
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
            var url = BuildInstrumentsUrl(normalizedBase, cursor);
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
                    if (!TryReadString(entry, "baseCoin", out var entryBase)
                        || !string.Equals(entryBase, normalizedBase, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (applyQuoteFilter
                        && !MatchesQuote(entry, normalizedQuote))
                    {
                        continue;
                    }

                    if (!TryReadString(entry, "contractType", out var contractType)
                        || !contractType.Contains("Futures", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!TryReadLong(entry, "deliveryTime", out var deliveryTime) || deliveryTime <= 0)
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

            cursor = TryReadString(resultElement, "nextPageCursor", out var nextCursor) && !string.IsNullOrWhiteSpace(nextCursor)
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

    private static string BuildInstrumentsUrl(string baseAsset, string? cursor)
    {
        var builder = new List<string>
        {
            BybitInstrumentsInfoUrl,
            $"baseCoin={Uri.EscapeDataString(baseAsset)}",
            $"limit={PageSize.ToString(CultureInfo.InvariantCulture)}"
        };

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            builder.Add($"cursor={Uri.EscapeDataString(cursor)}");
        }

        return $"{BybitInstrumentsInfoUrl}&{string.Join("&", builder.Skip(1))}";
    }

    private static bool MatchesQuote(JsonElement entry, string normalizedQuote)
    {
        if (TryReadString(entry, "quoteCoin", out var quoteCoin)
            && string.Equals(quoteCoin, normalizedQuote, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return TryReadString(entry, "settleCoin", out var settleCoin)
            && string.Equals(settleCoin, normalizedQuote, StringComparison.OrdinalIgnoreCase);
    }

    private static void ThrowIfRetCodeError(JsonElement root)
    {
        if (!TryReadInt(root, "retCode", out var retCode) || retCode == 0)
        {
            return;
        }

        var message = TryReadString(root, "retMsg", out var retMsg) ? retMsg : "Unknown error";
        throw new InvalidOperationException($"Bybit API error {retCode}: {message}");
    }

    private static DateTime ResolveDeliveryDate(long deliveryTime)
    {
        // Bybit uses milliseconds, but some responses may return seconds for deliveryTime.
        var timestamp = deliveryTime < 1_000_000_000_000L
            ? deliveryTime * 1000
            : deliveryTime;

        return DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime.Date;
    }

    private static bool TryReadString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        value = property.ValueKind == JsonValueKind.String ? property.GetString() ?? string.Empty : property.GetRawText();
        return true;
    }

    private static bool TryReadInt(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value))
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return int.TryParse(property.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadLong(JsonElement element, string propertyName, out long value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out value))
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return long.TryParse(property.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }
}
