using System.Net.Http.Json;
using System.Text.Json;
using BlazorOptions.API.TradingHistory;

namespace BlazorOptions.Services;

public sealed class TradingHistoryPort : ITradingHistoryPort
{
    private readonly HttpClient _httpClient;
    private readonly Microsoft.Extensions.Options.IOptions<AuthSessionState> _sessionState;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public TradingHistoryPort(
        HttpClient httpClient,
        Microsoft.Extensions.Options.IOptions<AuthSessionState> sessionState)
    {
        _httpClient = httpClient;
        _sessionState = sessionState;
    }

    // Removed availability probing to avoid extra auth checks.

    public async Task<TradingHistoryMeta> LoadMetaAsync(string? exchangeConnectionId = null)
    {
        var response = await SendAsync(HttpMethod.Get, AppendExchangeConnectionId("api/trading-history/meta", exchangeConnectionId));
        return await response.Content.ReadFromJsonAsync<TradingHistoryMeta>(JsonOptions) ?? new TradingHistoryMeta();
    }

    public async Task SaveMetaAsync(TradingHistoryMeta meta, string? exchangeConnectionId = null)
    {
        await SendAsync(HttpMethod.Post, AppendExchangeConnectionId("api/trading-history/meta", exchangeConnectionId), meta);
    }


    public async Task SaveTradesAsync(IReadOnlyList<TradingHistoryEntry> entries, string? exchangeConnectionId = null)
    {
        await SendAsync(HttpMethod.Post, AppendExchangeConnectionId("api/trading-history/trades/bulk", exchangeConnectionId), entries);
    }

    public async Task<TradingHistoryResult> LoadEntriesAsync(string? baseAsset, int startIndex, int limit, string? exchangeConnectionId = null)
    {
        var query = new List<string> { $"startIndex={startIndex}", $"limit={limit}" };
        if (!string.IsNullOrWhiteSpace(baseAsset))
        {
            query.Add($"baseAsset={Uri.EscapeDataString(baseAsset)}");
        }
        if (!string.IsNullOrWhiteSpace(exchangeConnectionId))
        {
            query.Add($"exchangeConnectionId={Uri.EscapeDataString(exchangeConnectionId)}");
        }

        var response = await SendAsync(HttpMethod.Get, $"api/trading-history/entries?{string.Join("&", query)}");
        return await response.Content.ReadFromJsonAsync<TradingHistoryResult>(JsonOptions) ?? new TradingHistoryResult();
    }



    public async Task<IReadOnlyList<TradingHistoryEntry>> LoadBySymbolAsync(string symbol, string? category, long? sinceTimestamp, string? exchangeConnectionId = null)
    {
        var query = new List<string> { $"symbol={Uri.EscapeDataString(symbol)}" };
        if (!string.IsNullOrWhiteSpace(category))
        {
            query.Add($"category={Uri.EscapeDataString(category)}");
        }
        if (sinceTimestamp.HasValue)
        {
            query.Add($"sinceTimestamp={sinceTimestamp.Value}");
        }
        if (!string.IsNullOrWhiteSpace(exchangeConnectionId))
        {
            query.Add($"exchangeConnectionId={Uri.EscapeDataString(exchangeConnectionId)}");
        }

        var response = await SendAsync(HttpMethod.Get, $"api/trading-history/by-symbol?{string.Join("&", query)}");
        return await ReadListAsync(response);
    }

    public async Task<IReadOnlyList<TradingSummaryBySymbolRow>> LoadSummaryBySymbolAsync(string? exchangeConnectionId = null)
    {
        var response = await SendAsync(HttpMethod.Get, AppendExchangeConnectionId("api/trading-history/summary/by-symbol", exchangeConnectionId));
        var items = await response.Content.ReadFromJsonAsync<TradingSummaryBySymbolRow[]>(JsonOptions);
        return items ?? Array.Empty<TradingSummaryBySymbolRow>();
    }

    public async Task<IReadOnlyList<TradingPnlByCoinRow>> LoadPnlBySettleCoinAsync(string? exchangeConnectionId = null)
    {
        var response = await SendAsync(HttpMethod.Get, AppendExchangeConnectionId("api/trading-history/summary/by-settle-coin", exchangeConnectionId));
        var items = await response.Content.ReadFromJsonAsync<TradingPnlByCoinRow[]>(JsonOptions);
        return items ?? Array.Empty<TradingPnlByCoinRow>();
    }

    public async Task<IReadOnlyList<TradingDailyPnlRow>> LoadDailyPnlAsync(long fromTimestamp, long toTimestamp, string? exchangeConnectionId = null)
    {
        var response = await SendAsync(
            HttpMethod.Get,
            AppendExchangeConnectionId($"api/trading-history/daily-pnl?fromTimestamp={fromTimestamp}&toTimestamp={toTimestamp}", exchangeConnectionId));
        var items = await response.Content.ReadFromJsonAsync<TradingDailyPnlRow[]>(JsonOptions);
        return items ?? Array.Empty<TradingDailyPnlRow>();
    }

    public async Task<TradingHistoryLatestInfo> LoadLatestBySymbolMetaAsync(string symbol, string? category, string? exchangeConnectionId = null)
    {
        var query = new List<string> { $"symbol={Uri.EscapeDataString(symbol)}" };
        if (!string.IsNullOrWhiteSpace(category))
        {
            query.Add($"category={Uri.EscapeDataString(category)}");
        }
        if (!string.IsNullOrWhiteSpace(exchangeConnectionId))
        {
            query.Add($"exchangeConnectionId={Uri.EscapeDataString(exchangeConnectionId)}");
        }

        var response = await SendAsync(HttpMethod.Get, $"api/trading-history/latest-meta?{string.Join("&", query)}");
        var payload = await response.Content.ReadFromJsonAsync<TradingHistoryLatestInfo>(JsonOptions);
        return payload ?? new TradingHistoryLatestInfo();
    }

    public async Task RecalculateAsync(long? fromTimestamp, string? exchangeConnectionId = null)
    {
        var uri = "api/trading-history/recalculate";
        if (fromTimestamp.HasValue)
        {
            uri = $"{uri}?fromTimestamp={fromTimestamp.Value}";
        }

        uri = AppendExchangeConnectionId(uri, exchangeConnectionId);

        await SendAsync(HttpMethod.Post, uri);
    }

    private static string AppendExchangeConnectionId(string uri, string? exchangeConnectionId)
    {
        if (string.IsNullOrWhiteSpace(exchangeConnectionId))
        {
            return uri;
        }

        var separator = uri.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{uri}{separator}exchangeConnectionId={Uri.EscapeDataString(exchangeConnectionId)}";
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string uri, object? payload = null)
    {
        var request = new HttpRequestMessage(method, uri);
        var token = _sessionState.Value.Token;
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Add("X-User-Token", token);
        }

        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload, options: JsonOptions);
        }

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var problem = await ReadProblemDetailsAsync(response);
            if (problem is not null)
            {
                if (problem.Status == 401)
                {
                    throw new UnauthorizedAccessException(problem.Title ?? "Sign in to view trading history.");
                }

                throw new ProblemDetailsException(problem);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new UnauthorizedAccessException("Sign in to view trading history.");
            }

            var error = await ReadErrorAsync(response);
            throw new HttpRequestException(error ?? $"Request to '{uri}' failed.");
        }

        return response;
    }

    private static async Task<IReadOnlyList<TradingHistoryEntry>> ReadListAsync(HttpResponseMessage response)
    {
        var items = await response.Content.ReadFromJsonAsync<TradingHistoryEntry[]>(JsonOptions);
        return items ?? Array.Empty<TradingHistoryEntry>();
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
            if (payload is not null && payload.TryGetValue("error", out var error))
            {
                return error;
            }
        }
        catch
        {
            return null;
        }

        var text = await response.Content.ReadAsStringAsync();
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        return null;
    }

    private static async Task<ProblemDetails?> ReadProblemDetailsAsync(HttpResponseMessage response)
    {
        if (response.Content is null)
        {
            return null;
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        if (!contentType.Contains("application/problem+json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<ProblemDetails>(JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new SafeDecimalConverter());
        return options;
    }
}
