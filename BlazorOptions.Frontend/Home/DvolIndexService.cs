using System.Globalization;
using System.Text.Json;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class DvolIndexService
{
    private const string CacheKeyPrefix = "home.dvol.v1.";
    private static readonly TimeSpan Lookback = TimeSpan.FromDays(370);
    private static readonly TimeSpan AverageWindow = TimeSpan.FromDays(365);
    private const int ResolutionSeconds = 86400; // 1D timeframe

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILocalStorageService _localStorageService;

    public DvolIndexService(IHttpClientFactory httpClientFactory, ILocalStorageService localStorageService)
    {
        _httpClientFactory = httpClientFactory;
        _localStorageService = localStorageService;
    }

    public async Task<DvolChartData?> LoadCachedAsync(string? baseAsset)
    {
        var currency = NormalizeCurrency(baseAsset);
        if (string.IsNullOrWhiteSpace(currency))
        {
            return null;
        }

        var cacheKey = $"{CacheKeyPrefix}{currency}";
        var cached = await TryReadCacheAsync(cacheKey);
        return IsValid(cached?.Data) ? cached!.Data : null;
    }

    public async Task<DvolChartData?> RefreshChartAsync(string? baseAsset, CancellationToken cancellationToken = default)
    {
        var currency = NormalizeCurrency(baseAsset);
        if (string.IsNullOrWhiteSpace(currency))
        {
            return null;
        }

        var cacheKey = $"{CacheKeyPrefix}{currency}";

        try
        {
            var now = DateTimeOffset.UtcNow;
            var start = now - Lookback;
            var url =
                $"https://www.deribit.com/api/v2/public/get_volatility_index_data?currency={currency}&start_timestamp={start.ToUnixTimeMilliseconds()}&end_timestamp={now.ToUnixTimeMilliseconds()}&resolution={ResolutionSeconds}";
            var httpClient = _httpClientFactory.CreateClient("External");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            var points = ParsePoints(document);
            if (points.Count == 0)
            {
                return null;
            }

            var labels = BuildLabels(points, ResolutionSeconds);
            var candles = points
                .Select(point => new DvolCandlePoint(point.Open, point.Close, point.Low, point.High))
                .ToArray();
            var latest = points[^1].Close;
            var averageWindowStart = DateTimeOffset.UtcNow - AverageWindow;
            var averageSource = points
                .Where(point => DateTimeOffset.FromUnixTimeMilliseconds(point.Timestamp) >= averageWindowStart)
                .Select(point => point.Close)
                .ToArray();
            var averageLastYear = averageSource.Length > 0
                ? averageSource.Average()
                : points.Average(point => point.Close);
            var data = new DvolChartData(currency, labels, candles, latest, averageLastYear);
            await WriteCacheAsync(cacheKey, new DvolCacheEnvelope(DateTime.UtcNow, data));
            return data;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsValid(DvolChartData? data)
    {
        return data is not null &&
               data.Candles is not null &&
               data.Candles.Count > 0 &&
               data.XLabels is not null &&
               data.XLabels.Count == data.Candles.Count;
    }

    private static string? NormalizeCurrency(string? baseAsset)
    {
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            return null;
        }

        return baseAsset.Trim().ToUpperInvariant();
    }

    private async Task<DvolCacheEnvelope?> TryReadCacheAsync(string key)
    {
        try
        {
            var payload = await _localStorageService.GetItemAsync(key);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            return JsonSerializer.Deserialize<DvolCacheEnvelope>(payload);
        }
        catch
        {
            return null;
        }
    }

    private async Task WriteCacheAsync(string key, DvolCacheEnvelope envelope)
    {
        try
        {
            var payload = JsonSerializer.Serialize(envelope);
            await _localStorageService.SetItemAsync(key, payload);
        }
        catch
        {
            // Ignore storage failures so dashboard still renders with fresh in-memory data.
        }
    }

    private static List<DvolPoint> ParsePoints(JsonDocument document)
    {
        var result = new List<DvolPoint>();
        if (!document.RootElement.TryGetProperty("result", out var resultNode) ||
            !resultNode.TryGetProperty("data", out var dataNode) ||
            dataNode.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var row in dataNode.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 5)
            {
                continue;
            }

            var values = row.EnumerateArray().ToArray();
            if (!values[0].TryGetInt64(out var timestamp))
            {
                continue;
            }

            if (!TryReadDouble(values[1], out var open) ||
                !TryReadDouble(values[2], out var high) ||
                !TryReadDouble(values[3], out var low) ||
                !TryReadDouble(values[4], out var close))
            {
                continue;
            }

            result.Add(new DvolPoint(timestamp, open, high, low, close));
        }

        result.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
        return result;
    }

    private static bool TryReadDouble(JsonElement element, out double value)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetDouble(out value);
        }

        if (element.ValueKind == JsonValueKind.String &&
            double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        value = 0d;
        return false;
    }

    private static IReadOnlyList<string> BuildLabels(IReadOnlyList<DvolPoint> points, int resolutionSeconds)
    {
        if (points.Count == 0)
        {
            return Array.Empty<string>();
        }

        var labelFormat = resolutionSeconds >= 86400 ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm";
        return points
            .Select(point => DateTimeOffset.FromUnixTimeMilliseconds(point.Timestamp).ToLocalTime().ToString(labelFormat))
            .ToArray();
    }

    private sealed record DvolPoint(long Timestamp, double Open, double High, double Low, double Close);
    private sealed record DvolCacheEnvelope(DateTime CachedAtUtc, DvolChartData Data);
}

public sealed record DvolChartData(
    string BaseAsset,
    IReadOnlyList<string> XLabels,
    IReadOnlyList<DvolCandlePoint> Candles,
    double LatestValue,
    double AverageLastYear);

public sealed record DvolCandlePoint(
    double Open,
    double Close,
    double Low,
    double High);
