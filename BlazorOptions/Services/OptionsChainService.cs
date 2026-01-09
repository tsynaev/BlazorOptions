using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlazorOptions.ViewModels;

namespace BlazorOptions.Services;

public class OptionsChainService
{
    private const string BybitOptionsTickerUrl = "https://api.bybit.com/v5/market/tickers?category=option";
    private const string BybitOptionsDocsUrl = "https://bybit-exchange.github.io/docs/v5/market/tickers";
    private static readonly string[] ExpirationFormats = { "ddMMMyy", "ddMMMyyyy" };
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _cacheLock = new();
    private List<OptionChainTicker> _cachedTickers = new();
    private HashSet<string> _trackedSymbols = new(StringComparer.OrdinalIgnoreCase);

    public OptionsChainService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public DateTime? LastUpdatedUtc { get; private set; }

    public bool IsRefreshing { get; private set; }

    public event Action? ChainUpdated;

    public IReadOnlyList<OptionChainTicker> GetSnapshot()
    {
        lock (_cacheLock)
        {
            return _cachedTickers.ToList();
        }
    }

    public IReadOnlyCollection<string> GetTrackedSymbols()
    {
        lock (_cacheLock)
        {
            return _trackedSymbols.ToList();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!await _refreshLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            IsRefreshing = true;
            var updated = await FetchTickersAsync(cancellationToken);

            if (updated.Count > 0)
            {
                lock (_cacheLock)
                {
                    _cachedTickers = updated;
                }

                LastUpdatedUtc = DateTime.UtcNow;
                ChainUpdated?.Invoke();
            }
        }
        finally
        {
            IsRefreshing = false;
            _refreshLock.Release();
        }
    }

    public OptionChainTicker? FindTickerForLeg(OptionLegModel leg, string? baseAsset)
    {
        var snapshot = GetSnapshot();

        if (!string.IsNullOrWhiteSpace(leg.ChainSymbol))
        {
            var symbolMatch = snapshot.FirstOrDefault(ticker => string.Equals(ticker.Symbol, leg.ChainSymbol, StringComparison.OrdinalIgnoreCase));
            if (symbolMatch is not null)
            {
                return symbolMatch;
            }
        }

        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            return null;
        }

        return snapshot.FirstOrDefault(ticker =>
            string.Equals(ticker.BaseAsset, baseAsset, StringComparison.OrdinalIgnoreCase)
            && ticker.Type == leg.Type
            && ticker.ExpirationDate.Date == leg.ExpirationDate.Date
            && Math.Abs(ticker.Strike - leg.Strike) < 0.01);
    }

    public void TrackLegs(IEnumerable<OptionLegModel> legs, string? baseAsset)
    {
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var leg in legs)
        {
            if (!string.IsNullOrWhiteSpace(leg.ChainSymbol))
            {
                symbols.Add(leg.ChainSymbol);
                continue;
            }

            var ticker = FindTickerForLeg(leg, baseAsset);
            if (ticker is not null)
            {
                symbols.Add(ticker.Symbol);
            }
        }

        lock (_cacheLock)
        {
            _trackedSymbols = symbols;
        }
    }

    private async Task<List<OptionChainTicker>> FetchTickersAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(BybitOptionsTickerUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return await FetchTickersFromDocumentationAsync(cancellationToken);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            return ParseTickersFromDocument(document);
        }
        catch
        {
            return await FetchTickersFromDocumentationAsync(cancellationToken);
        }
    }

    private static List<OptionChainTicker> ParseTickersFromDocument(JsonDocument? document)
    {
        if (document is null)
        {
            return new List<OptionChainTicker>();
        }

        if (!document.RootElement.TryGetProperty("result", out var resultElement))
        {
            return new List<OptionChainTicker>();
        }

        if (!resultElement.TryGetProperty("list", out var listElement) || listElement.ValueKind != JsonValueKind.Array)
        {
            return new List<OptionChainTicker>();
        }

        var tickers = new List<OptionChainTicker>();

        foreach (var entry in listElement.EnumerateArray())
        {
            if (!TryReadString(entry, "symbol", out var symbol))
            {
                continue;
            }

            if (!TryParseSymbol(symbol, out var baseAsset, out var expirationDate, out var strike, out var type))
            {
                continue;
            }

            var markPrice = ReadDouble(entry, "markPrice");
            var markIv = ReadDouble(entry, "markIv");
            var delta = ReadNullableDouble(entry, "delta");
            var gamma = ReadNullableDouble(entry, "gamma");
            var vega = ReadNullableDouble(entry, "vega");
            var theta = ReadNullableDouble(entry, "theta");

            tickers.Add(new OptionChainTicker(
                symbol,
                baseAsset,
                expirationDate,
                strike,
                type,
                markPrice,
                markIv,
                delta,
                gamma,
                vega,
                theta));
        }

        return tickers;
    }

    private async Task<List<OptionChainTicker>> FetchTickersFromDocumentationAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(BybitOptionsDocsUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new List<OptionChainTicker>();
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var codeBlocks = Regex.Matches(html, "<code[^>]*>(.*?)</code>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

            foreach (Match match in codeBlocks)
            {
                var rawBlock = match.Groups[1].Value;
                var withoutTags = Regex.Replace(rawBlock, "<[^>]+>", string.Empty);
                var decoded = WebUtility.HtmlDecode(withoutTags);

                if (!decoded.Contains("\"category\"", StringComparison.OrdinalIgnoreCase) || !decoded.Contains("\"option\"", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var document = TryParseJson(decoded);
                if (document is null)
                {
                    continue;
                }

                using (document)
                {
                    var tickers = ParseTickersFromDocument(document);
                    if (tickers.Count > 0)
                    {
                        return tickers;
                    }
                }
            }

            return new List<OptionChainTicker>();
        }
        catch
        {
            return new List<OptionChainTicker>();
        }
    }

    private static JsonDocument? TryParseJson(string content)
    {
        try
        {
            return JsonDocument.Parse(content);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseSymbol(string symbol, out string baseAsset, out DateTime expiration, out double strike, out OptionLegType type)
    {
        baseAsset = string.Empty;
        expiration = default;
        strike = 0;
        type = OptionLegType.Call;

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

        if (!double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out strike))
        {
            return false;
        }

        var typeToken = parts[3].Trim();
        type = typeToken.Equals("P", StringComparison.OrdinalIgnoreCase) ? OptionLegType.Put : OptionLegType.Call;
        return true;
    }

    private static bool TryReadString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;

        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        value = property.ValueKind == JsonValueKind.String ? property.GetString() ?? string.Empty : property.GetRawText();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static double ReadDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        var raw = property.ValueKind == JsonValueKind.String ? property.GetString() : property.GetRawText();

        return double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static double? ReadNullableDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        var raw = property.ValueKind == JsonValueKind.String ? property.GetString() : property.GetRawText();
        if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return null;
    }
}
