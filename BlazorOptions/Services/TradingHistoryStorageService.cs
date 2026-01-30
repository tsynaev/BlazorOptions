using BlazorOptions.ViewModels;
using System.Text.Json;
using Microsoft.JSInterop;

namespace BlazorOptions.Services;

public class TradingHistoryStorageService
{
    private readonly IJSRuntime _jsRuntime;
    private readonly ITelemetryService _telemetryService;
    private bool _isInitialized;

    public TradingHistoryStorageService(IJSRuntime jsRuntime, ITelemetryService telemetryService)
    {
        _jsRuntime = jsRuntime;
        _telemetryService = telemetryService;
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        try
        {
            await _jsRuntime.InvokeVoidAsync("tradingHistoryDb.init");
        }
        catch (JSException)
        {
            await EnsureScriptLoadedAsync();
            await _jsRuntime.InvokeVoidAsync("tradingHistoryDb.init");
        }
        _isInitialized = true;
    }

    public async Task<TradingHistoryMeta> LoadMetaAsync()
    {
        using var activity = _telemetryService.StartActivity("TradingHistoryStorageService.LoadMetaAsync");
        await InitializeAsync();
        var element = await _jsRuntime.InvokeAsync<JsonElement>("tradingHistoryDb.getMeta");
        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
        {
            return new TradingHistoryMeta();
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (!element.TryGetProperty("value", out var valueElement))
            {
                element.TryGetProperty("Value", out valueElement);
            }

            if (valueElement.ValueKind != JsonValueKind.Undefined)
            {
                return DeserializeMetaElement(valueElement);
            }
        }

        return DeserializeMetaElement(element);
    }

    private static TradingHistoryMeta DeserializeMetaElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var payload = element.GetString();
            if (!string.IsNullOrWhiteSpace(payload))
            {
                return JsonSerializer.Deserialize<TradingHistoryMeta>(payload, CreateSerializerOptions()) ?? new TradingHistoryMeta();
            }
        }

        return element.Deserialize<TradingHistoryMeta>(CreateSerializerOptions()) ?? new TradingHistoryMeta();
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }

    public async Task SaveMetaAsync(TradingHistoryMeta meta)
    {
        using var activity = _telemetryService.StartActivity("TradingHistoryStorageService.SaveMetaAsync");
        await InitializeAsync();
        await _jsRuntime.InvokeVoidAsync("tradingHistoryDb.setMeta", meta);
    }

    public async Task SaveDailySummariesAsync(IEnumerable<TradingDailySummary> summaries)
    {
        using var activity = _telemetryService.StartActivity("TradingHistoryStorageService.SaveDailySummariesAsync");
        await InitializeAsync();
        var payload = summaries.ToArray();
        if (payload.Length == 0)
        {
            return;
        }

        await _jsRuntime.InvokeAsync<int>("tradingHistoryDb.replaceDailySummaries", (object)payload);
    }

    public async Task<int> GetCountAsync()
    {
        using var activity = _telemetryService.StartActivity("TradingHistoryStorageService.GetCountAsync");
        await InitializeAsync();
        return await _jsRuntime.InvokeAsync<int>("tradingHistoryDb.getCount");
    }

    public async Task SaveTradesAsync(IEnumerable<TradingHistoryEntry> entries)
    {
        using var activity = _telemetryService.StartActivity("TradingHistoryStorageService.SaveTradesAsync");
        await InitializeAsync();
        var payload = entries.ToArray();
        if (payload.Length == 0)
        {
            return;
        }

        var written = await _jsRuntime.InvokeAsync<int>("tradingHistoryDb.putTrades", (object)payload);
        if (written == 0)
        {
            var jsError = await _jsRuntime.InvokeAsync<string?>("tradingHistoryDb.getLastError");
            Console.WriteLine($"TradingHistoryStorageService: No trades written (payload {payload.Length}). JS error: {jsError ?? "none"}");
        }
    }

    public async Task<IReadOnlyList<TradingHistoryEntry>> LoadLatestAsync(int limit)
    {
        using var activity = _telemetryService.StartActivity("TradingHistoryStorageService.LoadLatestAsync");
        await InitializeAsync();
        return await _jsRuntime.InvokeAsync<TradingHistoryEntry[]>("tradingHistoryDb.fetchLatest", limit);
    }

    public async Task<IReadOnlyList<TradingHistoryEntry>> LoadAnyAsync(int limit)
    {
        using var activity = _telemetryService.StartActivity("TradingHistoryStorageService.LoadAnyAsync");
        await InitializeAsync();
        return await _jsRuntime.InvokeAsync<TradingHistoryEntry[]>("tradingHistoryDb.fetchAny", limit);
    }

    public async Task<IReadOnlyList<TradingHistoryEntry>> LoadBeforeAsync(long? beforeTimestamp, string? beforeKey, int limit)
    {
        using var activity = _telemetryService.StartActivity("TradingHistoryStorageService.LoadBeforeAsync");
        await InitializeAsync();
        return await _jsRuntime.InvokeAsync<TradingHistoryEntry[]>("tradingHistoryDb.fetchBefore", beforeTimestamp, beforeKey, limit);
    }

    public async Task<IReadOnlyList<TradingHistoryEntry>> LoadBySymbolAsync(string symbol, string? category = null)
    {
        using var activity = _telemetryService.StartActivity("TradingHistoryStorageService.LoadBySymbolAsync");
        await InitializeAsync();
        var symbolKey = NormalizeSymbolKey(symbol);
        var categoryKey = NormalizeCategoryKey(category);
        return await _jsRuntime.InvokeAsync<TradingHistoryEntry[]>("tradingHistoryDb.fetchBySymbol", symbolKey, categoryKey);
    }

    public async Task<IReadOnlyList<TradingHistoryEntry>> LoadBySymbolSinceAsync(string symbol, long? sinceTimestamp, string? category = null)
    {
        using var activity = _telemetryService.StartActivity("TradingHistoryStorageService.LoadBySymbolSinceAsync");
        await InitializeAsync();
        var symbolKey = NormalizeSymbolKey(symbol);
        var categoryKey = NormalizeCategoryKey(category);
        var since = sinceTimestamp ?? 0;
        return await _jsRuntime.InvokeAsync<TradingHistoryEntry[]>("tradingHistoryDb.fetchBySymbolSince", symbolKey, categoryKey, since);
    }

    public async Task<IReadOnlyList<TradingHistoryEntry>> LoadBySymbolSummaryAsync(string symbol, string? category = null)
    {
        using var activity = _telemetryService.StartActivity("TradingHistoryStorageService.LoadBySymbolSummaryAsync");
        await InitializeAsync();
        var symbolKey = NormalizeSymbolKey(symbol);
        var categoryKey = NormalizeCategoryKey(category);
        return await _jsRuntime.InvokeAsync<TradingHistoryEntry[]>("tradingHistoryDb.fetchBySymbolSummary", symbolKey, categoryKey);
    }

    public async Task<IReadOnlyList<TradingHistoryEntry>> LoadBySymbolSummarySinceAsync(string symbol, long? sinceTimestamp, string? category = null)
    {
        using var activity = _telemetryService.StartActivity("TradingHistoryStorageService.LoadBySymbolSummarySinceAsync");
        await InitializeAsync();
        var symbolKey = NormalizeSymbolKey(symbol);
        var categoryKey = NormalizeCategoryKey(category);
        var since = sinceTimestamp ?? 0;
        return await _jsRuntime.InvokeAsync<TradingHistoryEntry[]>("tradingHistoryDb.fetchBySymbolSummarySince", symbolKey, categoryKey, since);
    }

    public async Task<IReadOnlyList<TradingHistoryEntry>> LoadAllAscAsync()
    {
        using var activity = _telemetryService.StartActivity("TradingHistoryStorageService.LoadAllAscAsync");
        await InitializeAsync();
        return await _jsRuntime.InvokeAsync<TradingHistoryEntry[]>("tradingHistoryDb.fetchAllAsc");
    }
 
    private static string NormalizeSymbolKey(string? symbol)
    {
        return string.IsNullOrWhiteSpace(symbol)
            ? string.Empty
            : symbol.Trim().ToUpperInvariant();
    }

    private static string? NormalizeCategoryKey(string? category)
    {
        return string.IsNullOrWhiteSpace(category)
            ? null
            : category.Trim().ToLowerInvariant();
    }

    private async Task EnsureScriptLoadedAsync()
    {
        const string ensureScript = """
            window.__ensureTradingHistoryDb = window.__ensureTradingHistoryDb || (function () {
                return function () {
                    if (window.tradingHistoryDb) {
                        return true;
                    }
                    return new Promise(function (resolve, reject) {
                        var script = document.createElement('script');
                        script.src = 'js/trading-history-db.js?v=5';
                        script.async = false;
                        script.onload = function () { resolve(true); };
                        script.onerror = function () { reject('Failed to load trading-history-db.js'); };
                        document.head.appendChild(script);
                    });
                };
            })();
            """;

        await _jsRuntime.InvokeVoidAsync("eval", ensureScript);
        await _jsRuntime.InvokeAsync<bool>("__ensureTradingHistoryDb");
    }

 
}

